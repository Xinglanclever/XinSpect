using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// EC 界線與存在性的守門（ULTIMATE-1000 階段二 P2-092／P2-093）。
///
/// <para>
/// <b>核心是「沒有 ECDT」與「沒有 EC」之別</b>，
/// 以及「界線要被說出來」。
/// </para>
/// </summary>
public class EmbeddedControllerFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 表清單讀不到時要說那與沒有ECDT不同()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, null);
        var p = facts.Single(f => f.Key == EmbeddedControllerFactsService.PresentKey);

        Assert.Equal(FactAvailability.NotSupported, p.Availability);
        Assert.Null(p.NumericValue);
        Assert.Contains("「表清單讀不到」與「表清單裡沒有 ECDT」是不同的斷言", p.UnavailableReason);
    }

    [Fact]
    public void 有ECDT時要報出張數()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["DSDT", "FACP", "ECDT", "SSDT"]);
        var p = facts.Single(f => f.Key == EmbeddedControllerFactsService.PresentKey);

        Assert.Equal(FactAvailability.Present, p.Availability);
        Assert.Equal(1.0, p.NumericValue);
        Assert.Contains("有 ECDT（1 張）", p.Value);
    }

    /// <summary>這一條守著本項最重要的一句話。</summary>
    [Fact]
    public void 沒有ECDT時要說那不等於沒有EC()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["DSDT", "FACP"]);
        var p = facts.Single(f => f.Key == EmbeddedControllerFactsService.PresentKey);

        Assert.Equal(FactAvailability.NotApplicable, p.Availability);
        // **沒有 ECDT 時不帶數字**——「沒有」不是一個量測結果，而是一個否定。
        // 第一版我寫 Assert.Equal(0.0, ...)，而型別不變式把它收斂成 null，
        // 紅燈逼我面對「這把鍵該不該在沒有 ECDT 時帶 0」——答案是不該。
        Assert.Null(p.NumericValue);
        Assert.Contains("「沒有 ECDT」與「沒有 EC」是不同的斷言", p.UnavailableReason);
        Assert.Contains("ECDT 是可選的表", p.UnavailableReason);
        Assert.Contains("PNP0C09", p.UnavailableReason);
        Assert.Contains("只說「沒有從 ACPI 表看到它」", p.UnavailableReason);
    }

    [Fact]
    public void 有ECDT時要說出不讀它的內容()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["ECDT"]);
        var p = facts.Single(f => f.Key == EmbeddedControllerFactsService.PresentKey);

        Assert.Contains("不讀 ECDT 的內容", p.UnavailableReason);
        Assert.Contains("存在性這個問題用簽章就回答了", p.UnavailableReason);
    }

    [Fact]
    public void 簽章比對要忽略大小寫()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["ecdt"]);
        var p = facts.Single(f => f.Key == EmbeddedControllerFactsService.PresentKey);

        Assert.Equal(FactAvailability.Present, p.Availability);
    }

    // ── 界線鍵 ──────────────────────────────────────────────────────────

    [Fact]
    public void 界線鍵要說出本專案不讀寫EC埠()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["ECDT"]);
        var b = facts.Single(f => f.Key == EmbeddedControllerFactsService.BoundaryKey);

        Assert.Equal("本專案不讀寫 EC 埠", b.Value);
    }

    [Fact]
    public void 界線鍵要說出為什麼不碰()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["ECDT"]);
        var b = facts.Single(f => f.Key == EmbeddedControllerFactsService.BoundaryKey);

        Assert.Contains("0x62／0x66", b.UnavailableReason);
        Assert.Contains("有狀態的握手", b.UnavailableReason);
        Assert.Contains("通常由 ACPI 的 EC 驅動持有", b.UnavailableReason);
    }

    /// <summary>
    /// <b>這一條守著那把鍵存在的理由。</b>
    /// 直接讀 EC 的後果不是「讀到錯的值」，而是「讓別人的值變錯」。
    /// </summary>
    [Fact]
    public void 界線鍵要說出後果是讓別人的值變錯()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["ECDT"]);
        var b = facts.Single(f => f.Key == EmbeddedControllerFactsService.BoundaryKey);

        Assert.Contains("不是「讀到錯的值」", b.UnavailableReason);
        Assert.Contains("讓別人的值變錯", b.UnavailableReason);
    }

    [Fact]
    public void 界線鍵要說出沒說出口的界線會被誤讀()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["ECDT"]);
        var b = facts.Single(f => f.Key == EmbeddedControllerFactsService.BoundaryKey);

        Assert.Contains("這把鍵存在的理由就是說出那件事", b.UnavailableReason);
        Assert.Contains("會被讀成「還沒做」而不是「刻意不做」", b.UnavailableReason);
    }

    [Fact]
    public void 界線鍵在失敗路徑也要出現且內容相同()
    {
        var ok = EmbeddedControllerFactsService.Collect(At, ["ECDT"])
            .Single(f => f.Key == EmbeddedControllerFactsService.BoundaryKey);
        var none = EmbeddedControllerFactsService.Collect(At, null)
            .Single(f => f.Key == EmbeddedControllerFactsService.BoundaryKey);

        Assert.Equal(ok.Value, none.Value);
        Assert.Equal(ok.UnavailableReason, none.UnavailableReason);
    }

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = EmbeddedControllerFactsService.Collect(At, ["ECDT"])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = EmbeddedControllerFactsService.Collect(At, null)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = EmbeddedControllerFactsService.Collect(At, ["ECDT"]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "EC 事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
