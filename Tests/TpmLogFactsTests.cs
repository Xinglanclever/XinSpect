using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 量測開機日誌摘要與直方圖的守門（ULTIMATE-1000 階段二 P2-071／P2-072）。
///
/// <para>
/// <b>核心是「只列型別編號，不命名」。</b>本專案沒有 TCG EV_* 表的出處，
/// 而填錯的編號會讓一個不存在的東西看起來存在。
/// </para>
/// </summary>
public class TpmLogFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static Tpm2.TcgEvent E(uint pcr, uint type, int digests = 1)
        => new(pcr, type, Enumerable.Range(0, digests)
            .Select(i => ((ushort)(0x0B + i), new byte[32])).ToList());

    private static Tpm2.TcgLogWalk W(bool truncated, params Tpm2.TcgEvent[] events)
        => new(events, truncated);

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有走訪結果時要標不支援並說出不自己讀TPM()
    {
        var facts = TpmLogFactsService.Collect(At, null);
        var s = facts.Single(f => f.Key == TpmLogFactsService.SummaryKey);

        Assert.Equal(FactAvailability.NotSupported, s.Availability);
        Assert.Contains("不自己去讀 TPM", s.UnavailableReason);
        Assert.Contains("tpm.tcg_log", s.UnavailableReason);
    }

    [Fact]
    public void 沒有事件時要說那與沒有log不同且不填零()
    {
        var facts = TpmLogFactsService.Collect(At, W(false));
        var s = facts.Single(f => f.Key == TpmLogFactsService.SummaryKey);
        var h = facts.Single(f => f.Key == TpmLogFactsService.HistogramKey);

        Assert.Equal(FactAvailability.NotApplicable, s.Availability);
        Assert.Null(s.NumericValue);
        Assert.Contains("「log 裡沒有可解析事件」與「沒有 log」是不同的斷言", s.UnavailableReason);

        Assert.Null(h.NumericValue);
        Assert.Contains("不填 0", h.UnavailableReason);
        Assert.Contains("「0 種型別」與「數不出來」不同", h.UnavailableReason);
    }

    // ── 摘要 ────────────────────────────────────────────────────────────

    [Fact]
    public void 摘要要報出事件數PCR覆蓋與型別種類數()
    {
        var facts = TpmLogFactsService.Collect(At, W(false,
            E(0, 0x01), E(0, 0x01), E(4, 0x0D), E(7, 0x0D)));
        var s = facts.Single(f => f.Key == TpmLogFactsService.SummaryKey);

        Assert.Contains("4 筆事件", s.Value);
        Assert.Contains("覆蓋 3 個 PCR", s.Value);
        Assert.Contains("0、4、7", s.Value);
        Assert.Contains("型別 2 種", s.Value);
        Assert.Equal(4.0, s.NumericValue);
    }

    [Fact]
    public void 摘要要說出事件數多寡本身不代表什麼()
    {
        var facts = TpmLogFactsService.Collect(At, W(false, E(0, 0x01)));
        var s = facts.Single(f => f.Key == TpmLogFactsService.SummaryKey);

        Assert.Contains("事件數多不代表量測完整、少不代表有問題", s.UnavailableReason);
        Assert.Contains("不同平台的 log 粒度本來就不同", s.UnavailableReason);
    }

    [Fact]
    public void 摘要要說出它補上了既有的哪一塊()
    {
        var facts = TpmLogFactsService.Collect(At, W(false, E(0, 0x01)));
        var s = facts.Single(f => f.Key == TpmLogFactsService.SummaryKey);

        Assert.Contains("既有的 tpm.tcg_log 說的是「N 個事件」與 PCR 覆蓋", s.UnavailableReason);
        Assert.Contains("把「型別種類數」補上", s.UnavailableReason);
    }

    // ── 直方圖 ──────────────────────────────────────────────────────────

    [Fact]
    public void 直方圖要按事件數排序並列出編號()
    {
        var facts = TpmLogFactsService.Collect(At, W(false,
            E(0, 0x01), E(0, 0x01), E(0, 0x01), E(4, 0x0D), E(7, 0x0D)));
        var h = facts.Single(f => f.Key == TpmLogFactsService.HistogramKey);

        Assert.Contains("0x00000001×3", h.Value);
        Assert.Contains("0x0000000D×2", h.Value);
        Assert.Equal(2.0, h.NumericValue);
    }

    /// <summary>
    /// <b>這一條守著本項最重要的一句話。</b>
    /// 不得為任何型別編號命名——即使是最有名的那些。
    /// </summary>
    [Fact]
    public void 直方圖不得替任何型別命名()
    {
        // 餵進幾個「有名」的編號，要求輸出裡完全沒有名字。
        var facts = TpmLogFactsService.Collect(At, W(false,
            E(0, 0x00), E(0, 0x01), E(0, 0x04), E(7, 0x0D)));
        var h = facts.Single(f => f.Key == TpmLogFactsService.HistogramKey);

        Assert.DoesNotContain("SEPARATOR", h.Value);
        Assert.DoesNotContain("POST_CODE", h.Value);
        Assert.DoesNotContain("PREBOOT", h.Value);
        Assert.DoesNotContain("EFI_VARIABLE", h.Value);
        Assert.DoesNotContain("EV_", h.Value);
    }

    [Fact]
    public void 直方圖的理由要說出我記得不是出處()
    {
        var facts = TpmLogFactsService.Collect(At, W(false, E(0, 0x01)));
        var h = facts.Single(f => f.Key == TpmLogFactsService.HistogramKey);

        Assert.Contains("只列型別編號與事件數，不命名", h.UnavailableReason);
        Assert.Contains("本專案沒有任何地方收錄那張表", h.UnavailableReason);
        Assert.Contains("「我記得」不是出處", h.UnavailableReason);
        Assert.Contains("填錯的編號會讓一個不存在的東西看起來存在", h.UnavailableReason);
    }

    [Fact]
    public void 直方圖的理由要指出修好的路徑()
    {
        var facts = TpmLogFactsService.Collect(At, W(false, E(0, 0x01)));
        var h = facts.Single(f => f.Key == TpmLogFactsService.HistogramKey);

        Assert.Contains("TCG PC Client 規格的 EV_* 表出處", h.UnavailableReason);
        Assert.Contains("一處改、多處受益", h.UnavailableReason);
    }

    [Fact]
    public void 型別超過十二種時要說出等幾種()
    {
        var events = Enumerable.Range(0, 15).Select(i => E(0, (uint)i)).ToArray();
        var facts = TpmLogFactsService.Collect(At, W(false, events));
        var h = facts.Single(f => f.Key == TpmLogFactsService.HistogramKey);

        Assert.Contains("等 15 種", h.Value);
        Assert.Equal(15.0, h.NumericValue);
    }

    // ── 截斷 ────────────────────────────────────────────────────────────

    [Fact]
    public void 走訪截斷時要說出計數是走到哪算到哪()
    {
        var facts = TpmLogFactsService.Collect(At, W(true, E(0, 0x01), E(4, 0x0D)));
        var s = facts.Single(f => f.Key == TpmLogFactsService.SummaryKey);
        var h = facts.Single(f => f.Key == TpmLogFactsService.HistogramKey);

        Assert.Contains("走訪提前結束", s.UnavailableReason);
        Assert.Contains("走到哪算到哪", s.UnavailableReason);
        Assert.Contains("與「log 裡只有這些事件」是**不同的斷言**", s.UnavailableReason);

        Assert.Contains("走訪提前結束", h.UnavailableReason);
    }

    [Fact]
    public void 沒有事件且走訪截斷時要說出那更可能是損毀()
    {
        var facts = TpmLogFactsService.Collect(At, W(true));
        var s = facts.Single(f => f.Key == TpmLogFactsService.SummaryKey);

        Assert.Contains("這次的走訪是**提前結束**的——那更可能是損毀", s.UnavailableReason);
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = TpmLogFactsService.Collect(At, W(false, E(0, 0x01)))
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = TpmLogFactsService.Collect(At, null)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = TpmLogFactsService.Collect(At, W(false, E(0, 0x01)));
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "TPM log 事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
