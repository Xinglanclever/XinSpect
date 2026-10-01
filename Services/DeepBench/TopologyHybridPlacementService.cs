using System.Diagnostics;
using System.Runtime.Intrinsics.X86;

namespace XinSpect;

public enum TopologyHybridCoreKind
{
    Unknown,
    Efficient,
    Performance,
}

public sealed record TopologyHybridCore(
    ProcessorRef Primary,
    IReadOnlyList<ProcessorRef> LogicalProcessors,
    TopologyHybridCoreKind Kind,
    byte NativeModel);

public sealed record TopologyHybridPlacementClassification(
    bool HybridCpuId,
    IReadOnlyList<TopologyHybridCore> Cores,
    string Source,
    string? UnsupportedReason)
{
    public bool CanMeasure => HybridCpuId && string.IsNullOrWhiteSpace(UnsupportedReason);

    public static TopologyHybridPlacementClassification Unsupported(string reason) =>
        new(false, [], reason, reason);
}

public interface ITopologyHybridPlacementClassifier
{
    TopologyHybridPlacementClassification Classify();
}

public sealed record TopologyHybridPlacementSettings(
    int OperationsPerWorker,
    int WarmupRounds,
    int MeasureRounds);

public sealed record TopologyHybridPlacementPlan(
    ProcessorRef PerformanceSingle,
    ProcessorRef EfficientSingle,
    IReadOnlyList<ProcessorRef> PerformancePair,
    IReadOnlyList<ProcessorRef> EfficientPair,
    IReadOnlyList<ProcessorRef> MixedPair);

public sealed record TopologyHybridPlacementScenarioSamples(
    string ScenarioId,
    IReadOnlyList<ProcessorRef> Processors,
    IReadOnlyList<double> Samples);

public sealed record TopologyHybridPlacementMeasurement(
    IReadOnlyList<TopologyHybridPlacementScenarioSamples> Scenarios);

public sealed record TopologyHybridPlacementContext(
    TopologyHybridPlacementClassification Classification,
    TopologyHybridPlacementPlan Plan,
    DeepBenchRunProfile Profile,
    TopologyHybridPlacementSettings Settings,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public interface ITopologyHybridPlacementEngine
{
    Task<TopologyHybridPlacementMeasurement> MeasureAsync(
        TopologyHybridPlacementContext context,
        CancellationToken cancellationToken);
}

public sealed class TopologyHybridPlacementValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// 混合核心放置：先誠實分類 P/E，再比較單緒、同類雙核與混合雙核放置。
/// 分類不完整時回 Unsupported，不把普通同構核心硬說成混合架構。
/// </summary>
public sealed class TopologyHybridPlacementService(
    ITopologyHybridPlacementClassifier? classifier = null,
    ITopologyHybridPlacementEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "topology.hybrid-placement";

    public static string[] Limitations { get; } =
    [
        "P/E 分類必須同時具備 CPUID hybrid 位元與逐核心 CPUID 0x1A 原生模型；分不出來就回 Unsupported，不用 EfficiencyClass 猜測。",
        "量的是本程式 pinned managed workload 在五種放置下的合併吞吐，不是 CPU 規格值，也不外推不同指令混合的表現。",
        "五種放置與明示比率並列檢視；不加權合成單一總分。",
        "只觀察放置差異；不自動改排程、電源計劃、優先權或頻率。",
        "使用者模式親和性受行程 affinity、處理器群組、電源與系統排程影響；多處理器群組路徑未在實機全面驗證。",
    ];

    private readonly ITopologyHybridPlacementClassifier _classifier =
        classifier ?? new WindowsTopologyHybridPlacementClassifier();
    private readonly ITopologyHybridPlacementEngine _engine = engine ?? new WindowsTopologyHybridPlacementEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "分類混合核心拓樸"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            TopologyHybridPlacementClassification classification = _classifier.Classify();
            if (!classification.CanMeasure)
                return Unsupported(context, started, classification.UnsupportedReason ?? "無法誠實分類 P/E 核心。");

            TopologyHybridPlacementPlan? plan = SelectPlan(classification);
            if (plan is null)
                return Unsupported(context, started, "需要可驗證的兩顆 P-core 與兩顆 E-core。");

            TopologyHybridPlacementSettings settings = GetSettings(context.Profile);
            var engineContext = new TopologyHybridPlacementContext(
                classification,
                plan,
                context.Profile,
                settings,
                context.Progress,
                cancellationToken);
            TopologyHybridPlacementMeasurement measurement = await _engine
                .MeasureAsync(engineContext, cancellationToken)
                .ConfigureAwait(false);
            Validate(measurement, plan);
            return CreateResult(context, started, classification, settings, plan, measurement);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (TopologyHybridPlacementValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static TopologyHybridPlacementSettings GetSettings(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(1_500_000, 1, 5),
        DeepBenchRunProfile.Full => new(5_000_000, 2, 18),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    internal static TopologyHybridPlacementPlan? SelectPlan(TopologyHybridPlacementClassification classification)
    {
        if (!classification.CanMeasure)
            return null;

        List<TopologyHybridCore> validCores = [.. classification.Cores.Where(IsWellFormedCore)];
        List<ProcessorRef> seen = [];
        if (validCores.Any(core => core.LogicalProcessors.Any(processor => !seen.AddUnique(processor))))
            return null;

        List<TopologyHybridCore> performance = [.. validCores.Where(core => core.Kind == TopologyHybridCoreKind.Performance)];
        List<TopologyHybridCore> efficient = [.. validCores.Where(core => core.Kind == TopologyHybridCoreKind.Efficient)];
        if (performance.Count < 2 || efficient.Count < 2)
            return null;

        return new TopologyHybridPlacementPlan(
            performance[0].Primary,
            efficient[0].Primary,
            [performance[0].Primary, performance[1].Primary],
            [efficient[0].Primary, efficient[1].Primary],
            [performance[0].Primary, efficient[0].Primary]);
    }

    private static bool IsWellFormedCore(TopologyHybridCore core) =>
        core.Kind is TopologyHybridCoreKind.Performance or TopologyHybridCoreKind.Efficient
        && core.LogicalProcessors.Count > 0
        && core.LogicalProcessors.Contains(core.Primary);

    private static void Validate(TopologyHybridPlacementMeasurement measurement, TopologyHybridPlacementPlan plan)
    {
        var expected = new (string Id, IReadOnlyList<ProcessorRef> Processors)[]
        {
            ("performance-single", [plan.PerformanceSingle]),
            ("efficient-single", [plan.EfficientSingle]),
            ("performance-pair", plan.PerformancePair),
            ("efficient-pair", plan.EfficientPair),
            ("mixed-pair", plan.MixedPair),
        };

        if (measurement.Scenarios.Count != expected.Length)
            throw new TopologyHybridPlacementValidationException("五種放置情境不完整或順序不一致；不推算缺失情境。");

        bool scenarioOrderMatches = expected
            .Select((item, index) => measurement.Scenarios[index].ScenarioId == item.Id)
            .All(match => match);
        if (!scenarioOrderMatches)
            throw new TopologyHybridPlacementValidationException("五種放置情境不完整或順序不一致；不推算缺失情境。");
        if (measurement.Scenarios.Select(scenario => scenario.Samples.Count).Distinct().Count() != 1)
            throw new TopologyHybridPlacementValidationException("五種情境樣本數不一致；不推算缺失樣本。");
        if (measurement.Scenarios.Select(scenario => scenario.Samples.Count).FirstOrDefault() == 0)
            throw new TopologyHybridPlacementValidationException("沒有任何混合核心放置樣本；不輸出空結果。");

        for (int index = 0; index < expected.Length; index++)
        {
            TopologyHybridPlacementScenarioSamples actual = measurement.Scenarios[index];
            if (!SameProcessors(actual.Processors, expected[index].Processors))
            {
                string message = index switch
                {
                    0 => $"單緒 P-core 必須是 {Label(plan.PerformanceSingle)}。",
                    1 => $"單緒 E-core 必須是 {Label(plan.EfficientSingle)}。",
                    2 => "P-core 雙核放置與分類規劃不一致。",
                    3 => "E-core 雙核放置與分類規劃不一致。",
                    _ => "混合放置與分類規劃不一致。",
                };
                throw new TopologyHybridPlacementValidationException(message);
            }
        }

        if (measurement.Scenarios.Any(scenario => scenario.Samples.Any(value => !double.IsFinite(value) || value <= 0)))
            throw new TopologyHybridPlacementValidationException("混合核心吞吐出現非有限或非正數樣本；整場拒收。");
    }

    private static bool SameProcessors(IReadOnlyList<ProcessorRef> actual, IReadOnlyList<ProcessorRef> expected) =>
        actual.Count == expected.Count && actual.SequenceEqual(expected);

    private static string Label(ProcessorRef processor) => CpuAffinity.IsMultiGroup
        ? $"G{processor.Group}·LP{processor.Index}"
        : $"LP{processor.Index}";

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        TopologyHybridPlacementClassification classification,
        TopologyHybridPlacementSettings settings,
        TopologyHybridPlacementPlan plan,
        TopologyHybridPlacementMeasurement measurement)
    {
        bool multiGroup = CpuAffinity.IsMultiGroup;
        double performanceMedian = Median(measurement.Scenarios[0].Samples);
        double efficientMedian = Median(measurement.Scenarios[1].Samples);
        double performancePairMedian = Median(measurement.Scenarios[2].Samples);
        double efficientPairMedian = Median(measurement.Scenarios[3].Samples);
        double mixedMedian = Median(measurement.Scenarios[4].Samples);
        double performanceOverEfficient = performanceMedian / efficientMedian;
        double performancePairScaling = performancePairMedian / (2d * performanceMedian);
        double efficientPairScaling = efficientPairMedian / (2d * efficientMedian);
        double mixedPlacementEfficiency = mixedMedian / (performanceMedian + efficientMedian);
        string placementText =
            $"P {plan.PerformanceSingle.Label(multiGroup)}；E {plan.EfficientSingle.Label(multiGroup)}；" +
            $"混合 {plan.MixedPair[0].Label(multiGroup)} + {plan.MixedPair[1].Label(multiGroup)}";
        string configuration =
            $"{settings.MeasureRounds} 量測回；每 worker {settings.OperationsPerWorker / 1_000_000.0:0.#}M ops；{placementText}";

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            configuration,
            [
                ScenarioMetric("performance-single", "P-core single", measurement.Scenarios[0].Samples),
                ScenarioMetric("efficient-single", "E-core single", measurement.Scenarios[1].Samples),
                ScenarioMetric("performance-pair", "P-core pair combined", measurement.Scenarios[2].Samples),
                ScenarioMetric("efficient-pair", "E-core pair combined", measurement.Scenarios[3].Samples),
                ScenarioMetric("mixed-pair", "P+E mixed combined", measurement.Scenarios[4].Samples),
                new(
                    "topology.hybrid.performance-over-efficient.ratio",
                    "P/E single throughput",
                    "ratio",
                    true,
                    "median / median",
                    [performanceOverEfficient],
                    []),
                new(
                    "topology.hybrid.performance-pair-scaling.ratio",
                    "P-core pair scaling",
                    "ratio",
                    true,
                    "combined / (2 × P single)",
                    [performancePairScaling],
                    []),
                new(
                    "topology.hybrid.efficient-pair-scaling.ratio",
                    "E-core pair scaling",
                    "ratio",
                    true,
                    "combined / (2 × E single)",
                    [efficientPairScaling],
                    []),
                new(
                    "topology.hybrid.mixed-placement-efficiency.ratio",
                    "Mixed placement efficiency",
                    "ratio",
                    true,
                    "mixed combined / (P single + E single)",
                    [mixedPlacementEfficiency],
                    []),
            ],
            [
                $"分類來源：{classification.Source}；{placementText}。",
                $"實測 {measurement.Scenarios[0].Samples.Count} 回；P/E 單緒中位數比率 {performanceOverEfficient:0.###}；混合放置效率 {mixedPlacementEfficiency:0.###}。",
                multiGroup ? "偵測到多處理器群組；使用明確 thread group affinity，但此路徑未在實機全面驗證。" : "使用使用者模式 thread affinity；不讀 MSR、不載入驅動。",
            ],
            Limitations,
            DeepBenchFailureKind.None,
            null);
    }

    private static DeepBenchMetric ScenarioMetric(
        string scenarioId,
        string name,
        IReadOnlyList<double> samples) =>
        new(
            $"topology.hybrid.{scenarioId}.mops",
            name,
            "Mops/s",
            true,
            "pinned managed integer/fixed-point workload",
            samples,
            []);

    private static double Median(IReadOnlyList<double> samples)
    {
        double[] sorted = [.. samples];
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2d;
    }

    private static DeepBenchTestResult Unsupported(DeepBenchRunContext context, DateTime started, string reason) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [],
            ["需要 CPUID hybrid、逐核心 CPUID 0x1A、兩顆 P-core 與兩顆 E-core。"],
            DeepBenchFailureKind.Unsupported, reason);

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消後不輸出部分放置補值。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["量測不完整時不推算缺失放置。"], kind, error);
}

/// <summary>Windows 使用者模式分類器：逐實體核心釘選後讀 CPUID 0x1A。</summary>
public sealed class WindowsTopologyHybridPlacementClassifier : ITopologyHybridPlacementClassifier
{
    public TopologyHybridPlacementClassification Classify()
    {
        if (!X86Base.IsSupported)
            return TopologyHybridPlacementClassification.Unsupported("此平台不支援 x86 CPUID。");

        uint maxStandardLeaf = (uint)X86Base.CpuId(0, 0).Eax;
        if (maxStandardLeaf < 7)
            return TopologyHybridPlacementClassification.Unsupported("CPUID hybrid 不可用：缺 leaf 7.0。");

        uint hybridFlags = (uint)X86Base.CpuId(7, 0).Edx;
        if (((hybridFlags >> 15) & 1u) == 0)
            return TopologyHybridPlacementClassification.Unsupported("CPUID hybrid 不可用：leaf 7.0 EDX 位 15 為 0。");
        if (maxStandardLeaf < 0x1A)
            return TopologyHybridPlacementClassification.Unsupported("CPUID hybrid 不可用：缺 leaf 0x1A。");

        bool multiGroup = CpuAffinity.IsMultiGroup;
        ulong group0Mask = ulong.MaxValue;
        if (!multiGroup)
        {
            try { group0Mask = (ulong)Process.GetCurrentProcess().ProcessorAffinity.ToInt64(); }
            catch { group0Mask = ulong.MaxValue; }
        }

        List<TopologyHybridCore> cores = [];
        foreach (IReadOnlyList<ProcessorRef> logicalProcessors in CpuAffinity.PhysicalCoreProcessorSets(multiGroup, group0Mask))
        {
            if (logicalProcessors.Count == 0)
                continue;
            if (TryClassifyCore(logicalProcessors[0], out TopologyHybridCoreKind kind, out byte nativeModel))
                cores.Add(new(logicalProcessors[0], logicalProcessors, kind, nativeModel));
        }

        int performanceCount = cores.Count(core => core.Kind == TopologyHybridCoreKind.Performance);
        int efficientCount = cores.Count(core => core.Kind == TopologyHybridCoreKind.Efficient);
        string source = "CPUID 7.0 hybrid + CPUID 0x1A";
        if (performanceCount < 2 || efficientCount < 2)
        {
            return new(
                true,
                cores,
                source,
                $"CPUID hybrid 可用，但只可靠分類 {performanceCount} 顆 P-core 與 {efficientCount} 顆 E-core。");
        }

        return new(true, cores, source, null);
    }

    internal static TopologyHybridCoreKind ClassifyCoreType(uint eax)
    {
        uint coreType = eax & 0xFF;
        return coreType switch
        {
            0x20 => TopologyHybridCoreKind.Efficient,
            0x40 => TopologyHybridCoreKind.Performance,
            _ => TopologyHybridCoreKind.Unknown,
        };
    }

    private static bool TryClassifyCore(ProcessorRef processor, out TopologyHybridCoreKind kind, out byte nativeModel)
    {
        kind = TopologyHybridCoreKind.Unknown;
        nativeModel = 0;
        try
        {
            using CpuAffinity.Pin pin = CpuAffinity.Pinned(processor);
            if (!pin.Ok || !X86Base.IsSupported)
                return false;

            uint eax = (uint)X86Base.CpuId(0x1A, 0).Eax;
            kind = ClassifyCoreType(eax);
            nativeModel = (byte)((eax >> 8) & 0xFF);
            return kind != TopologyHybridCoreKind.Unknown;
        }
        catch
        {
            kind = TopologyHybridCoreKind.Unknown;
            nativeModel = 0;
            return false;
        }
    }
}

/// <summary>Windows 使用者模式實作；五種放置全部明確釘選，不碰 MSR 或驅動。</summary>
public sealed class WindowsTopologyHybridPlacementEngine : ITopologyHybridPlacementEngine
{
    public Task<TopologyHybridPlacementMeasurement> MeasureAsync(
        TopologyHybridPlacementContext context,
        CancellationToken cancellationToken)
        => Task.Run(() => Measure(context, cancellationToken), cancellationToken);

    private static TopologyHybridPlacementMeasurement Measure(
        TopologyHybridPlacementContext context,
        CancellationToken cancellationToken)
    {
        _ = TopologyHybridPlacementService.SelectPlan(context.Classification)
            ?? throw new TopologyHybridPlacementValidationException("engine 收到不完整的混合核心分類。");
        ValidatePlan(context.Plan);
        var scenarios = new Dictionary<string, List<double>>(StringComparer.Ordinal)
        {
            ["performance-single"] = [],
            ["efficient-single"] = [],
            ["performance-pair"] = [],
            ["efficient-pair"] = [],
            ["mixed-pair"] = [],
        };
        var placements = new Dictionary<string, IReadOnlyList<ProcessorRef>>(StringComparer.Ordinal)
        {
            ["performance-single"] = [context.Plan.PerformanceSingle],
            ["efficient-single"] = [context.Plan.EfficientSingle],
            ["performance-pair"] = context.Plan.PerformancePair,
            ["efficient-pair"] = context.Plan.EfficientPair,
            ["mixed-pair"] = context.Plan.MixedPair,
        };
        int totalRounds = context.Settings.WarmupRounds + context.Settings.MeasureRounds;
        int completed = 0;
        int total = context.Settings.MeasureRounds * placements.Count;

        for (int round = 0; round < totalRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isWarmup = round < context.Settings.WarmupRounds;
            foreach ((string scenarioId, IReadOnlyList<ProcessorRef> processors) in placements)
            {
                context.Progress.Report(new DeepBenchProgress(
                    TopologyHybridPlacementService.TestId,
                    completed,
                    total,
                    0.05 + 0.93 * completed / total,
                    isWarmup ? $"warmup {round + 1}/{totalRounds} {scenarioId}" : $"量測 {scenarioId}"));
                double throughput = RunWorkers(
                    processors,
                    context.Settings.OperationsPerWorker,
                    cancellationToken);
                if (!isWarmup)
                {
                    scenarios[scenarioId].Add(throughput);
                    completed++;
                }
            }
        }

        context.Progress.Report(new DeepBenchProgress(
            TopologyHybridPlacementService.TestId,
            total,
            total,
            0.98,
            $"混合核心放置完成；{context.Settings.MeasureRounds} 回"));
        return new TopologyHybridPlacementMeasurement(
        [
            new("performance-single", placements["performance-single"], scenarios["performance-single"]),
            new("efficient-single", placements["efficient-single"], scenarios["efficient-single"]),
            new("performance-pair", placements["performance-pair"], scenarios["performance-pair"]),
            new("efficient-pair", placements["efficient-pair"], scenarios["efficient-pair"]),
            new("mixed-pair", placements["mixed-pair"], scenarios["mixed-pair"]),
        ]);
    }

    private static void ValidatePlan(TopologyHybridPlacementPlan plan)
    {
        IEnumerable<ProcessorRef> all =
        [
            plan.PerformanceSingle,
            plan.EfficientSingle,
            .. plan.PerformancePair,
            .. plan.EfficientPair,
            .. plan.MixedPair,
        ];
        if (all.Any(processor => processor.Index < 0 || processor.Index >= 64))
            throw new TopologyHybridPlacementValidationException("混合核心放置包含非法邏輯處理器。");
        if (plan.PerformancePair.Count != 2 || plan.EfficientPair.Count != 2 || plan.MixedPair.Count != 2)
            throw new TopologyHybridPlacementValidationException("混合核心雙核放置必須兩個執行點。");
        if (plan.PerformancePair[0].Equals(plan.PerformancePair[1])
            || plan.EfficientPair[0].Equals(plan.EfficientPair[1])
            || plan.MixedPair[0].Equals(plan.MixedPair[1]))
            throw new TopologyHybridPlacementValidationException("雙核放置不可使用相同邏輯處理器。");
    }

    private static double RunWorkers(
        IReadOnlyList<ProcessorRef> processors,
        long operationsPerWorker,
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
                    operationsPerWorker,
                    ready,
                    finished,
                    startGate,
                    cancellationToken))
                .ToArray();
            var threads = workers
                .Select((worker, index) => new Thread(worker.Run)
                {
                    IsBackground = true,
                    Name = $"XinSpect hybrid worker {index} G{processors[index].Group}LP{processors[index].Index}",
                })
                .ToArray();
            foreach (Thread thread in threads) thread.Start();
            ready.Signal();
            ready.Wait(cancellationToken);
            if (workers.Any(worker => worker.PinFailed))
            {
                startGate.Set();
                foreach (Thread thread in threads) thread.Join();
                throw new TopologyHybridPlacementValidationException("無法完成使用者模式親和性釘選；不偽裝成 pinned 混合核心量測。");
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
                throw new TopologyHybridPlacementValidationException("混合核心計時時間異常。");
            double throughput = processors.Count * operationsPerWorker / elapsedSeconds / 1_000_000d;
            if (!double.IsFinite(throughput) || throughput <= 0)
                throw new TopologyHybridPlacementValidationException("混合核心吞吐非有限或非正數。");
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

                Workload(operations, 23);
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

file static class ProcessorRefExtensions
{
    public static bool AddUnique<T>(this List<T> list, T value)
    {
        if (list.Contains(value))
            return false;
        list.Add(value);
        return true;
    }
}
