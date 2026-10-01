using Xunit;

namespace XinSpect.Tests;

public class TopologyHybridPlacementServiceTests
{
    [Fact]
    public void 分組需要兩顆Pcore與兩顆Ecore並保留完整放置()
    {
        var cores = new[]
        {
            Core(0, 0, TopologyHybridCoreKind.Performance),
            Core(0, 1, TopologyHybridCoreKind.Performance),
            Core(0, 2, TopologyHybridCoreKind.Efficient),
            Core(0, 3, TopologyHybridCoreKind.Efficient),
            Core(0, 4, TopologyHybridCoreKind.Unknown),
        };

        TopologyHybridPlacementPlan? plan = TopologyHybridPlacementService.SelectPlan(
            new TopologyHybridPlacementClassification(true, cores, "CPUID 7.0 hybrid + CPUID 0x1A", null));

        Assert.NotNull(plan);
        Assert.Equal(new ProcessorRef(0, 0), plan.PerformanceSingle);
        Assert.Equal(new ProcessorRef(0, 2), plan.EfficientSingle);
        Assert.Equal([new ProcessorRef(0, 0), new ProcessorRef(0, 1)], plan.PerformancePair);
        Assert.Equal([new ProcessorRef(0, 2), new ProcessorRef(0, 3)], plan.EfficientPair);
        Assert.Equal([new ProcessorRef(0, 0), new ProcessorRef(0, 2)], plan.MixedPair);
    }

    [Fact]
    public async Task Pcore或Ecore不足兩顆時不退回普通核心測試()
    {
        var oneEfficient = new TopologyHybridPlacementClassification(
            true,
            [
                Core(0, 0, TopologyHybridCoreKind.Performance),
                Core(0, 1, TopologyHybridCoreKind.Performance),
                Core(0, 2, TopologyHybridCoreKind.Efficient),
            ],
            "CPUID 7.0 hybrid + CPUID 0x1A",
            null);
        Assert.Null(TopologyHybridPlacementService.SelectPlan(oneEfficient));

        var classifier = new FixedClassifier(TopologyHybridPlacementClassification.Unsupported("CPUID hybrid 不可用"));
        var engine = new CapturingEngine(CreateMeasurement(CreatePlan()));
        var service = new TopologyHybridPlacementService(classifier, engine);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Empty(engine.Contexts);
        Assert.Contains("CPUID hybrid", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 量測保留五種放置原始樣本並只做明示比率()
    {
        var measurement = CreateMeasurement(CreatePlan(), [101, 103], [63, 61], [198, 206], [118, 122], [159, 163]);
        var service = CreateService(measurement);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(
            [
                "topology.hybrid.performance-single.mops",
                "topology.hybrid.efficient-single.mops",
                "topology.hybrid.performance-pair.mops",
                "topology.hybrid.efficient-pair.mops",
                "topology.hybrid.mixed-pair.mops",
                "topology.hybrid.performance-over-efficient.ratio",
                "topology.hybrid.performance-pair-scaling.ratio",
                "topology.hybrid.efficient-pair-scaling.ratio",
                "topology.hybrid.mixed-placement-efficiency.ratio",
            ],
            result.Metrics.Select(metric => metric.Id));
        Assert.Equal([101, 103], result.Metrics[0].Samples);
        Assert.Equal([63, 61], result.Metrics[1].Samples);
        Assert.Equal([198, 206], result.Metrics[2].Samples);
        Assert.Equal([118, 122], result.Metrics[3].Samples);
        Assert.Equal([159, 163], result.Metrics[4].Samples);
        Assert.Equal(102d / 62d, result.Metrics[5].Samples.Single(), 12);
        Assert.Equal(202d / 204d, result.Metrics[6].Samples.Single(), 12);
        Assert.Equal(120d / 124d, result.Metrics[7].Samples.Single(), 12);
        Assert.Equal(161d / 164d, result.Metrics[8].Samples.Single(), 12);
        string mixedText = CpuAffinity.IsMultiGroup ? "G0·LP0 + G0·LP2" : "混合 LP0 + LP2";
        Assert.Contains(mixedText, string.Join('\n', result.Conditions), StringComparison.Ordinal);
        Assert.Contains("不加權", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 缺樣本非有限或非正數都整場拒收()
    {
        var missing = CreateMeasurement(
            CreatePlan(),
            [101],
            [63, 61],
            [198, 198],
            [118, 118],
            [159, 159]);
        var invalid = new TopologyHybridPlacementMeasurement(
            [
                Scenario("performance-single", [new ProcessorRef(0, 0)], [101, 0]),
                Scenario("efficient-single", [new ProcessorRef(0, 2)], [63, 61]),
                Scenario("performance-pair", [new ProcessorRef(0, 0), new ProcessorRef(0, 1)], [198, 206]),
                Scenario("efficient-pair", [new ProcessorRef(0, 2), new ProcessorRef(0, 3)], [118, 122]),
                Scenario("mixed-pair", [new ProcessorRef(0, 0), new ProcessorRef(0, 2)], [159, 163]),
            ]);
        var incomplete = new TopologyHybridPlacementMeasurement(
            [
                Scenario("performance-single", [new ProcessorRef(0, 0)], [101]),
                Scenario("efficient-single", [new ProcessorRef(0, 2)], [63]),
                Scenario("performance-pair", [new ProcessorRef(0, 0), new ProcessorRef(0, 1)], [198]),
                Scenario("efficient-pair", [new ProcessorRef(0, 2), new ProcessorRef(0, 3)], [118]),
            ]);

        DeepBenchTestResult missingResult = await RunAsync(CreateService(missing), DeepBenchRunProfile.Full);
        DeepBenchTestResult invalidResult = await RunAsync(CreateService(invalid), DeepBenchRunProfile.Quick);
        DeepBenchTestResult incompleteResult = await RunAsync(CreateService(incomplete), DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, missingResult.FailureKind);
        Assert.Equal(DeepBenchFailureKind.Unstable, invalidResult.FailureKind);
        Assert.Equal(DeepBenchFailureKind.Unstable, incompleteResult.FailureKind);
        Assert.Contains("樣本數", missingResult.Error, StringComparison.Ordinal);
        Assert.Contains("非有限或非正數", invalidResult.Error, StringComparison.Ordinal);
        Assert.Contains("情境不完整", incompleteResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 情境放置與分類不符時整場拒收()
    {
        var plan = CreatePlan();
        var measurement = new TopologyHybridPlacementMeasurement(
            [
                Scenario("performance-single", [plan.PerformanceSingle], [101]),
                IndexScenario("efficient-single", [0, 0], [63]),
                IndexScenario("performance-pair", [0, 0, 0, 1], [198]),
                IndexScenario("efficient-pair", [0, 2, 0, 3], [118]),
                IndexScenario("mixed-pair", [0, 0, 0, 2], [159]),
            ]);
        var service = CreateService(measurement);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        string expectedLabel = CpuAffinity.IsMultiGroup
            ? $"G0·LP{plan.EfficientSingle.Index}"
            : $"LP{plan.EfficientSingle.Index}";
        Assert.Contains($"單緒 E-core 必須是 {expectedLabel}", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消後不輸出部分放置補值()
    {
        using var cts = new CancellationTokenSource(50);
        var service = new TopologyHybridPlacementService(
            new FixedClassifier(CreateClassification()),
            new CancellingEngine());
        var context = new DeepBenchRunContext(
            Guid.NewGuid(),
            DeepBenchRunProfile.Quick,
            new Progress<DeepBenchProgress>());

        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Equal("已取消", result.Configuration);
    }

    [Fact]
    public async Task Quick與Full使用不同負載與回數()
    {
        var capturing = new CapturingEngine(CreateMeasurement(CreatePlan()));
        var service = new TopologyHybridPlacementService(new FixedClassifier(CreateClassification()), capturing);

        await RunAsync(service, DeepBenchRunProfile.Quick);
        await RunAsync(service, DeepBenchRunProfile.Full);

        TopologyHybridPlacementSettings quick = Assert.Single(
            capturing.Contexts,
            item => item.Profile == DeepBenchRunProfile.Quick).Settings;
        TopologyHybridPlacementSettings full = Assert.Single(
            capturing.Contexts,
            item => item.Profile == DeepBenchRunProfile.Full).Settings;
        Assert.True(full.OperationsPerWorker > quick.OperationsPerWorker);
        Assert.True(full.MeasureRounds > quick.MeasureRounds);
    }

    [Fact]
    public void 誠實界線明示分類來源且不控制排程()
    {
        Assert.Contains(TopologyHybridPlacementService.Limitations, item => item.Contains("CPUID 0x1A", StringComparison.Ordinal));
        Assert.Contains(TopologyHybridPlacementService.Limitations, item => item.Contains("不自動改排程", StringComparison.Ordinal));
        Assert.Contains(TopologyHybridPlacementService.Limitations, item => item.Contains("多處理器群組", StringComparison.Ordinal));
    }

    [Fact]
    public void CPUID混合核心類型只接受可驗證值()
    {
        Assert.Equal(TopologyHybridCoreKind.Performance, WindowsTopologyHybridPlacementClassifier.ClassifyCoreType(0x40 | (0x97u << 8)));
        Assert.Equal(TopologyHybridCoreKind.Efficient, WindowsTopologyHybridPlacementClassifier.ClassifyCoreType(0x20 | (0x5Cu << 8)));
        Assert.Equal(TopologyHybridCoreKind.Unknown, WindowsTopologyHybridPlacementClassifier.ClassifyCoreType(0));
        Assert.Equal(TopologyHybridCoreKind.Unknown, WindowsTopologyHybridPlacementClassifier.ClassifyCoreType(0x80));
    }

    [Fact]
    public async Task Windows引擎能完成一次最小pinned實測()
    {
        List<ProcessorRef> processors = CpuAffinity.AllLogicalProcessors();
        if (processors.Count < 4)
            return;

        var cores = processors
            .Take(4)
            .Select((processor, index) => new TopologyHybridCore(
                processor,
                [processor],
                index < 2 ? TopologyHybridCoreKind.Performance : TopologyHybridCoreKind.Efficient,
                0))
            .ToArray();
        var classification = new TopologyHybridPlacementClassification(true, cores, "engine smoke test", null);
        TopologyHybridPlacementPlan? plan = TopologyHybridPlacementService.SelectPlan(classification);
        Assert.NotNull(plan);
        var engine = new WindowsTopologyHybridPlacementEngine();
        var context = new TopologyHybridPlacementContext(
            classification,
            plan,
            DeepBenchRunProfile.Quick,
            new TopologyHybridPlacementSettings(16_384, 0, 1),
            new Progress<DeepBenchProgress>(),
            CancellationToken.None);

        TopologyHybridPlacementMeasurement measurement = await engine.MeasureAsync(context, CancellationToken.None);

        Assert.Equal(5, measurement.Scenarios.Count);
        Assert.All(measurement.Scenarios, scenario =>
        {
            Assert.Single(scenario.Samples);
            Assert.True(double.IsFinite(scenario.Samples[0]) && scenario.Samples[0] > 0);
        });
    }

    private static TopologyHybridCore Core(ushort group, int index, TopologyHybridCoreKind kind) =>
        new(new(group, index), [new(group, index)], kind, kind == TopologyHybridCoreKind.Performance ? (byte)0x97 : (byte)0x5C);

    private static TopologyHybridPlacementClassification CreateClassification()
    {
        var cores = new[]
        {
            Core(0, 0, TopologyHybridCoreKind.Performance),
            Core(0, 1, TopologyHybridCoreKind.Performance),
            Core(0, 2, TopologyHybridCoreKind.Efficient),
            Core(0, 3, TopologyHybridCoreKind.Efficient),
        };
        return new(true, cores, "CPUID 7.0 hybrid + CPUID 0x1A", null);
    }

    private static TopologyHybridPlacementPlan CreatePlan() =>
        TopologyHybridPlacementService.SelectPlan(CreateClassification())!;

    private static TopologyHybridPlacementScenarioSamples Scenario(
        string id,
        IReadOnlyList<ProcessorRef> processors,
        IReadOnlyList<double> samples) => new(id, processors, samples);

    private static TopologyHybridPlacementScenarioSamples IndexScenario(
        string id,
        IReadOnlyList<int> processorIndexes,
        IReadOnlyList<double> samples) =>
        new(id, processorIndexes.Select(index => new ProcessorRef(0, index)).ToArray(), samples);

    private static TopologyHybridPlacementMeasurement CreateMeasurement(
        TopologyHybridPlacementPlan plan,
        IReadOnlyList<double>? performanceSingle = null,
        IReadOnlyList<double>? efficientSingle = null,
        IReadOnlyList<double>? performancePair = null,
        IReadOnlyList<double>? efficientPair = null,
        IReadOnlyList<double>? mixedPair = null) => new(
    [
        Scenario("performance-single", [plan.PerformanceSingle], performanceSingle ?? [101]),
        Scenario("efficient-single", [plan.EfficientSingle], efficientSingle ?? [63]),
        Scenario("performance-pair", plan.PerformancePair, performancePair ?? [198]),
        Scenario("efficient-pair", plan.EfficientPair, efficientPair ?? [118]),
        Scenario("mixed-pair", plan.MixedPair, mixedPair ?? [159]),
    ]);

    private static TopologyHybridPlacementService CreateService(TopologyHybridPlacementMeasurement measurement) =>
        new(new FixedClassifier(CreateClassification()), new CapturingEngine(measurement));

    private static async Task<DeepBenchTestResult> RunAsync(TopologyHybridPlacementService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class FixedClassifier(TopologyHybridPlacementClassification Classification)
        : ITopologyHybridPlacementClassifier
    {
        public TopologyHybridPlacementClassification Classify() => Classification;
    }

    private sealed class CapturingEngine(TopologyHybridPlacementMeasurement measurement) : ITopologyHybridPlacementEngine
    {
        public List<TopologyHybridPlacementContext> Contexts { get; } = [];

        public Task<TopologyHybridPlacementMeasurement> MeasureAsync(
            TopologyHybridPlacementContext context,
            CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            return Task.FromResult(measurement);
        }
    }

    private sealed class CancellingEngine : ITopologyHybridPlacementEngine
    {
        public async Task<TopologyHybridPlacementMeasurement> MeasureAsync(
            TopologyHybridPlacementContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new TopologyHybridPlacementMeasurement([]);
        }
    }
}
