using Xunit;

namespace XinSpect.Tests;

public class SmtContentionServiceTests
{
    [Fact]
    public void 選點會找出SMT兄弟與兩顆獨立實體核心()
    {
        var cores = new[]
        {
            new ProcessorRef[] { new(0, 0), new(0, 4) },
            new ProcessorRef[] { new(0, 1), new(0, 5) },
            new ProcessorRef[] { new(0, 2) },
        };

        SmtContentionPlan? plan = SmtContentionService.SelectPlan(cores);

        Assert.NotNull(plan);
        Assert.Equal(new ProcessorRef(0, 0), plan.Single);
        Assert.Equal(new ProcessorRef(0, 1), plan.IndependentFirst);
        Assert.Equal(new ProcessorRef(0, 2), plan.IndependentSecond);
        Assert.Equal(new ProcessorRef(0, 0), plan.SiblingFirst);
        Assert.Equal(new ProcessorRef(0, 4), plan.SiblingSecond);
    }

    [Fact]
    public void 沒有SMT兄弟或第二顆實體核心時不偽裝可測()
    {
        Assert.Null(SmtContentionService.SelectPlan([]));
        Assert.Null(SmtContentionService.SelectPlan([new ProcessorRef[] { new(0, 0), new(0, 1) }]));
        Assert.Null(SmtContentionService.SelectPlan(
        [
            new ProcessorRef[] { new(0, 0) },
            new ProcessorRef[] { new(0, 1) },
        ]));
    }

    [Fact]
    public async Task 量測結果保留三種情境原始樣本與推導比率()
    {
        var measurement = new SmtContentionMeasurement(
            [100, 104],
            [188, 196],
            [124, 132],
            new ProcessorRef(0, 0),
            new ProcessorRef(0, 1),
            new ProcessorRef(0, 2),
            new ProcessorRef(0, 0),
            new ProcessorRef(0, 4));
        var service = CreateService(measurement);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(
            ["topology.smt.single.mops", "topology.smt.independent.mops", "topology.smt.sibling.mops",
             "topology.smt.independent-scaling.ratio", "topology.smt.sibling-efficiency.ratio"],
            result.Metrics.Select(metric => metric.Id));
        Assert.Equal([100, 104], result.Metrics[0].Samples);
        Assert.Equal([188, 196], result.Metrics[1].Samples);
        Assert.Equal([124, 132], result.Metrics[2].Samples);
        Assert.Equal(192d / 204d, result.Metrics[3].Samples.Single(), 12);
        Assert.Equal(128d / 204d, result.Metrics[4].Samples.Single(), 12);
        Assert.Contains("LP1 + LP2", string.Join('\n', result.Conditions), StringComparison.Ordinal);
        Assert.Contains("LP0 + LP4", string.Join('\n', result.Conditions), StringComparison.Ordinal);
        Assert.Contains("不是 CPU 規格值", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 缺樣本非有限或非正數都整場拒收()
    {
        var missing = new SmtContentionMeasurement(
            [100], [100, 100], [100, 100],
            new(0, 0), new(0, 1), new(0, 2), new(0, 0), new(0, 1));
        var nonPositive = new SmtContentionMeasurement(
            [100, 100], [100, 0], [100, 100],
            new(0, 0), new(0, 1), new(0, 2), new(0, 0), new(0, 1));

        DeepBenchTestResult missingResult = await RunAsync(CreateService(missing), DeepBenchRunProfile.Full);
        DeepBenchTestResult invalidResult = await RunAsync(CreateService(nonPositive), DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, missingResult.FailureKind);
        Assert.Equal(DeepBenchFailureKind.Unstable, invalidResult.FailureKind);
        Assert.Contains("樣本數", missingResult.Error, StringComparison.Ordinal);
        Assert.Contains("非有限或非正數", invalidResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 兄弟與獨立對照點不可相同或跨到同一核心()
    {
        var measurement = new SmtContentionMeasurement(
            [100, 100], [100, 100], [100, 100],
            new(0, 0), new(0, 1), new(0, 1), new(0, 0), new(0, 4));
        var service = CreateService(measurement);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("獨立對照", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 拓樸不足時標示不支援且不呼叫engine()
    {
        var engine = new CapturingEngine(CreateMeasurement());
        var service = new SmtContentionService(
            () => [new ProcessorRef[] { new(0, 0), new(0, 1) }],
            engine);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Empty(engine.Contexts);
        Assert.Contains("SMT", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消後不輸出部分情境補值()
    {
        using var cts = new CancellationTokenSource(50);
        var service = new SmtContentionService(
            () => CreateCores(),
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
        var capturing = new CapturingEngine(CreateMeasurement());
        var service = new SmtContentionService(() => CreateCores(), capturing);

        await RunAsync(service, DeepBenchRunProfile.Quick);
        await RunAsync(service, DeepBenchRunProfile.Full);

        SmtContentionSettings quick = Assert.Single(capturing.Contexts, item => item.Profile == DeepBenchRunProfile.Quick).Settings;
        SmtContentionSettings full = Assert.Single(capturing.Contexts, item => item.Profile == DeepBenchRunProfile.Full).Settings;
        Assert.True(full.OperationsPerWorker > quick.OperationsPerWorker);
        Assert.True(full.MeasureRounds > quick.MeasureRounds);
    }

    [Fact]
    public void 誠實界線明示量的是可觀察干擾不是硬體計數器()
    {
        Assert.Contains(SmtContentionService.Limitations, item => item.Contains("不是 CPU 規格值", StringComparison.Ordinal));
        Assert.Contains(SmtContentionService.Limitations, item => item.Contains("不是 SMT 硬體計數器", StringComparison.Ordinal));
        Assert.Contains(SmtContentionService.Limitations, item => item.Contains("親和性", StringComparison.Ordinal));
    }

    [Fact]
    public void 實機核心集合與既有實體核心列舉一致()
    {
        bool multiGroup = CpuAffinity.IsMultiGroup;
        ulong mask = multiGroup
            ? ulong.MaxValue
            : (ulong)System.Diagnostics.Process.GetCurrentProcess().ProcessorAffinity.ToInt64();
        var rows = CpuAffinity.PhysicalCores(multiGroup, mask);
        var sets = CpuAffinity.PhysicalCoreProcessorSets(multiGroup, mask);

        Assert.Equal(rows.Count, sets.Count);
        Assert.All(sets, set => Assert.NotEmpty(set));
        for (int index = 0; index < rows.Count; index++)
        {
            Assert.Equal(rows[index].First, sets[index][0]);
            Assert.Equal(rows[index].LpText, string.Join("／", sets[index].Select(item => item.Label(multiGroup))));
        }
    }

    [Fact]
    public async Task Windows引擎能完成一次最小實測()
    {
        var engine = new WindowsSmtContentionEngine();
        var context = new SmtContentionContext(
            CreateCores(),
            DeepBenchRunProfile.Quick,
            new SmtContentionSettings(16_384, 0, 1),
            new Progress<DeepBenchProgress>(),
            CancellationToken.None);

        SmtContentionMeasurement measurement = await engine.MeasureAsync(context, CancellationToken.None);

        Assert.Single(measurement.SingleSamples);
        Assert.Single(measurement.IndependentSamples);
        Assert.Single(measurement.SiblingSamples);
        Assert.All(measurement.SingleSamples, value => Assert.True(double.IsFinite(value) && value > 0));
        Assert.All(measurement.IndependentSamples, value => Assert.True(double.IsFinite(value) && value > 0));
        Assert.All(measurement.SiblingSamples, value => Assert.True(double.IsFinite(value) && value > 0));
    }

    private static ProcessorRef[][] CreateCores() =>
    [
        new ProcessorRef[] { new(0, 0), new(0, 4) },
        new ProcessorRef[] { new(0, 1), new(0, 5) },
        new ProcessorRef[] { new(0, 2) },
    ];

    private static SmtContentionMeasurement CreateMeasurement() =>
        new([100, 104], [188, 196], [124, 132], new(0, 0), new(0, 1), new(0, 2), new(0, 0), new(0, 4));

    private static SmtContentionService CreateService(SmtContentionMeasurement measurement) =>
        new(() => CreateCores(), new CapturingEngine(measurement));

    private static async Task<DeepBenchTestResult> RunAsync(SmtContentionService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(
            Guid.NewGuid(),
            profile,
            new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class CapturingEngine(SmtContentionMeasurement measurement) : ISmtContentionEngine
    {
        public List<SmtContentionContext> Contexts { get; } = [];

        public Task<SmtContentionMeasurement> MeasureAsync(
            SmtContentionContext context,
            CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            return Task.FromResult(measurement);
        }
    }

    private sealed class CancellingEngine : ISmtContentionEngine
    {
        public async Task<SmtContentionMeasurement> MeasureAsync(
            SmtContentionContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new SmtContentionMeasurement([], [], [], default, default, default, default, default);
        }
    }
}
