using Xunit;

namespace XinSpect.Tests;

public class WindowsThroughputDegradationEngineTests
{
    [Fact]
    public async Task Windows引擎啟動前取消不殘留全核心負載()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var engine = new WindowsThroughputDegradationEngine();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.MeasureAsync(ThroughputDegradationService.GetSettings(DeepBenchRunProfile.Quick), cts.Token));
    }

    [Fact]
    public async Task Windows引擎保留真實分窗operations與電源快照()
    {
        var engine = new WindowsThroughputDegradationEngine();

        ThroughputDegradationRun run = await engine.MeasureAsync(
            new ThroughputDegradationWorkload(20, 1, 20, 1),
            CancellationToken.None);

        Assert.Equal(Environment.ProcessorCount, run.LogicalProcessors);
        ThroughputWindow window = Assert.Single(run.Windows);
        Assert.Equal(0, window.Index);
        Assert.True(window.DurationMs > 0);
        Assert.True(window.Operations > 0);
        Assert.True(window.OperationsPerSecond > 0);
        Assert.True(window.CurrentMhzMin > 0 && window.CurrentMhzMin <= window.CurrentMhzMax);
        Assert.True(window.MhzLimitMin > 0 && window.MhzLimitMin <= window.MhzLimitMax);
    }

    [Fact]
    public void Quick設定是短時四窗長跑()
    {
        var workload = ThroughputDegradationService.GetSettings(DeepBenchRunProfile.Quick);

        Assert.Equal(300, workload.WarmupMs);
        Assert.Equal(4, workload.Windows);
        Assert.Equal(800, workload.WindowMs);
        Assert.Equal(150, workload.CooldownMs);
    }
}
