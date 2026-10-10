using Xunit;

namespace XinSpect.Tests;

public class WindowsThroughputDegradationEngineTests
{
    [Fact]
    public async Task Windows全核心吞吐長跑保留分窗實測()
    {
        var engine = new WindowsThroughputDegradationEngine();

        ThroughputDegradationRun run = await engine.MeasureAsync(
            new ThroughputDegradationWorkload(100, 2, 80, 20),
            CancellationToken.None);

        Assert.Equal(Environment.ProcessorCount, run.LogicalProcessors);
        Assert.Equal(2, run.Windows.Count);
        Assert.All(run.Windows.Select(set => Assert.Single(set.Windows)), window =>
        {
            Assert.True(double.IsFinite(window.DurationMs) && window.DurationMs > 0);
            Assert.True(double.IsFinite(window.Operations) && window.Operations > 0);
            Assert.True(double.IsFinite(window.OperationsPerSecond) && window.OperationsPerSecond > 0);
            Assert.True(window.CurrentMhzMin > 0 && window.CurrentMhzMin <= window.CurrentMhzMax);
            Assert.True(window.MhzLimitMin > 0 && window.MhzLimitMin <= window.MhzLimitMax);
        });
    }
}
