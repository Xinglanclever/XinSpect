using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 初始開機塊（IBB）與金鑰模組（KM）判定的守門（ULTIMATE-1000 階段二 P2-054／P2-055）。
///
/// <para>
/// <b>核心是「目錄是索引，不是結論」</b>：FIT 目錄裡有那些條目，
/// 不代表 Boot Guard 已啟用、更不代表驗證通過。
/// </para>
///
/// <para>
/// <b>而這一組也守著一件事：沒有出處就不給編號。</b>
/// Key Manifest 的型別編號不在本專案任何地方，
/// 所以 <c>bt.km</c> 永遠 <c>NotSupported</c>——見 <c>Km</c> 的實作。
/// </para>
/// </summary>
public class BootGuardFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static FitTable Fit(params (ushort Type, uint Offset, uint Size)[] entries)
        => new(entries.Select(e => new FitEntry(e.Type,
            FitDecoder.TypeNames.TryGetValue(e.Type, out string? n) ? n : "未收錄",
            e.Offset, e.Size)).ToList(), 0x100, null);

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有FIT表時要說那與沒有信任鏈條目不同()
    {
        var facts = BootGuardFactsService.Collect(At, null);
        var i = facts.Single(f => f.Key == BootGuardFactsService.IbbKey);

        Assert.Equal(FactAvailability.NotSupported, i.Availability);
        Assert.Contains("「讀不到 FIT」與「FIT 裡沒有信任鏈條目」是不同的斷言", i.UnavailableReason);
        Assert.Contains("不自己去讀快閃", i.UnavailableReason);
    }

    [Fact]
    public void 空目錄時要標不適用並說出不猜()
    {
        var facts = BootGuardFactsService.Collect(At, Fit());
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
    }

    // ── IBB ─────────────────────────────────────────────────────────────

    [Fact]
    public void 有啟動模組條目時要報出位移與長度()
    {
        var facts = BootGuardFactsService.Collect(At, Fit((0x07, 0x1000, 0x8000)));
        var i = facts.Single(f => f.Key == BootGuardFactsService.IbbKey);

        Assert.Equal(FactAvailability.Present, i.Availability);
        Assert.Equal(1.0, i.NumericValue);
        Assert.Contains("位移 0x1000", i.Value);
        Assert.Contains("長度 32768 bytes", i.Value);
    }

    [Fact]
    public void 沒有啟動模組條目要說那不是沒有IBB()
    {
        var facts = BootGuardFactsService.Collect(At, Fit((0x02, 0x100, 0x40)));
        var i = facts.Single(f => f.Key == BootGuardFactsService.IbbKey);

        Assert.Equal(FactAvailability.NotApplicable, i.Availability);
        Assert.Contains("「目錄裡沒有啟動模組條目」與「這台機器沒有 IBB」是不同的斷言", i.UnavailableReason);
        Assert.Contains("不是「沒有 IBB」", i.UnavailableReason);
    }

    [Fact]
    public void IBB要說出目錄條目不是IBB範圍()
    {
        var facts = BootGuardFactsService.Collect(At, Fit((0x07, 0x1000, 0x8000)));
        var i = facts.Single(f => f.Key == BootGuardFactsService.IbbKey);

        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("目錄條目給的是「宣告的位置與長度」，不是「IBB 的範圍」", i.Value);
        Assert.Contains("真正的 IBB 範圍由 Boot Policy Manifest 決定", i.Value);
        Assert.Contains("本版只到目錄層", i.Value);
    }

    [Fact]
    public void IBB要說出有條目不代表BootGuard已啟用()
    {
        var facts = BootGuardFactsService.Collect(At, Fit((0x07, 0x1000, 0x8000)));
        var i = facts.Single(f => f.Key == BootGuardFactsService.IbbKey);

        Assert.Contains("不代表 Boot Guard 已啟用", i.Value);
        Assert.Contains("目錄是索引，不是結論", i.Value);
    }

    // ── KM：永遠 NotSupported ───────────────────────────────────────────

    /// <summary>
    /// <b>這一條守著本輪最重要的一個決定。</b>
    /// Key Manifest 的型別編號沒有出處，所以不得產出任何值——
    /// <b>即使目錄裡有看起來像它的未收錄條目。</b>
    /// </summary>
    [Fact]
    public void KM永遠標不支援且不給編號()
    {
        // 目錄裡放一個典型的「未收錄」型別（0x0B 是 KM 的常見說法，但本版不採用）。
        var facts = BootGuardFactsService.Collect(At, Fit((0x0B, 0x2000, 0x100)));
        var k = facts.Single(f => f.Key == BootGuardFactsService.KmKey);

        Assert.Equal(FactAvailability.NotSupported, k.Availability);
        Assert.Null(k.NumericValue);
        Assert.Equal("", k.Value);
        Assert.DoesNotContain("0x0B", k.Value);
        Assert.DoesNotContain("0x0B", k.UnavailableReason);
    }

    [Fact]
    public void KM的理由要說出填錯編號比未收錄更糟()
    {
        var facts = BootGuardFactsService.Collect(At, Fit((0x07, 0x1000, 0x8000)));
        var k = facts.Single(f => f.Key == BootGuardFactsService.KmKey);

        Assert.Contains("本版不判定 KM", k.UnavailableReason);
        Assert.Contains("不憑印象填一個進去", k.UnavailableReason);
        Assert.Contains("填錯的編號會讓一個不存在的條目看起來存在", k.UnavailableReason);
        Assert.Contains("那比「未收錄」更糟", k.UnavailableReason);
    }

    [Fact]
    public void KM的理由要指出修好的路徑()
    {
        var facts = BootGuardFactsService.Collect(At, Fit((0x07, 0x1000, 0x8000)));
        var k = facts.Single(f => f.Key == BootGuardFactsService.KmKey);

        Assert.Contains("Intel FWI 的型別編號出處", k.UnavailableReason);
        Assert.Contains("FitDecoder.TypeNames", k.UnavailableReason);
        Assert.Contains("一處改、兩處受益", k.UnavailableReason);
    }

    // ── 信任鏈目錄 ──────────────────────────────────────────────────────

    [Fact]
    public void 目錄條目要逐類計數()
    {
        var facts = BootGuardFactsService.Collect(At,
            Fit((0x02, 0x100, 0x40), (0x05, 0x200, 0x80), (0x07, 0x1000, 0x8000)));
        var m = facts.Single(f => f.Key == BootGuardFactsService.ManifestsKey);

        Assert.Equal(3.0, m.NumericValue);
        Assert.Contains("ACM 1", m.Value);
        Assert.Contains("Boot Policy 1", m.Value);
        Assert.Contains("啟動模組 1", m.Value);
    }

    [Fact]
    public void 未收錄型別要列編號但不命名()
    {
        var facts = BootGuardFactsService.Collect(At,
            Fit((0x07, 0x1000, 0x8000), (0x0B, 0x2000, 0x100), (0x42, 0x3000, 0x50)));
        var m = facts.Single(f => f.Key == BootGuardFactsService.ManifestsKey);

        Assert.Equal(FactAvailability.Unknown, m.Availability);
        Assert.Contains("0x0B", m.Value);
        Assert.Contains("0x42", m.Value);
        Assert.Contains("如實列出編號而不命名", m.UnavailableReason);
        Assert.Contains("猜一個名字會讓它們看起來像我們知道的東西", m.UnavailableReason);
    }

    [Fact]
    public void 未收錄型別裡要暗示KM可能在其中但不指名()
    {
        var facts = BootGuardFactsService.Collect(At, Fit((0x0B, 0x2000, 0x100)));
        var m = facts.Single(f => f.Key == BootGuardFactsService.ManifestsKey);

        Assert.Contains("Key Manifest 很可能就在其中，但本版不指名", m.UnavailableReason);
    }

    [Fact]
    public void 目錄要說出有條目與已啟用是兩件事()
    {
        var facts = BootGuardFactsService.Collect(At, Fit((0x07, 0x1000, 0x8000)));
        var m = facts.Single(f => f.Key == BootGuardFactsService.ManifestsKey);

        Assert.Contains("目錄是索引，不是結論", m.Value);
        Assert.Contains("「有這些條目」與「Boot Guard 已啟用」是兩件事", m.Value);
        Assert.Contains("FLMAP／FMLF", m.Value);
    }

    [Fact]
    public void 沒有未收錄型別時目錄要標Present()
    {
        var facts = BootGuardFactsService.Collect(At, Fit((0x07, 0x1000, 0x8000)));
        var m = facts.Single(f => f.Key == BootGuardFactsService.ManifestsKey);

        Assert.Equal(FactAvailability.Present, m.Availability);
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = BootGuardFactsService.Collect(At, Fit((0x07, 0x1000, 0x8000)))
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = BootGuardFactsService.Collect(At, null)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = BootGuardFactsService.Collect(At, Fit((0x07, 0x1000, 0x8000)));
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "Boot Guard 事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
