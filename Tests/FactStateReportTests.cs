using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-014（狀態格成文）、P1-017（讀取失敗帶原因）、
/// P1-020（逐態覆蓋申報）、P1-021（不可用原因可查詢）的輸出鍵生產與內容守門。
/// </summary>
public class FactStateReportTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, FactAvailability availability, string? reason = null) =>
        new(key, "測試", key, availability == FactAvailability.Present ? "值" : "—", "", "測試來源",
            FactTrustLevel.Reported, false, At, availability == FactAvailability.Present ? 1 : null,
            availability, reason);

    // ── P1-020 逐態統計 ─────────────────────────────────────────────────

    [Fact]
    public void 狀態分布_六態全部都要有計數包括零()
    {
        var counts = FactStateReport.CountByState([Fact("a", FactAvailability.Present)]);
        Assert.Equal(6, counts.Count);
        Assert.Equal(0, counts[FactAvailability.ReadError]);     // 「0 筆」不是「不存在」
        Assert.Equal(1, counts[FactAvailability.Present]);
    }

    [Fact]
    public void 狀態分布_總和等於事實總數且驗證通過()
    {
        var facts = new List<HardwareFact>
        {
            Fact("a", FactAvailability.Present),
            Fact("b", FactAvailability.Unknown, "快取殘留"),
            Fact("c", FactAvailability.ReadError, "MMIO 失敗"),
            Fact("d", FactAvailability.NotApplicable, "沒有 BMC"),
        };
        Assert.Equal(facts.Count, FactStateReport.CountByState(facts).Values.Sum());
        Assert.Empty(FactStateReport.ValidateCounts(facts));
    }

    [Fact]
    public void 狀態分布_有讀取受阻的事實時這把鍵本身不得標Present()
    {
        var fact = FactStateReport.ToCountFact(At, [Fact("a", FactAvailability.ReadError, "失敗")]);
        Assert.NotEqual(FactAvailability.Present, fact.Availability);
        Assert.Contains("不能", fact.UnavailableReason);
    }

    [Fact]
    public void 狀態分布_全部可用時才標Present()
    {
        var fact = FactStateReport.ToCountFact(At, [Fact("a", FactAvailability.Present)]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Null(fact.UnavailableReason);
    }

    [Fact]
    public void 狀態分布_沒有事實時標不適用而不是零分布()
    {
        var fact = FactStateReport.ToCountFact(At, []);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("沒有任何事實", fact.Value);
    }

    // ── P1-017 讀取失敗要帶原因 ─────────────────────────────────────────

    [Fact]
    public void 讀取失敗原因_沒有失敗時標不適用不是空字串()
    {
        var fact = FactStateReport.ToReadErrorReasonFact(At, [Fact("a", FactAvailability.Present)]);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("沒有讀取失敗", fact.Value);
    }

    [Fact]
    public void 讀取失敗原因_有失敗且都有原因時標Present()
    {
        var fact = FactStateReport.ToReadErrorReasonFact(At,
            [Fact("mchbar.base", FactAvailability.ReadError, "MMIO 失敗")]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("mchbar.base", fact.Value);
        Assert.Contains("MMIO 失敗", fact.Value);
    }

    [Fact]
    public void 讀取失敗原因_有失敗卻沒原因時要標讀取失敗()
    {
        var facts = new[] { Fact("x", FactAvailability.ReadError) };
        var fact = FactStateReport.ToReadErrorReasonFact(At, facts);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("沒有留下原因", fact.Value);
        Assert.Contains("沒有附原因", fact.UnavailableReason);
    }

    [Fact]
    public void 讀取失敗原因_驗證會指名沒附原因的鍵()
    {
        var problems = FactStateReport.ValidateCounts([Fact("孤兒鍵", FactAvailability.ReadError)]);
        Assert.Single(problems);
        Assert.Contains("孤兒鍵", problems[0]);
    }

    // ── P1-021 不可用原因可查詢 ─────────────────────────────────────────

    [Fact]
    public void 原因查詢_每一筆不可用的事實都要有句子()
    {
        var fact = FactStateReport.ToWhyUnreadableFact(At,
        [
            Fact("a", FactAvailability.Unknown, "快取殘留"),
            Fact("b", FactAvailability.NotSupported),
        ]);

        Assert.Contains("a：", fact.Value);
        Assert.Contains("b：", fact.Value);
        Assert.Contains("快取殘留", fact.Value);
        // 沒有附原因的，要用狀態本身的說明句補上，不能留空
        Assert.Contains("這一版的實作沒有這條路徑", fact.Value);
    }

    [Fact]
    public void 原因查詢_本身永遠是Present因為它是查得到的證明()
    {
        var fact = FactStateReport.ToWhyUnreadableFact(At, [Fact("a", FactAvailability.ReadError, "x")]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }

    [Fact]
    public void 原因查詢_全部可用時標不適用()
    {
        var fact = FactStateReport.ToWhyUnreadableFact(At, [Fact("a", FactAvailability.Present)]);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
    }

    // ── P1-014 狀態格成文 ───────────────────────────────────────────────

    [Fact]
    public void 狀態格_輸出的順序與程式碼的格一致()
    {
        var fact = FactStateReport.ToLatticeFact(At);
        int last = -1;
        foreach (var s in FactStateLattice.LatticeOrder)
        {
            int at = fact.Value.IndexOf(FactStateLattice.Describe(s), StringComparison.Ordinal);
            Assert.True(at > last, $"{s} 在輸出裡的位置不對——順序必須與格一致");
            last = at;
        }
    }

    [Fact]
    public void 狀態格_要說出傳播規則是最弱者()
    {
        var fact = FactStateReport.ToLatticeFact(At);
        Assert.Contains("最弱者", fact.Value);
        Assert.Equal(6.0, fact.NumericValue);
    }

    [Fact]
    public void 狀態格_永遠是Present因為它是規則的陳述()
    {
        Assert.Equal(FactAvailability.Present, FactStateReport.ToLatticeFact(At).Availability);
    }

    // ── 四把鍵一起 ─────────────────────────────────────────────────────

    [Fact]
    public void 四把狀態鍵_一次產生且鍵名與矩陣宣告一致()
    {
        var facts = FactStateReport.All(At, [Fact("a", FactAvailability.Present)]);
        Assert.Equal(4, facts.Count);
        Assert.Contains(facts, f => f.Key == "state.count");
        Assert.Contains(facts, f => f.Key == "fact.readerror.reason");
        Assert.Contains(facts, f => f.Key == "fact.why_unreadable");
        Assert.Contains(facts, f => f.Key == "state.lattice");
    }
}
