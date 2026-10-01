using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XinSpect;

public interface ICoherenceLockEngine
{
    Task<CoherenceLockMeasurement> MeasureAsync(
        CoherenceLockContext context,
        CancellationToken cancellationToken);
}

public sealed record CoherenceLockSettings(
    int OperationsPerWorker,
    int WarmupRounds,
    int MeasureRounds);

public sealed record CoherenceLockContext(
    IReadOnlyList<IReadOnlyList<ProcessorRef>> PhysicalCores,
    DeepBenchRunProfile Profile,
    CoherenceLockSettings Settings,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public sealed record CoherenceLockPlan(
    ProcessorRef FirstCore,
    ProcessorRef SecondCore);

public sealed record CoherenceLockMeasurement(
    IReadOnlyList<double> IndependentLineSamples,
    IReadOnlyList<double> FalseSharingSamples,
    IReadOnlyList<double> LockSingleSamples,
    IReadOnlyList<double> LockTwoSamples,
    ProcessorRef FirstCore,
    ProcessorRef SecondCore);

public sealed class CoherenceLockValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// Cache coherence / lock scaling：比較 pinned managed atomic workload 在分離 cache line
/// 與同一 cache line 鄰接欄位的合併吞吐，並量測同一 lock critical section 在一／兩個
/// 實體核心的 scaling。量的是可觀察吞吐，不是硬體一致性計數器。
/// </summary>
public sealed class CoherenceLockService(
    Func<IReadOnlyList<IReadOnlyList<ProcessorRef>>>? physicalCoreProvider = null,
    ICoherenceLockEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "topology.coherence-lock";

    public static string[] Limitations { get; } =
    [
        "量的是本程式 pinned managed atomic/load-store workload 的合併吞吐對照，不是硬體快取一致性計數器，也不外推其他資料大小或存取圖樣。",
        "lock scaling 量的是本程式 .NET Monitor lock critical section 的可觀察吞吐；不是通用鎖理論上限，也不外推不同臨界區長度。",
        "使用者模式親和性受行程 affinity、處理器群組、電源與系統排程影響；多處理器群組路徑未在實機全面驗證。",
        "混合架構下兩個對照點可能落在 P/E 不同族群；結果只代表實際選點，不宣稱同質核心。",
    ];

    private readonly Func<IReadOnlyList<IReadOnlyList<ProcessorRef>>> _physicalCoreProvider =
        physicalCoreProvider ?? DefaultPhysicalCoreProvider;
    private readonly ICoherenceLockEngine _engine = engine ?? new WindowsCoherenceLockEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "列舉兩顆獨立實體核心"));
        IReadOnlyList<IReadOnlyList<ProcessorRef>> cores = _physicalCoreProvider();
        CoherenceLockPlan? plan = SelectPlan(cores);
        if (plan is null)
            return Unsupported(context, started);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CoherenceLockSettings settings = GetSettings(context.Profile);
            var engineContext = new CoherenceLockContext(
                cores,
                context.Profile,
                settings,
                context.Progress,
                cancellationToken);
            CoherenceLockMeasurement measurement = await _engine
                .MeasureAsync(engineContext, cancellationToken)
                .ConfigureAwait(false);
            ValidateMeasurement(measurement);
            return CreateResult(context, started, settings, measurement);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (CoherenceLockValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static CoherenceLockSettings GetSettings(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(250_000, 1, 8),
        DeepBenchRunProfile.Full => new(1_000_000, 2, 24),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    internal static CoherenceLockPlan? SelectPlan(IReadOnlyList<IReadOnlyList<ProcessorRef>> physicalCores)
    {
        List<IReadOnlyList<ProcessorRef>> cores = [.. physicalCores.Where(core => core.Count > 0)];
        if (cores.Count < 2)
            return null;

        ProcessorRef first = cores[0][0];
        ProcessorRef second = cores[1][0];
        if (first.Equals(second))
            return null;
        return new CoherenceLockPlan(first, second);
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

    private static void ValidateMeasurement(CoherenceLockMeasurement measurement)
    {
        int expected = measurement.IndependentLineSamples.Count;
        if (measurement.FalseSharingSamples.Count != expected
            || measurement.LockSingleSamples.Count != expected
            || measurement.LockTwoSamples.Count != expected)
            throw new CoherenceLockValidationException("四種情境樣本數不一致；不推算缺失樣本。");
        if (expected == 0)
            throw new CoherenceLockValidationException("沒有任何 coherence / lock 樣本；不輸出空結果。");
        if (measurement.FirstCore.Equals(measurement.SecondCore))
            throw new CoherenceLockValidationException("coherence 對照不可使用相同邏輯處理器。");
        if (!IsFinitePositive(measurement.IndependentLineSamples)
            || !IsFinitePositive(measurement.FalseSharingSamples)
            || !IsFinitePositive(measurement.LockSingleSamples)
            || !IsFinitePositive(measurement.LockTwoSamples))
            throw new CoherenceLockValidationException("coherence / lock 吞吐出現非有限或非正數樣本；整場拒收。");
    }

    private static bool IsFinitePositive(IReadOnlyList<double> samples) =>
        samples.All(value => double.IsFinite(value) && value > 0);

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        CoherenceLockSettings settings,
        CoherenceLockMeasurement measurement)
    {
        bool multiGroup = CpuAffinity.IsMultiGroup;
        double independentMedian = Median(measurement.IndependentLineSamples);
        double falseSharingMedian = Median(measurement.FalseSharingSamples);
        double lockSingleMedian = Median(measurement.LockSingleSamples);
        double lockTwoMedian = Median(measurement.LockTwoSamples);
        double falseSharingPenalty = independentMedian / falseSharingMedian;
        double lockScaling = lockTwoMedian / lockSingleMedian;
        string processorText =
            $"LP{measurement.FirstCore.Index} + LP{measurement.SecondCore.Index}";
        string configuration =
            $"{settings.MeasureRounds} 量測回；每 worker {settings.OperationsPerWorker / 1_000.0:0.#}K ops；{processorText}";

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            configuration,
            [
                Metric("topology.coherence.independent-line.mops", "Independent cache lines", "Mops/s", measurement.IndependentLineSamples),
                Metric("topology.coherence.falsesharing.mops", "False sharing", "Mops/s", measurement.FalseSharingSamples),
                Metric("topology.coherence.falsesharing-penalty.ratio", "False-sharing penalty", "ratio", [falseSharingPenalty]),
                Metric("topology.coherence.lock-single.mops", "Lock single core", "Mops/s", measurement.LockSingleSamples),
                Metric("topology.coherence.lock-two.mops", "Lock two cores", "Mops/s", measurement.LockTwoSamples),
                Metric("topology.coherence.lock-scaling.ratio", "Lock 1→2 scaling", "ratio", [lockScaling]),
            ],
            [
                $"實測 {settings.MeasureRounds} 回；對照點 {processorText}；分離線使用 64-byte offset，false sharing 使用同線鄰接欄位。",
                $"中位數比率：false-sharing penalty {falseSharingPenalty:0.###}；lock scaling {lockScaling:0.###}。",
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
        IReadOnlyList<double> samples) =>
        new(id, name, unit, true, "pinned managed atomic/load-store workload", samples, []);

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
            ["需要至少兩顆可釘選的獨立實體核心。"],
            DeepBenchFailureKind.Unsupported, "缺少第二顆實體核心。");

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
public sealed class WindowsCoherenceLockEngine : ICoherenceLockEngine
{
    public Task<CoherenceLockMeasurement> MeasureAsync(
        CoherenceLockContext context,
        CancellationToken cancellationToken)
        => Task.Run(() => Measure(context, cancellationToken), cancellationToken);

    private static CoherenceLockMeasurement Measure(
        CoherenceLockContext context,
        CancellationToken cancellationToken)
    {
        CoherenceLockPlan? plan = CoherenceLockService.SelectPlan(context.PhysicalCores)
            ?? throw new CoherenceLockValidationException("engine 收到不完整的 coherence 拓樸。");
        var samples = (
            Independent: new List<double>(),
            FalseSharing: new List<double>(),
            LockSingle: new List<double>(),
            LockTwo: new List<double>());
        int totalRounds = context.Settings.WarmupRounds + context.Settings.MeasureRounds;

        for (int round = 0; round < totalRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isWarmup = round < context.Settings.WarmupRounds;
            int completed = samples.Independent.Count + samples.FalseSharing.Count
                + samples.LockSingle.Count + samples.LockTwo.Count;
            int total = context.Settings.MeasureRounds * 4;
            context.Progress.Report(new DeepBenchProgress(
                CoherenceLockService.TestId,
                completed,
                total,
                0.05 + 0.93 * completed / total,
                isWarmup ? $"warmup {round + 1}/{totalRounds}" : $"量測 {samples.Independent.Count + 1}/{context.Settings.MeasureRounds}"));

            double independent = RunWorkers(
                [plan.FirstCore, plan.SecondCore],
                WorkerMode.IndependentLine,
                context.Settings.OperationsPerWorker,
                cancellationToken);
            double falseSharing = RunWorkers(
                [plan.FirstCore, plan.SecondCore],
                WorkerMode.FalseSharing,
                context.Settings.OperationsPerWorker,
                cancellationToken);
            double lockSingle = RunWorkers(
                [plan.FirstCore],
                WorkerMode.Locked,
                context.Settings.OperationsPerWorker,
                cancellationToken);
            double lockTwo = RunWorkers(
                [plan.FirstCore, plan.SecondCore],
                WorkerMode.Locked,
                context.Settings.OperationsPerWorker,
                cancellationToken);
            if (isWarmup) continue;

            samples.Independent.Add(independent);
            samples.FalseSharing.Add(falseSharing);
            samples.LockSingle.Add(lockSingle);
            samples.LockTwo.Add(lockTwo);
        }

        context.Progress.Report(new DeepBenchProgress(
            CoherenceLockService.TestId,
            context.Settings.MeasureRounds * 4,
            context.Settings.MeasureRounds * 4,
            0.98,
            $"coherence / lock 完成；{context.Settings.MeasureRounds} 回"));
        return new CoherenceLockMeasurement(
            samples.Independent,
            samples.FalseSharing,
            samples.LockSingle,
            samples.LockTwo,
            plan.FirstCore,
            plan.SecondCore);
    }

    private static double RunWorkers(
        IReadOnlyList<ProcessorRef> processors,
        WorkerMode mode,
        int operationsPerWorker,
        CancellationToken cancellationToken)
    {
        ManualResetEventSlim? startGate = null;
        try
        {
            using CountdownEvent ready = new(processors.Count + 1);
            using CountdownEvent finished = new(processors.Count + 1);
            startGate = new ManualResetEventSlim(false);
            object sync = new();
            var independent = new Box<IndependentLineCounters>();
            var shared = new Box<SharedLineCounters>();
            var workers = processors
                .Select((processor, index) => new PinnedCoherenceWorker(
                    processor,
                    index,
                    mode,
                    operationsPerWorker,
                    independent,
                    shared,
                    sync,
                    ready,
                    finished,
                    startGate,
                    cancellationToken))
                .ToArray();
            var threads = workers
                .Select((worker, index) => new Thread(worker.Run)
                {
                    IsBackground = true,
                    Name = $"XinSpect coherence worker {index} G{processors[index].Group}LP{processors[index].Index}",
                })
                .ToArray();
            foreach (Thread thread in threads) thread.Start();
            ready.Signal();
            ready.Wait(cancellationToken);
            if (workers.Any(worker => worker.PinFailed))
            {
                startGate.Set();
                foreach (Thread thread in threads) thread.Join();
                throw new CoherenceLockValidationException("無法完成使用者模式親和性釘選；不偽裝成 pinned coherence 量測。");
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
                throw new CoherenceLockValidationException("coherence / lock 計時時間異常。");
            double throughput = processors.Count * operationsPerWorker / elapsedSeconds / 1_000_000d;
            if (!double.IsFinite(throughput) || throughput <= 0)
                throw new CoherenceLockValidationException("coherence / lock 合併吞吐非有限或非正數。");
            return throughput;
        }
        finally
        {
            startGate?.Set();
        }
    }

    private enum WorkerMode
    {
        IndependentLine,
        FalseSharing,
        Locked,
    }

    private sealed class Box<T>
    {
        public T Value = default!;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct IndependentLineCounters
    {
        [FieldOffset(0)] public long First;
        [FieldOffset(64)] public long Second;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SharedLineCounters
    {
        public long First;
        public long Second;
    }

    private sealed class PinnedCoherenceWorker(
        ProcessorRef processor,
        int workerIndex,
        WorkerMode mode,
        int operations,
        Box<IndependentLineCounters> independent,
        Box<SharedLineCounters> shared,
        object sync,
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

                Workload();
                finished.Signal();
            }
            catch (Exception exception)
            {
                Error = exception;
                finished.Signal();
            }
        }

        private void Workload()
        {
            switch (mode)
            {
                case WorkerMode.IndependentLine:
                    if (workerIndex == 0)
                    {
                        for (int index = 1; index <= operations; index++)
                            Interlocked.Increment(ref independent.Value.First);
                    }
                    else
                    {
                        for (int index = 1; index <= operations; index++)
                            Interlocked.Increment(ref independent.Value.Second);
                    }
                    break;
                case WorkerMode.FalseSharing:
                    if (workerIndex == 0)
                    {
                        for (int index = 1; index <= operations; index++)
                            Interlocked.Increment(ref shared.Value.First);
                    }
                    else
                    {
                        for (int index = 1; index <= operations; index++)
                            Interlocked.Increment(ref shared.Value.Second);
                    }
                    break;
                default:
                    for (int index = 1; index <= operations; index++)
                    {
                        lock (sync)
                        {
                            shared.Value.First++;
                        }
                    }
                    break;
            }
        }
    }
}
