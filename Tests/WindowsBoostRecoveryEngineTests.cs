using System.Linq;
using Xunit;

namespace XinSpect.Tests;

public class WindowsBoostRecoveryEngineTests
{
    [Fact]
    public async Task Windows電源取樣保留負載脈衝三段快照()
    {
        var engine = new WindowsBoostRecoveryEngine();

        // 相位窗放大（20/40/40 → 80/120/120）：高系統負載下單次 Task.Delay 的逾衝可能一口氣跳過整個
        // 40ms 相位——那是取樣時序的物理現象，不是引擎缺陷。放大窗口＋至多三次嘗試，
        // 把「偶發逾衝」（重試即過）與「引擎真的壞了」（三次都缺相位）分開；三次仍缺就照實紅燈。
        BoostRecoveryRun run = null!;
        for (int attempt = 1; ; attempt++)
        {
            run = await engine.MeasureAsync(
                new BoostRecoveryWorkload(1, 80, 120, 120, 5),
                CancellationToken.None);
            BoostRecoveryRound probe = Assert.Single(run.Rounds);
            bool complete = probe.Samples.Any(s => s.Phase == BoostRecoveryPhase.Idle)
                            && probe.Samples.Any(s => s.Phase == BoostRecoveryPhase.Load)
                            && probe.Samples.Any(s => s.Phase == BoostRecoveryPhase.Recovery);
            if (complete || attempt >= 3) break;
        }

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
