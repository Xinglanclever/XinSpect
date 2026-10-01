using Xunit;

namespace XinSpect.Tests;

public class ThroughputDegradationServiceTests
{
    [Fact]
    public void Quick與Full使用不同的分窗長跑設定()
    {
        var quick = ThroughputDegradationService.GetSettings(DeepBenchRunProfile.Quick);
        var full = ThroughputDegradationService.GetSettings(DeepBenchRunProfile.Full);

        Assert.Equal((300, 4, 800, 150), (quick.WarmupMs, quick.Windows, quick.WindowMs, quick.CooldownMs));
        Assert.Equal((500, 8, 1600, 300), (full.WarmupMs, full.Windows, full.WindowMs, full.CooldownMs));
    }

    [Fact]
    public async Task 結果保留分窗吞吐與early_late衰退指標()
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
    public async Task 分窗數不符時整場拒收()
    {
        var run = SampleRun() with { Windows = [.. SampleRun().Windows.Take(3)] };
        var service = new ThroughputDegradationService(new CapturingEngine(run));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("窗數不足", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 樣本非有限或區間無效時整場拒收()
    {
        var invalidOperations = SampleRun() with { Windows = Replace(0, w => w with { Operations = double.NaN }) };
        var invalidOrder = SampleRun() with { Windows = Replace(2, w => w with { CurrentMhzMin = 1900, CurrentMhzMax = 1800 }) };

        DeepBenchTestResult first = await RunAsync(new ThroughputDegradationService(new CapturingEngine(invalidOperations)), DeepBenchRunProfile.Quick);
        DeepBenchTestResult second = await RunAsync(new ThroughputDegradationService(new CapturingEngine(invalidOrder)), DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, first.FailureKind);
        Assert.Contains("非有限", first.Error, StringComparison.Ordinal);
        Assert.Equal(DeepBenchFailureKind.Unstable, second.FailureKind);
        Assert.Contains("非有限", second.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 電源API或平台不支援時如實Unsupported()
    {
        var service = new ThroughputDegradationService(
            new FailedEngine(new ThroughputDegradationUnsupportedException("CallNtPowerInformation returned 3221225473")));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Full);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("CallNtPowerInformation", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消後不輸出未完成分窗樣本()
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

    private static async Task<DeepBenchTestResult> RunAsync(
        ThroughputDegradationService service,
        DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private static ThroughputWindow[] Replace(int index, Func<ThroughputWindow, ThroughputWindow> change)
    {
        ThroughputWindow[] windows = [.. SampleRun().Windows];
        windows[index] = change(windows[index]);
        return windows;
    }

    private static ThroughputDegradationRun SampleRun()
    {
        ThroughputWindow Window(int index, double mops, double currentMhz) => new(
            index,
            800,
            mops * 1_000_000,
            mops * 1_000_000,
            currentMhz,
            currentMhz,
            2600,
            2600);

        return new(
            Environment.ProcessorCount,
            [
                Window(0, 1, 2000),
                Window(1, 1, 2000),
                Window(2, 0.8, 1800),
                Window(3, 0.8, 1800),
            ]);
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
