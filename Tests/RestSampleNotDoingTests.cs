using System;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// XT-003 REST 範例集「明文不做」的守門測試。
/// <para>
/// 這一項沒有實作可以測，所以測的是<b>聲明本身</b>：不做、不給範例、
/// 替代路徑存在、重新討論的條件寫得出來。把「不做」寫成可驗的契約，
/// 才不會哪天有人好心補上範例卻沒人發現紅線被移動了。
/// </para>
/// </summary>
public class RestSampleNotDoingTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact()
        => InterchangeAndReportFactsService.RestFact(At);

    [Fact]
    public void 狀態是NotSupported而不是空白待辦()
    {
        var f = Fact();
        // NotSupported 是明確的「刻意不支援」，與 NotApplicable（環境不適用）不同。
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Equal("xt.rest", f.Key);
    }

    [Fact]
    public void 值為空因為這把鍵不出範例()
    {
        var f = Fact();
        // 關鍵：不出範例。若哪天 Value 有內容，代表有人真的把範例放進去了。
        Assert.Equal("", f.Value);
        Assert.Null(f.NumericValue);
        Assert.False(f.Sensitive);
    }

    [Fact]
    public void 理由要說出為什麼連範例都不該出()
    {
        var reason = Fact().UnavailableReason!;
        Assert.Contains("範例一旦存在就會被複製到別的專案", reason);
    }

    [Fact]
    public void 替代路徑必須寫得出來()
    {
        var reason = Fact().UnavailableReason!;
        // 「不做」如果沒有替代路徑，使用者只會自己去找別的辦法（而那可能更糟）。
        Assert.Contains("CLI --json", reason);
        Assert.Contains("快照", reason);
    }

    [Fact]
    public void 重新討論的條件與接線登記一致()
    {
        var reason = Fact().UnavailableReason!;
        Assert.Contains("重新討論的條件", reason);
        // 與 WiringDecisions 對 LocalApiHandler 的判定指向同一組條件，避免兩處說法漂移。
        Assert.Contains("WiringDecisions.Deferred", reason);
        Assert.True(WiringDecisions.Deferred.ContainsKey("LocalApiHandler"),
            "LocalApiHandler 的接線判定不見了——這把鍵的重新討論條件指向它");
    }

    [Fact]
    public void 名稱本身就標明明文不做()
        => Assert.Contains("明文不做", Fact().Name);

    [Fact]
    public void 與ag遠端查詢同一政策()
    {
        var ag = AutomationAndGuidanceFactsService.Collect(At)
            .Single(f => f.Key == "ag.remote");
        // 兩把鍵是同一條紅線的兩個入口，狀態與信任度應該一致。
        Assert.Equal(FactAvailability.NotSupported, ag.Availability);
        Assert.Equal(FactAvailability.NotSupported, Fact().Availability);
        Assert.Equal(ag.Trust, Fact().Trust);
    }

    [Fact]
    public void 已收集的事實清單裡帶著這把鍵()
    {
        var facts = InterchangeAndReportFactsService.Collect(At);
        Assert.Contains(facts, f => f.Key == "xt.rest");
    }
}
