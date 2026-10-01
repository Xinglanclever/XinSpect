using Xunit;

namespace XinSpect.Tests;

public class CoherenceLockServiceTests
{
    [Fact]
    public void 選點使用兩顆獨立實體核心的第一條執行緒()
    {
        var cores = new[]
        {
            new ProcessorRef[] { new(0, 0), new(0, 4) },
            new ProcessorRef[] { new(0, 1), new(0, 5) },
            new ProcessorRef[] { new(0, 2), new(0, 6) },
        };

        CoherenceLockPlan? plan = CoherenceLockService.SelectPlan(cores);

        Assert.NotNull(plan);
        Assert.Equal(new ProcessorRef(0, 0), plan.FirstCore);
        Assert.Equal(new ProcessorRef(0, 1), plan.SecondCore);
    }

    [Fact]
    public void 少於兩顆實體核心時不偽裝可測()
    {
        Assert.Null(CoherenceLockService.SelectPlan([]));
        Assert.Null(CoherenceLockService.SelectPlan(
        [
            new ProcessorRef[] { new(0, 0), new(0, 1) },
        ]));
    }

    [Fact]
    public async Task 量測結果保留原始樣本與一致性懲罰和鎖擴展比率()
    {
        var measurement = CreateMeasurement();
        var service = CreateService(measurement);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(
        [
            "topology.coherence.independent-line.mops",
            "topology.coherence.falsesharing.mops",
            "topology.coherence.falsesharing-penalty.ratio",
            "topology.coherence.lock-single.mops",
            "topology.coherence.lock-two.mops",
            "topology.coherence.lock-scaling.ratio",
        ], result.Metrics.Select(metric => metric.Id));
        Assert.Equal([188, 196], result.Metrics[0].Samples);
        Assert.Equal([100, 104], result.Metrics[1].Samples);
        Assert.Equal(192d / 102d, result.Metrics[2].Samples.Single(), 12);
        Assert.Equal([120, 124], result.Metrics[3].Samples);
        Assert.Equal([180, 186], result.Metrics[4].Samples);
        Assert.Equal(183d / 122d, result.Metrics[5].Samples.Single(), 12);
        Assert.Contains("LP0 + LP1", string.Join('\n', result.Conditions), StringComparison.Ordinal);
        Assert.Contains("不是硬體快取一致性計數器", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 缺樣本非有限或非正數都整場拒收()
    {
        var missing = new CoherenceLockMeasurement(
            [100], [100, 100], [100, 100], [100, 100],
            new(0, 0), new(0, 1));
        var nonPositive = new CoherenceLockMeasurement(
            [100, 100], [100, 0], [100, 100], [100, 100],
            new(0, 0), new(0, 1));

        DeepBenchTestResult missingResult = await RunAsync(CreateService(missing), DeepBenchRunProfile.Full);
        DeepBenchTestResult invalidResult = await RunAsync(CreateService(nonPositive), DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, missingResult.FailureKind);
        Assert.Equal(DeepBenchFailureKind.Unstable, invalidResult.FailureKind);
        Assert.Contains("樣本數", missingResult.Error, StringComparison.Ordinal);
        Assert.Contains("非有限或非正數", invalidResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 拓樸不足時標示不支援且不呼叫engine()
    {
        var engine = new CapturingEngine(CreateMeasurement());
        var service = new CoherenceLockService(
            () => [new ProcessorRef[] { new(0, 0), new(0, 1) }],
            engine);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Empty(engine.Contexts);
        Assert.Contains("實體核心", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消後不輸出部分情境補值()
    {
        using var cts = new CancellationTokenSource(50);
        var service = new CoherenceLockService(() => CreateCores(), new CancellingEngine());
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
        var service = new CoherenceLockService(() => CreateCores(), capturing);

        await RunAsync(service, DeepBenchRunProfile.Quick);
        await RunAsync(service, DeepBenchRunProfile.Full);

        CoherenceLockSettings quick = Assert.Single(capturing.Contexts, item => item.Profile == DeepBenchRunProfile.Quick).Settings;
        CoherenceLockSettings full = Assert.Single(capturing.Contexts, item => item.Profile == DeepBenchRunProfile.Full).Settings;
        Assert.True(full.OperationsPerWorker > quick.OperationsPerWorker);
        Assert.True(full.MeasureRounds > quick.MeasureRounds);
    }

    [Fact]
    public void 誠實界線明示可觀察吞吐與鎖實作限制()
    {
        Assert.Contains(CoherenceLockService.Limitations, item => item.Contains("不是硬體快取一致性計數器", StringComparison.Ordinal));
        Assert.Contains(CoherenceLockService.Limitations, item => item.Contains("不外推不同臨界區長度", StringComparison.Ordinal));
        Assert.Contains(CoherenceLockService.Limitations, item => item.Contains("親和性", StringComparison.Ordinal));
        Assert.Contains(CoherenceLockService.Limitations, item => item.Contains("混合架構", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Windows引擎能完成一次最小實測()
    {
        var engine = new WindowsCoherenceLockEngine();
        var context = new CoherenceLockContext(
            CreateCores(),
            DeepBenchRunProfile.Quick,
            new CoherenceLockSettings(4_096, 0, 1),
            new Progress<DeepBenchProgress>(),
            CancellationToken.None);

        CoherenceLockMeasurement measurement = await engine.MeasureAsync(context, CancellationToken.None);

        Assert.Single(measurement.IndependentLineSamples);
        Assert.Single(measurement.FalseSharingSamples);
        Assert.Single(measurement.LockSingleSamples);
        Assert.Single(measurement.LockTwoSamples);
        Assert.All(
            measurement.IndependentLineSamples
                .Concat(measurement.FalseSharingSamples)
                .Concat(measurement.LockSingleSamples)
                .Concat(measurement.LockTwoSamples),
            value => Assert.True(double.IsFinite(value) && value > 0));
    }

    private static ProcessorRef[][] CreateCores() =>
    [
        new ProcessorRef[] { new(0, 0), new(0, 4) },
        new ProcessorRef[] { new(0, 1), new(0, 5) },
        new ProcessorRef[] { new(0, 2), new(0, 6) },
    ];

    private static CoherenceLockMeasurement CreateMeasurement() =>
        new([188, 196], [100, 104], [120, 124], [180, 186], new(0, 0), new(0, 1));

    private static CoherenceLockService CreateService(CoherenceLockMeasurement measurement) =>
        new(() => CreateCores(), new CapturingEngine(measurement));

    private static async Task<DeepBenchTestResult> RunAsync(CoherenceLockService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(
            Guid.NewGuid(),
            profile,
            new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class CapturingEngine(CoherenceLockMeasurement measurement) : ICoherenceLockEngine
    {
        public List<CoherenceLockContext> Contexts { get; } = [];

        public Task<CoherenceLockMeasurement> MeasureAsync(
            CoherenceLockContext context,
            CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            return Task.FromResult(measurement);
        }
    }

    private sealed class CancellingEngine : ICoherenceLockEngine
    {
        public async Task<CoherenceLockMeasurement> MeasureAsync(
            CoherenceLockContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new CoherenceLockMeasurement([], [], [], [], default, default);
        }
    }
}
