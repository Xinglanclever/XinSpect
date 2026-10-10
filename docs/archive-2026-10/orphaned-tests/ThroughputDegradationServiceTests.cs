using Xunit;

namespace XinSpect.Tests;

public class ThroughputDegradationServiceTests
{
    [Fact]
    public void Quick與Full使用不同窗數與長跑時間()
    {
        var quick = ThroughputDegradationService.GetSettings(DeepBenchRunProfile.Quick);
        var full = ThroughputDegradationService.GetSettings(DeepBenchRunProfile.Full);

        Assert.Equal((300, 4, 800, 150), (quick.WarmupMs, quick.Windows, quick.WindowMs, quick.CooldownMs));
        Assert.Equal((500, 8, 1600, 300), (full.WarmupMs, full.Windows, full.WindowMs, full.CooldownMs));
    }

    [Fact]
    public async Task 結果保留分窗吞吐前期後期與保留率()
    {
        var engine = new CapturingEngine(SampleRun());
        var service = new ThroughputDegradationService(engine);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal([1, 1, 0.8, 0.8], result.Metrics.Single(metric => metric.Id == "gauntlet.throughput.window.mops").Samples);
        Assert.Equal([1], result.Metrics.Single(metric => metric.Id == "gauntlet.throughput.early.mops").Samples);
        Assert.Equal([0.8], result.Metrics.Single(metric => metric.Id == "gauntlet.throughput.late.mops").Samples);
        Assert.Equal([80], result.Metrics.Single(metric => metric.Id == "gauntlet.throughput.retention.percent").Samples);
        Assert.Equal([20], result.Metrics.Single(metric => metric.Id == "gauntlet.throughput.degradation.percent").Samples);
        Assert.Equal([20], result.Metrics.Single(metric => metric.Id == "gauntlet.throughput.max-window-drop.percent").Samples);
        Assert.Equal([2000, 2000, 1800, 1800], result.Metrics.Single(metric => metric.Id == "gauntlet.throughput.current-mhz.max").Samples);
        Assert.Contains("first 25%", string.Join('\n', result.Conditions), StringComparison.Ordinal);
        Assert.Contains("P-state 上限換算值", string.Join('\n', result.Limitations), StringComparison.Ordinal);
        Assert.Single(engine.Runs);
    }

    [Fact]
    public async Task 窗數缺失或吞吐無效時整場拒收()
    {
        DeepBenchTestResult missing = await RunAsync(
            new ThroughputDegradationService(new CapturingEngine(RemoveLast(SampleRun()))),
            DeepBenchRunProfile.Quick);
        DeepBenchTestResult invalid = await RunAsync(
            new ThroughputDegradationService(new CapturingEngine(MakeInvalid(SampleRun()))),
            DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, missing.FailureKind);
        Assert.Contains("窗數不足", missing.Error, StringComparison.Ordinal);
        Assert.Equal(DeepBenchFailureKind.Unstable, invalid.FailureKind);
        Assert.Contains("非有限", invalid.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 引擎失敗時如實Unsupported()
    {
        var service = new ThroughputDegradationService(
            new FailedEngine(new ThroughputDegradationUnsupportedException("CallNtPowerInformation returned 3221225473")));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Full);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("CallNtPowerInformation", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消後不輸出未完成吞吐()
    {
        using var cts = new CancellationTokenSource(40);
        var service = new ThroughputDegradationService(new CancellingEngine());
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), DeepBenchRunProfile.Full, new Progress<DeepBenchProgress>());

        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Equal("已取消", result.Configuration);
    }

    [Fact]
    public void 保留率與相鄰下降只用同場正數吞吐()
    {
        Assert.Equal(80, ThroughputDegradationService.RetentionPercent(1_000, 800));
        Assert.Equal(20, ThroughputDegradationService.PercentChange(1_000, 800));
        Assert.Equal(0, ThroughputDegradationService.PercentChange(800, 1_000));
    }

    private static async Task<DeepBenchTestResult> RunAsync(ThroughputDegradationService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private static ThroughputDegradationRun SampleRun()
    {
        return new ThroughputDegradationRun(Environment.ProcessorCount,
        [
            new(0, 800, 800_000, 1_000_000, 2_000, 2_000, 2_600, 2_600),
            new(1, 800, 800_000, 1_000_000, 2_000, 2_000, 2_600, 2_600),
            new(2, 800, 640_000, 800_000, 1_800, 1_800, 2_600, 2_600),
            new(3, 800, 640_000, 800_000, 1_800, 1_800, 2_600, 2_600),
        ]);
    }

    private static ThroughputDegradationRun RemoveLast(ThroughputDegradationRun run) => run with
    {
        Windows = [.. run.Windows.Take(3)]
    };

    private static ThroughputDegradationRun MakeInvalid(ThroughputDegradationRun run)
    {
        List<ThroughputWindow> windows = [.. run.Windows];
        windows[2] = windows[2] with { Operations = double.NaN };
        return run with { Windows = windows };
    }

    private sealed class CapturingEngine(ThroughputDegradationRun run) : IThroughputDegradationEngine
    {
        public List<ThroughputDegradationRun> Runs { get; } = [];

        public Task<ThroughputDegradationRun> MeasureAsync(
            ThroughputDegradationWorkload workload,
            CancellationToken cancellationToken)
        {
            _ = workload;
            _ = cancellationToken;
            Runs.Add(run);
            return Task.FromResult(run);
        }
    }

    private sealed class FailedEngine(ThroughputDegradationUnsupportedException exception) : IThroughputDegradationEngine
    {
        public Task<ThroughputDegradationRun> MeasureAsync(
            ThroughputDegradationWorkload workload,
            CancellationToken cancellationToken)
        {
            _ = workload;
            _ = cancellationToken;
            throw exception;
        }
    }

    private sealed class CancellingEngine : IThroughputDegradationEngine
    {
        public async Task<ThroughputDegradationRun> MeasureAsync(
            ThroughputDegradationWorkload workload,
            CancellationToken cancellationToken)
        {
            _ = workload;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ThroughputDegradationRun(Environment.ProcessorCount, []);
        }
    }
}
