using Xunit;

namespace XinSpect.Tests;

public class WindowsBoostRecoveryEngineTests
{
    [Fact]
    public async Task Windows電源取樣保留負載脈衝三段快照()
    {
        var engine = new WindowsBoostRecoveryEngine();

        BoostRecoveryRun run = await engine.MeasureAsync(
            new BoostRecoveryWorkload(1, 20, 40, 40, 5),
            CancellationToken.None);

        Assert.Equal(Environment.ProcessorCount, run.LogicalProcessors);
        BoostRecoveryRound round = Assert.Single(run.Rounds);
        Assert.Contains(round.Samples, sample => sample.Phase == BoostRecoveryPhase.Idle);
        Assert.Contains(round.Samples, sample => sample.Phase == BoostRecoveryPhase.Load);
        Assert.Contains(round.Samples, sample => sample.Phase == BoostRecoveryPhase.Recovery);
        Assert.All(round.Samples, sample =>
        {
            Assert.True(double.IsFinite(sample.TimeMs) && sample.TimeMs >= 0);
            Assert.True(sample.CurrentMhzMin > 0 && sample.CurrentMhzMin <= sample.CurrentMhzMax);
            Assert.True(sample.MhzLimitMin > 0 && sample.MhzLimitMin <= sample.MhzLimitMax);
        });
    }
}
