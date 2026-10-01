using System.Diagnostics;

namespace XinSpect;

public interface ISmtContentionEngine
{
    Task<SmtContentionMeasurement> MeasureAsync(
        SmtContentionContext context,
        CancellationToken cancellationToken);
}

public sealed record SmtContentionSettings(
    int OperationsPerWorker,
    int WarmupRounds,
    int MeasureRounds);

public sealed record SmtContentionContext(
    IReadOnlyList<IReadOnlyList<ProcessorRef>> PhysicalCores,
    DeepBenchRunProfile Profile,
    SmtContentionSettings Settings,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public sealed record SmtContentionPlan(
    ProcessorRef Single,
    ProcessorRef IndependentFirst,
    ProcessorRef IndependentSecond,
    ProcessorRef SiblingFirst,
    ProcessorRef SiblingSecond);

public sealed record SmtContentionMeasurement(
    IReadOnlyList<double> SingleSamples,
    IReadOnlyList<double> IndependentSamples,
    IReadOnlyList<double> SiblingSamples,
    ProcessorRef SingleProcessor,
    ProcessorRef IndependentFirst,
    ProcessorRef IndependentSecond,
    ProcessorRef SiblingFirst,
    ProcessorRef SiblingSecond);

public sealed class SmtContentionValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// SMT sibling 干擾：比較單執行緒、兩顆獨立實體核心、同一實體核心兩條 SMT 執行緒的
/// pinned managed workload 合併吞吐。量的是本程式可觀察的資源干擾，不是硬體計數器歸因。
/// </summary>
public sealed class SmtContentionService(
    Func<IReadOnlyList<IReadOnlyList<ProcessorRef>>>? physicalCoreProvider = null,
    ISmtContentionEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "topology.smt-contention";

    public static string[] Limitations { get; } =
    [
        "量的是本程式 pinned managed workload 在三種放置下的合併吞吐，不是 CPU 規格值，也不外推不同指令混合的絕對損耗。",
        "不直接讀取 SMT 硬體計數器；比率反映可觀察的執行資源、快取與排程干擾，不是 SMT 硬體計數器歸因。",
        "使用者模式親和性受行程 affinity、處理器群組、電源與系統排程影響；多處理器群組路徑未在實機全面驗證。",
        "混合架構下獨立對照可能落在 P/E 不同族群；結果只代表實際選點，不宣稱同質核心。",
    ];

    private readonly Func<IReadOnlyList<IReadOnlyList<ProcessorRef>>> _physicalCoreProvider =
        physicalCoreProvider ?? DefaultPhysicalCoreProvider;
    private readonly ISmtContentionEngine _engine = engine ?? new WindowsSmtContentionEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "列舉實體核心與 SMT 兄弟"));
        IReadOnlyList<IReadOnlyList<ProcessorRef>> cores = _physicalCoreProvider();
        SmtContentionPlan? plan = SelectPlan(cores);
        if (plan is null)
            return Unsupported(context, started);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SmtContentionSettings settings = GetSettings(context.Profile);
            var engineContext = new SmtContentionContext(
                cores,
                context.Profile,
                settings,
                context.Progress,
                cancellationToken);
            SmtContentionMeasurement measurement = await _engine
                .MeasureAsync(engineContext, cancellationToken)
                .ConfigureAwait(false);
            ValidateMeasurement(measurement);
            return CreateResult(context, started, settings, measurement);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (SmtContentionValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static SmtContentionSettings GetSettings(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(1_500_000, 1, 8),
        DeepBenchRunProfile.Full => new(5_000_000, 2, 24),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    internal static SmtContentionPlan? SelectPlan(IReadOnlyList<IReadOnlyList<ProcessorRef>> physicalCores)
    {
        if (physicalCores.Count < 2)
            return null;

        int siblingIndex = -1;
        for (int index = 0; index < physicalCores.Count; index++)
        {
            if (physicalCores[index].Count >= 2)
            {
                siblingIndex = index;
                break;
            }
        }

        if (siblingIndex < 0)
            return null;

        IReadOnlyList<ProcessorRef> siblingCore = physicalCores[siblingIndex];
        List<IReadOnlyList<ProcessorRef>> independentCores = [.. physicalCores
            .Where((core, index) => index != siblingIndex && core.Count > 0)];
        if (independentCores.Count < 2)
            return null;

        IReadOnlyList<ProcessorRef> first = independentCores[0];
        IReadOnlyList<ProcessorRef> second = independentCores[1];
        if (first.Count == 0 || second.Count == 0 || first[0].Equals(second[0]))
            return null;

        return new SmtContentionPlan(
            siblingCore[0],
            first[0],
            second[0],
            siblingCore[0],
            siblingCore[1]);
    }

    private static IReadOnlyList<IReadOnlyList<ProcessorRef>> DefaultPhysicalCoreProvider()
    {
        bool multiGroup = CpuAffinity.IsMultiGroup;
        ulong mask = ulong.MaxValue;
        if (!multiGroup)
        {
            try { mask = (ulong)Process.GetCurrentProcess().ProcessorAffinity.ToInt64(); }
            catch { mask = ulong.MaxValue; }
        }

        return CpuAffinity.PhysicalCoreProcessorSets(multiGroup, mask);
    }

    private static void ValidateMeasurement(SmtContentionMeasurement measurement)
    {
        int expected = Math.Min(measurement.SingleSamples.Count, Math.Min(
            measurement.IndependentSamples.Count,
            measurement.SiblingSamples.Count));
        if (measurement.SingleSamples.Count != expected
            || measurement.IndependentSamples.Count != expected
            || measurement.SiblingSamples.Count != expected)
            throw new SmtContentionValidationException("三種情境樣本數不一致；不推算缺失樣本。");
        if (expected == 0)
            throw new SmtContentionValidationException("沒有任何 SMT contention 樣本；不輸出空結果。");
        if (!measurement.SingleProcessor.Equals(measurement.SiblingFirst))
            throw new SmtContentionValidationException("單執行緒基準與 SMT 對照第一點必須相同。");
        if (measurement.SiblingFirst.Equals(measurement.SiblingSecond))
            throw new SmtContentionValidationException("SMT 對照不可使用相同邏輯處理器。");
        if (measurement.IndependentFirst.Equals(measurement.IndependentSecond))
            throw new SmtContentionValidationException("獨立對照不可使用相同邏輯處理器。");
        if (!IsFinitePositive(measurement.SingleSamples)
            || !IsFinitePositive(measurement.IndependentSamples)
            || !IsFinitePositive(measurement.SiblingSamples))
            throw new SmtContentionValidationException("SMT 吞吐出現非有限或非正數樣本；整場拒收。");
    }

    private static bool IsFinitePositive(IReadOnlyList<double> samples) =>
        samples.All(value => double.IsFinite(value) && value > 0);

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        SmtContentionSettings settings,
        SmtContentionMeasurement measurement)
    {
        bool multiGroup = CpuAffinity.IsMultiGroup;
        double singleMedian = Median(measurement.SingleSamples);
        double independentMedian = Median(measurement.IndependentSamples);
        double siblingMedian = Median(measurement.SiblingSamples);
        double independentScaling = independentMedian / (2d * singleMedian);
        double siblingEfficiency = siblingMedian / (2d * singleMedian);
        string independentText = $"LP{measurement.IndependentFirst.Index} + LP{measurement.IndependentSecond.Index}";
        string siblingText = $"LP{measurement.SiblingFirst.Index} + LP{measurement.SiblingSecond.Index}";
        string configuration =
            $"{settings.MeasureRounds} 量測回；每 worker {settings.OperationsPerWorker / 1_000_000.0:0.#}M ops；" +
            $"獨立 {independentText}；SMT {siblingText}";

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            configuration,
            [
                Metric("topology.smt.single.mops", "Single-thread baseline", "Mops/s", measurement.SingleSamples, []),
                Metric("topology.smt.independent.mops", "Independent-core combined", "Mops/s", measurement.IndependentSamples, []),
                Metric("topology.smt.sibling.mops", "SMT sibling combined", "Mops/s", measurement.SiblingSamples, []),
                Metric("topology.smt.independent-scaling.ratio", "Independent scaling", "ratio", [independentScaling], []),
                Metric("topology.smt.sibling-efficiency.ratio", "SMT efficiency", "ratio", [siblingEfficiency], []),
            ],
            [
                $"實測 {measurement.SingleSamples.Count} 回；基準 LP{measurement.SingleProcessor.Index}；獨立 {independentText}；SMT {siblingText}。",
                $"中位數比率：independent scaling {independentScaling:0.###}；sibling efficiency {siblingEfficiency:0.###}。",
                multiGroup ? "偵測到多處理器群組；使用明確 thread group affinity，但此路徑未在實機全面驗證。" : "使用使用者模式 thread affinity；不讀 MSR、不載入驅動。",
            ],
            Limitations,
            DeepBenchFailureKind.None,
            null);
    }

    private static DeepBenchMetric Metric(
        string id,
        string name,
        string unit,
        IReadOnlyList<double> samples,
        IReadOnlyList<DeepBenchMetricPoint> points) =>
        new(id, name, unit, true, "pinned managed integer/fixed-point workload", samples, points);

    private static double Median(IReadOnlyList<double> samples)
    {
        double[] sorted = [.. samples];
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2d;
    }

    private static DeepBenchTestResult Unsupported(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [],
            ["需要至少兩顆實體核心，且其中一顆有至少兩條 SMT 執行緒。"],
            DeepBenchFailureKind.Unsupported, "缺少 SMT 兄弟或第二顆實體核心。");

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消後不輸出部分情境補值。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["量測不完整時不推算缺失情境。"], kind, error);
}

/// <summary>Windows 使用者模式實作；所有 worker 明確釘選，不碰 MSR 或驅動。</summary>
public sealed class WindowsSmtContentionEngine : ISmtContentionEngine
{
    public Task<SmtContentionMeasurement> MeasureAsync(
        SmtContentionContext context,
        CancellationToken cancellationToken)
        => Task.Run(() => Measure(context, cancellationToken), cancellationToken);

    private static SmtContentionMeasurement Measure(
        SmtContentionContext context,
        CancellationToken cancellationToken)
    {
        SmtContentionPlan? plan = SmtContentionService.SelectPlan(context.PhysicalCores)
            ?? throw new SmtContentionValidationException("engine 收到不完整的 SMT 拓樸。");
        var samples = (SingleSamples: new List<double>(), IndependentSamples: new List<double>(), SiblingSamples: new List<double>());
        int totalRounds = context.Settings.WarmupRounds + context.Settings.MeasureRounds;

        for (int round = 0; round < totalRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isWarmup = round < context.Settings.WarmupRounds;
            int completed = samples.SingleSamples.Count + samples.IndependentSamples.Count + samples.SiblingSamples.Count;
            int total = context.Settings.MeasureRounds * 3;
            context.Progress.Report(new DeepBenchProgress(
                SmtContentionService.TestId,
                completed,
                total,
                0.05 + 0.93 * completed / total,
                isWarmup ? $"warmup {round + 1}/{totalRounds}" : $"量測 {samples.SingleSamples.Count + 1}/{context.Settings.MeasureRounds}"));

            double single = RunWorkers(
                [plan.Single],
                context.Settings,
                cancellationToken);
            double independent = RunWorkers(
                [plan.IndependentFirst, plan.IndependentSecond],
                context.Settings,
                cancellationToken);
            double sibling = RunWorkers(
                [plan.SiblingFirst, plan.SiblingSecond],
                context.Settings,
                cancellationToken);
            if (isWarmup) continue;

            samples.SingleSamples.Add(single);
            samples.IndependentSamples.Add(independent);
            samples.SiblingSamples.Add(sibling);
        }

        context.Progress.Report(new DeepBenchProgress(
            SmtContentionService.TestId,
            context.Settings.MeasureRounds * 3,
            context.Settings.MeasureRounds * 3,
            0.98,
            $"SMT 干擾完成；{context.Settings.MeasureRounds} 回"));
        return new SmtContentionMeasurement(
            samples.SingleSamples,
            samples.IndependentSamples,
            samples.SiblingSamples,
            plan.Single,
            plan.IndependentFirst,
            plan.IndependentSecond,
            plan.SiblingFirst,
            plan.SiblingSecond);
    }

    private static double RunWorkers(
        IReadOnlyList<ProcessorRef> processors,
        SmtContentionSettings settings,
        CancellationToken cancellationToken)
    {
        ManualResetEventSlim? startGate = null;
        try
        {
            using CountdownEvent ready = new(processors.Count + 1);
            using CountdownEvent finished = new(processors.Count + 1);
            startGate = new ManualResetEventSlim(false);
            var workers = processors
                .Select(processor => new PinnedComputeWorker(
                    processor,
                    settings.OperationsPerWorker,
                    ready,
                    finished,
                    startGate,
                    cancellationToken))
                .ToArray();
            var threads = workers
                .Select((worker, index) => new Thread(worker.Run)
                {
                    IsBackground = true,
                    Name = $"XinSpect SMT worker {index} G{processors[index].Group}LP{processors[index].Index}",
                })
                .ToArray();
            foreach (Thread thread in threads) thread.Start();
            ready.Signal();
            ready.Wait(cancellationToken);
            if (workers.Any(worker => worker.PinFailed))
            {
                startGate.Set();
                foreach (Thread thread in threads) thread.Join();
                throw new SmtContentionValidationException("無法完成使用者模式親和性釘選；不偽裝成 pinned SMT 量測。");
            }

            long timestamp = Stopwatch.GetTimestamp();
            startGate.Set();
            finished.Signal();
            finished.Wait(cancellationToken);
            foreach (Thread thread in threads) thread.Join();
            cancellationToken.ThrowIfCancellationRequested();
            if (workers.Any(worker => worker.Error is not null))
                throw workers.First(worker => worker.Error is not null).Error!;

            double elapsedSeconds = Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
            if (elapsedSeconds <= 0)
                throw new SmtContentionValidationException("SMT 計時時間異常。");
            double throughput = processors.Count * settings.OperationsPerWorker / elapsedSeconds / 1_000_000d;
            if (!double.IsFinite(throughput) || throughput <= 0)
                throw new SmtContentionValidationException("SMT 合併吞吐非有限或非正數。");
            return throughput;
        }
        finally
        {
            startGate?.Set();
        }
    }

    private sealed class PinnedComputeWorker(
        ProcessorRef processor,
        long operations,
        CountdownEvent ready,
        CountdownEvent finished,
        ManualResetEventSlim startGate,
        CancellationToken cancellationToken)
    {
        public Exception? Error { get; private set; }
        public bool PinFailed { get; private set; }

        public void Run()
        {
            using CpuAffinity.Pin pin = CpuAffinity.Pinned(processor);
            PinFailed = !pin.Ok;
            ready.Signal();
            try
            {
                startGate.Wait(cancellationToken);
                if (!pin.Ok) return;

                Workload(operations, 17);
                finished.Signal();
            }
            catch (Exception exception)
            {
                Error = exception;
                finished.Signal();
            }
        }

        private static long Workload(long operations, long seed)
        {
            long a = seed;
            long b = seed * 3 + 1;
            long c = seed * 5 + 2;
            long d = seed * 7 + 3;
            long count = operations / 4;
            for (long index = 1; index <= count; index++)
            {
                a = (a * 6364136223846793005L + index) ^ (b >> 7);
                b = (b * 2862933555777941757L + index) ^ (c >> 5);
                c = (c * 1442695040888963407L + index) ^ (d >> 3);
                d = (d * 1103515245125851171L + index) ^ (a >> 11);
            }

            return a ^ b ^ c ^ d;
        }
    }
}
