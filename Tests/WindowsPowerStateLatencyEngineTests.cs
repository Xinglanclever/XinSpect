using Xunit;

namespace XinSpect.Tests;

public class WindowsPowerStateLatencyEngineTests
{
    /// <remarks>
    /// 這條測試原本用 10 ms 取樣、80 ms 觀測窗。「至少兩個快照」在正常機器上一定成立，
    /// 但兩種情形會讓它間歇紅燈：一是系統被別的測試佔滿時，第一輪 while 進不去就整輪結束；
    /// 二是 <see cref="Task.Delay(int)"/> 的實際解析度——Windows 排程器的最小延遲約 15.6 ms，
    /// 迴圈會在第一圈就超時而只收到一個快照。這是測試環境的抖動，不是被測程式的缺陷；
    /// 拉長觀測窗與縮短間隔後，兩者都只在極端負載下才可能發生。
    /// </remarks>
    [Fact]
    public async Task Windows電源API取樣保留快照與查詢延遲()
    {
        var engine = new WindowsPowerStateLatencyEngine();

        PowerStateLatencyRun run = await engine.ObserveAsync(
            new PowerStateLatencyWorkload(1, ObservationMs: 300, SamplingIntervalMs: 1),
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
