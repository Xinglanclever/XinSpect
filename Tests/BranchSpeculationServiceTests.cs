using Xunit;

namespace XinSpect.Tests;

public class BranchSpeculationServiceTests
{
    [Fact]
    public void Quick與Full使用不同輸入長度和回數()
    {
        var quick = BranchSpeculationService.GetSettings(DeepBenchRunProfile.Quick);
        var full = BranchSpeculationService.GetSettings(DeepBenchRunProfile.Full);

        Assert.True(full.InputsLength > quick.InputsLength);
        Assert.True(full.MeasureRounds > quick.MeasureRounds);
    }

    [Fact]
    public void 分支圖樣固定建立且涵蓋六種模式()
    {
        bool[][] first = BranchSpeculationService.CreatePatterns(1_024, 2026);
        bool[][] second = BranchSpeculationService.CreatePatterns(1_024, 2026);
        string[] ids = BranchSpeculationService.PatternIds;

        Assert.Equal(6, first.Length);
        Assert.Equal(ids.Length, first.Length);
        Assert.Equal(
            ["always", "alternate", "short-loop", "random25", "random50", "random75"],
            ids);
        for (int pattern = 0; pattern < first.Length; pattern++)
        {
            Assert.Equal(1_024, first[pattern].Length);
            Assert.Equal(first[pattern], second[pattern]);
        }

        Assert.All(first[0], Assert.True);
        Assert.Equal(512, first[1].Count(value => value));
        Assert.Equal(896, first[2].Count(value => value));
        Assert.InRange(first[3].Count(value => value), 179, 345);
        Assert.InRange(first[4].Count(value => value), 410, 614);
        Assert.InRange(first[5].Count(value => value), 679, 845);
    }

    [Fact]
    public async Task 結果保留六種圖樣原始吞吐與隨機時間比率()
    {
        var measurement = new BranchSpeculationMeasurement(
            [100, 104], [80, 84], [92, 96], [68, 72], [72, 76], [76, 80], [1.20, 1.24]);
        var capturing = new CapturingEngine(measurement);
        var service = new BranchSpeculationService(capturing);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(
        [
            "cpu.branchspec.always.mops",
            "cpu.branchspec.alternate.mops",
            "cpu.branchspec.short-loop.mops",
            "cpu.branchspec.random25.mops",
            "cpu.branchspec.random50.mops",
            "cpu.branchspec.random75.mops",
            "cpu.branchspec.random50-always-time.ratio",
        ], result.Metrics.Select(metric => metric.Id));
        Assert.Equal([100, 104], result.Metrics[0].Samples);
        Assert.Equal([1.20, 1.24], result.Metrics[6].Samples);
        Assert.Contains("always → random50", string.Join('\n', result.Conditions), StringComparison.Ordinal);
        Assert.Contains("不是 branch mispredict penalty", string.Join('\n', result.Limitations), StringComparison.Ordinal);
        Assert.Single(capturing.Contexts);
    }

    [Fact]
    public async Task 圖樣缺樣本或非正數整場拒收()
    {
        var missing = new BranchSpeculationMeasurement(
            [100], [], [90], [80], [70], [60], [1.1]);
        var invalid = new BranchSpeculationMeasurement(
            [100], [0], [90], [80], [70], [60], [1.1]);

        DeepBenchTestResult missingResult = await RunAsync(
            new BranchSpeculationService(new CapturingEngine(missing)), DeepBenchRunProfile.Full);
        DeepBenchTestResult invalidResult = await RunAsync(
            new BranchSpeculationService(new CapturingEngine(invalid)), DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, missingResult.FailureKind);
        Assert.Equal(DeepBenchFailureKind.Unstable, invalidResult.FailureKind);
        Assert.Contains("六種", missingResult.Error, StringComparison.Ordinal);
        Assert.Contains("非有限或非正數", invalidResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消後不輸出部分圖樣補值()
    {
        using var cts = new CancellationTokenSource(50);
        var service = new BranchSpeculationService(new CancellingEngine());
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Equal("已取消", result.Configuration);
    }

    [Fact]
    public void 誠實界線明示不是硬體預測器計數器()
    {
        Assert.Contains(BranchSpeculationService.Limitations, item => item.Contains("不是硬體預測器計數器", StringComparison.Ordinal));
        Assert.Contains(BranchSpeculationService.Limitations, item => item.Contains("不是 branch mispredict penalty", StringComparison.Ordinal));
        Assert.Contains(BranchSpeculationService.Limitations, item => item.Contains("JIT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Windows引擎能完成一次最小實測()
    {
        var engine = new WindowsBranchSpeculationEngine();
        var context = new BranchSpeculationContext(
            DeepBenchRunProfile.Quick,
            new BranchSpeculationSettings(1_024, 0, 1),
            new Progress<DeepBenchProgress>(),
            CancellationToken.None);

        BranchSpeculationMeasurement measurement = await engine.MeasureAsync(context, CancellationToken.None);

        Assert.Equal(BranchSpeculationService.PatternIds.Length, measurement.Patterns.Count);
        Assert.Single(measurement.RandomHalfToAlwaysTimeRatios);
        Assert.All(measurement.Patterns, pattern => Assert.Single(pattern.Samples));
        Assert.All(
            measurement.Patterns.SelectMany(pattern => pattern.Samples)
                .Concat(measurement.RandomHalfToAlwaysTimeRatios),
            value => Assert.True(double.IsFinite(value) && value > 0));
    }

    private static ProcessorRef[][] CreateCores() =>
    [
        new ProcessorRef[] { new(0, 0), new(0, 4) },
        new ProcessorRef[] { new(0, 1), new(0, 5) },
    ];

    private static async Task<DeepBenchTestResult> RunAsync(BranchSpeculationService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class CapturingEngine(BranchSpeculationMeasurement measurement) : IBranchSpeculationEngine
    {
        public List<BranchSpeculationContext> Contexts { get; } = [];

        public Task<BranchSpeculationMeasurement> MeasureAsync(
            BranchSpeculationContext context,
            CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            return Task.FromResult(measurement);
        }
    }

    private sealed class CancellingEngine : IBranchSpeculationEngine
    {
        public async Task<BranchSpeculationMeasurement> MeasureAsync(
            BranchSpeculationContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new BranchSpeculationMeasurement([], [], [], [], [], [], []);
        }
    }
}
