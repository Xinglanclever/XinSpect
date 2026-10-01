using Xunit;

namespace XinSpect.Tests;

public class WindowsPowerStateLatencyEngineTests
{
    [Fact]
    public async Task Windows電源API取樣保留快照與查詢延遲()
    {
        var engine = new WindowsPowerStateLatencyEngine();

        PowerStateLatencyRun run = await engine.ObserveAsync(
            new PowerStateLatencyWorkload(1, 80, 10),
            CancellationToken.None);

        Assert.Equal(Environment.ProcessorCount, run.LogicalProcessors);
        PowerStateLatencyRound round = Assert.Single(run.Rounds);
        Assert.True(round.Observations.Count >= 2, "Windows 電源 API 快速取樣至少應保留兩個快照。");
        Assert.All(round.Observations, observation =>
        {
            Assert.True(double.IsFinite(observation.TimeMs) && observation.TimeMs >= 0);
            Assert.True(double.IsFinite(observation.QueryLatencyUs) && observation.QueryLatencyUs > 0);
            Assert.True(observation.CurrentMhzMin <= observation.CurrentMhzMax);
            Assert.True(observation.MhzLimitMin <= observation.MhzLimitMax);
            Assert.True(observation.CurrentIdleStateMin <= observation.CurrentIdleStateMax);
        });
    }
}
