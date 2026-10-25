using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 首頁健康摘要卡（U10-UI-0048／<c>ui.home.healthcards</c>）彙整器的守門：
/// <b>只陳述、不判決</b>——六態分布如實計數、矛盾逐條報名字、缺源不扣分不點燈；
/// 輸入只有既有事實，本服務不新增任何讀取路徑。
/// </summary>
public class HomeHealthSummaryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, string value = "讀到了", string name = "測試項",
        FactAvailability availability = FactAvailability.Present) =>
        new(key, "測試", name, value, "", "單元測試", FactTrustLevel.Measured, false, At, null, availability);

    private static HardwareFact Reconcile(string ruleId, string ruleName, string verdict) =>
        new($"reconcile.{ruleId}", "交叉對帳", ruleName, $"{verdict}：測試理由", "",
            "對帳規則 " + ruleId, FactTrustLevel.Derived, false, At, null);

    [Fact]
    public void 六態分布_如實計數_缺源是環境事實不是錯誤()
    {
        var s = HomeHealthSummaryService.Aggregate(
        [
            Fact("a.present.1"), Fact("a.present.2"), Fact("a.present.3"),
            Fact("b.notsup", availability: FactAvailability.NotSupported),
            Fact("c.priv", availability: FactAvailability.InsufficientPrivilege),
            Fact("d.err", availability: FactAvailability.ReadError),
            Fact("e.na", availability: FactAvailability.NotApplicable),
            Fact("f.unknown", availability: FactAvailability.Unknown),
        ]);

        Assert.Equal(8, s.Total);
        Assert.Equal(3, s.Present);
        Assert.Equal(1, s.NotSupported);
        Assert.Equal(1, s.InsufficientPrivilege);
        Assert.Equal(1, s.ReadError);
        Assert.Equal(1, s.NotApplicable);
        Assert.Equal(1, s.Unknown);
        Assert.Equal(5, s.Unreadable);       // 非 Present 的五態合計＝讀不到的來源數
        Assert.Equal(0, s.ReconcileCount);   // 這份合成輸入沒有對帳結論事實
    }

    [Fact]
    public void 矛盾_逐條報規則名字_一致與無法驗證不混入()
    {
        var s = HomeHealthSummaryService.Aggregate(
        [
            Fact("a.present"),
            Reconcile("r1", "微碼雙來源對帳", "矛盾"),
            Reconcile("r2", "SPI 後端一致性", "矛盾"),
            Reconcile("r3", "AER 管線狀態", "一致"),
            Reconcile("r4", "開機鏈重放", "無法驗證"),
        ]);

        Assert.Equal(4, s.ReconcileCount);   // 對帳筆數如實（矛盾 0 與 尚未對帳 是兩句話）
        Assert.Equal(2, s.ContradictionRules.Count);
        Assert.Contains("微碼雙來源對帳", s.ContradictionRules);
        Assert.Contains("SPI 後端一致性", s.ContradictionRules);
        Assert.DoesNotContain("AER 管線狀態", s.ContradictionRules);
        Assert.Equal("微碼雙來源對帳、SPI 後端一致性", s.ContradictionText);
    }

    [Fact]
    public void 沒有事實_直說尚未擷取_不畫全零的卡()
    {
        var s = HomeHealthSummaryService.Aggregate([]);

        Assert.False(s.HasFacts);
        Assert.Equal(0, s.Total);
        Assert.Equal(0, s.ReconcileCount);
        Assert.Empty(s.ContradictionRules);
    }

    [Fact]
    public void 零矛盾是陳述不是缺席_有事實無矛盾也要成立()
    {
        var s = HomeHealthSummaryService.Aggregate([Fact("a.present")]);

        Assert.True(s.HasFacts);
        Assert.Equal(0, s.ReconcileCount);    // 沒有對帳事實＝還沒對過帳，不是「矛盾 0」
        Assert.Empty(s.ContradictionRules);
        Assert.Equal("", s.ContradictionText);
        Assert.Equal(1, s.Present);
        Assert.Equal(0, s.Unreadable);
    }
}
