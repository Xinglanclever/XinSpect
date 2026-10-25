using System.Diagnostics;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 逐核一致性／弱核偵測（U10-TS-0026）的偏差數學與誠實界線。
/// 合成樣本注入，<b>不跑真實負載</b>——真機有沒有值不是測試的事。
/// </summary>
public class CoreUniformityTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    private static CoreUniformityService.CoreSample Core(int index, double mops) =>
        // MOPS = 運算量 ÷（Ticks/Frequency）÷ 1e6；Ticks 取一個真實 Frequency 窗，
        // 讓注入樣本與真機 QPC 頻率無關
        new(index, $"LP{index}", Operations: (long)(mops * 1_000_000), Ticks: Stopwatch.Frequency, Truncated: false);

    [Fact]
    public void 家族前綴是ts點core點uniformity點且不含Key字樣()
    {
        Assert.Equal("ts.core.uniformity.", CoreUniformityService.UniformityPrefix);
        Assert.DoesNotContain("Key", CoreUniformityService.UniformityPrefix);
    }

    [Fact]
    public void 一致的核心沒有疑似字樣_spread照實回報()
    {
        var facts = CoreUniformityService.BuildFacts(At,
        [
            Core(0, 100), Core(2, 101), Core(4, 99),
        ]);
        Assert.Equal(4, facts.Count);   // 3 核 + spread
        Assert.All(facts.Where(f => f.Key != "ts.core.uniformity.spread"),
            f => Assert.DoesNotContain("疑似", f.Value));
        var spread = facts.Single(f => f.Key == "ts.core.uniformity.spread");
        Assert.Equal(FactTrustLevel.Measured, spread.Trust);
        // 中位數 100；跨幅 (101-99)/100 = 2%
        Assert.Equal(2.0, (double)spread.NumericValue!, 6);
    }

    [Fact]
    public void 超過門檻的核標疑似但明言不構成判決()
    {
        var facts = CoreUniformityService.BuildFacts(At,
        [
            Core(0, 100), Core(2, 100), Core(4, 50),   // 核 4 慢一半
        ]);
        var weak = facts.Single(f => f.Key == "ts.core.uniformity.core.4");
        Assert.Equal(FactTrustLevel.Measured, weak.Trust);
        Assert.Contains("疑似被限流", weak.Value);
        Assert.Contains($"±{CoreUniformityService.DeviationThresholdFraction * 100:0}%", weak.Value);
        Assert.Contains("不構成判決", weak.Value);
        // 弱核不改寫其他核的事實
        var normal = facts.Single(f => f.Key == "ts.core.uniformity.core.0");
        Assert.DoesNotContain("疑似", normal.Value);
        // spread 總覽不列名、不判決
        var spread = facts.Single(f => f.Key == "ts.core.uniformity.spread");
        Assert.DoesNotContain("core.4", spread.Value);
        Assert.DoesNotContain("LP4", spread.Value);
    }

    [Fact]
    public void 剛好在門檻內不標疑似()
    {
        // 偏差 10% < 15% 門檻
        var facts = CoreUniformityService.BuildFacts(At,
        [
            Core(0, 100), Core(2, 90),
        ]);
        var core2 = facts.Single(f => f.Key == "ts.core.uniformity.core.2");
        Assert.DoesNotContain("疑似", core2.Value);
        Assert.Contains("門檻內", core2.Value);
    }

    [Fact]
    public void 鍵不重複_每核恰好一把()
    {
        var facts = CoreUniformityService.BuildFacts(At,
        [
            Core(0, 100), Core(2, 100), Core(4, 50),
        ]);
        Assert.Equal(facts.Count, facts.Select(f => f.Key).Distinct().Count());
    }

    [Fact]
    public void 非正值樣本標ReadError且不進入中位數()
    {
        var facts = CoreUniformityService.BuildFacts(At,
        [
            new(0, "LP0", Operations: 0, Ticks: 1_000, Truncated: false),
            Core(2, 100),
        ]);
        var bad = facts.Single(f => f.Key == "ts.core.uniformity.core.0");
        Assert.Equal(FactAvailability.ReadError, bad.Availability);
        Assert.Null(bad.NumericValue);
        var spread = facts.Single(f => f.Key == "ts.core.uniformity.spread");
        Assert.Contains("1 核", spread.Value);   // 只有 1 個有效核進入統計
    }

    [Fact]
    public void 全部取不到時回NotApplicable不以猜測冒充()
    {
        var facts = CoreUniformityService.BuildFacts(At, []);
        var f = Assert.Single(facts);
        Assert.Equal("ts.core.uniformity.spread", f.Key);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Null(f.NumericValue);
    }

    [Fact]
    public void 中位數奇偶取法()
    {
        Assert.Equal(3.0, CoreUniformityService.Median([1, 3, 9]), 6);
        Assert.Equal(4.0, CoreUniformityService.Median([1, 3, 5, 9]), 6);
        Assert.Equal(0.0, CoreUniformityService.Median([]), 6);
    }

    [Fact]
    public void 預算常數存在()
    {
        Assert.Equal(0.15, CoreUniformityService.DeviationThresholdFraction);
        Assert.Equal(200, CoreUniformityService.PerCoreBudgetMs);
    }

    [Fact]
    public void 注入探針時走同一個純函式出口()
    {
        var facts = CoreUniformityService.Collect(At, () =>
        [
            Core(0, 100), Core(2, 100), Core(4, 50),
        ]);
        Assert.Contains(facts, f => f.Key == "ts.core.uniformity.core.4" && f.Value.Contains("疑似"));
    }

    [Fact]
    public void 探針拋例外時整體收斂成ReadError而非崩潰()
    {
        var facts = CoreUniformityService.Collect(At, () => throw new InvalidOperationException("boom"));
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("boom", f.UnavailableReason);
    }
}
