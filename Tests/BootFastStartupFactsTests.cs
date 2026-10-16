using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 快速開機與休眠狀態的守門（ULTIMATE-1000 階段二 P2-045）。
///
/// <para>
/// <b>核心是「已啟用」時要說出它影響什麼。</b>快速開機讓「關機」實際上是
/// 休眠到檔案——所以任何「冷啟動」量測量到的不是它說的那件事。
/// 這一項的價值不在那個 0／1，而在它把那個後果說出來。
/// </para>
/// </summary>
public class BootFastStartupFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static BootFastStartupFactsService.Inputs In(
        int? hiberboot = null, int? hibernate = null, int? percent = null)
        => new(hiberboot, hibernate, percent);

    [Fact]
    public void 快速開機啟用時要說出它影響量測的意義()
    {
        var f = BootFastStartupFactsService.Collect(At, In(hiberboot: 1))
            .Single(x => x.Key == BootFastStartupFactsService.FastBootKey);

        Assert.Equal("已啟用", f.Value);
        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("休眠到檔案", f.UnavailableReason);
        Assert.Contains("韌體層面的狀態不會被重建", f.UnavailableReason);
        Assert.Contains("量到的不是它說的那件事", f.UnavailableReason);
    }

    [Fact]
    public void 快速開機停用時要說量的是它說的那件事()
    {
        var f = BootFastStartupFactsService.Collect(At, In(hiberboot: 0))
            .Single(x => x.Key == BootFastStartupFactsService.FastBootKey);

        Assert.StartsWith("已停用", f.Value);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("走完整的初始化路徑", f.Value);
    }

    [Fact]
    public void 沒有這個值要與它被設為零分開()
    {
        var f = BootFastStartupFactsService.Collect(At, In())
            .Single(x => x.Key == BootFastStartupFactsService.FastBootKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("「沒有這個值」與「它被設為 0」是不同的斷言", f.UnavailableReason);
        Assert.Contains("從來沒有這個設定", f.UnavailableReason);
    }

    [Fact]
    public void 快速開機要說它只陳述狀態不判死量測()
    {
        var f = BootFastStartupFactsService.Collect(At, In(hiberboot: 1))
            .Single(x => x.Key == BootFastStartupFactsService.FastBootKey);

        Assert.Contains("只陳述狀態", f.UnavailableReason);
        Assert.Contains("要看那一次走了哪條路徑", f.UnavailableReason);
    }

    [Fact]
    public void 休眠啟用要帶出上限百分比()
    {
        var f = BootFastStartupFactsService.Collect(At, In(hibernate: 1, percent: 40))
            .Single(x => x.Key == BootFastStartupFactsService.HibernateKey);

        Assert.Contains("已啟用", f.Value);
        Assert.Contains("40% 記憶體", f.Value);
    }

    [Fact]
    public void 停用休眠不代表快速開機也停用()
    {
        // 兩者共用休眠檔，但停用其中一個不必然停用另一個（反過來則不一定）。
        var f = BootFastStartupFactsService.Collect(At, In(hibernate: 0, hiberboot: 1))
            .Single(x => x.Key == BootFastStartupFactsService.HibernateKey);

        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("不代表快速開機也停用", f.Value);
        Assert.Contains("反過來則不一定", f.Value);
    }

    [Fact]
    public void 休眠與快速開機要說是同一個機制的兩種用法()
    {
        var f = BootFastStartupFactsService.Collect(At, In(hibernate: 1, percent: 50))
            .Single(x => x.Key == BootFastStartupFactsService.HibernateKey);

        Assert.Contains("同一個機制的兩種用法", f.Value);
    }

    [Fact]
    public void 沒有休眠值要說它從未被設定過()
    {
        var f = BootFastStartupFactsService.Collect(At, In())
            .Single(x => x.Key == BootFastStartupFactsService.HibernateKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("休眠從未被設定過", f.UnavailableReason);
    }

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = BootFastStartupFactsService.Collect(At, In(hiberboot: 1, hibernate: 1, percent: 40))
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = BootFastStartupFactsService.Collect(At, In())
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = BootFastStartupFactsService.Collect(At, In(hiberboot: 1, hibernate: 0));
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "快速開機事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
