using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 選項 ROM 盤點的守門（ULTIMATE-1000 階段二 P2-051）。
///
/// <para>
/// <b>核心是「自述有 ROM BAR」與「有選項 ROM」之別。</b>
/// 有些裝置的 ROM 由系統韌體在 POST 階段複製到記憶體後就停用了 ROM BAR
/// ——那時它仍然有選項 ROM，只是這個途徑看不到。
/// </para>
/// </summary>
public class OptionRomFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static OptionRomFactsService.Device D(string name, bool hasRom,
        long? bytes = null, string? error = null)
        => new(name, "PCI\\VEN_0000", hasRom, bytes, error);

    [Fact]
    public void 列舉不到時要說兩種可能分不出來()
    {
        var facts = OptionRomFactsService.Collect(At, []);
        var c = facts.Single(f => f.Key == OptionRomFactsService.CountKey);

        Assert.Equal(FactAvailability.NotApplicable, c.Availability);
        Assert.Contains("這可能是真的沒有", c.UnavailableReason);
        Assert.Contains("兩者在這裡分不出來", c.UnavailableReason);
    }

    [Fact]
    public void 有ROM的裝置要列出大小()
    {
        var facts = OptionRomFactsService.Collect(At,
            [D("NVIDIA GeForce RTX 4090", true, 128 * 1024), D("Intel I225-V", false)]);
        var l = facts.Single(f => f.Key == OptionRomFactsService.ListKey);

        Assert.Contains("NVIDIA GeForce RTX 4090（ROM BAR 128 KiB）", l.Value);
        Assert.DoesNotContain("I225-V", l.Value);
    }

    [Fact]
    public void 計數只算自述有ROM的()
    {
        var facts = OptionRomFactsService.Collect(At,
            [D("A", true, 65536), D("B", false), D("C", true, 32768)]);
        var c = facts.Single(f => f.Key == OptionRomFactsService.CountKey);

        Assert.Equal(2.0, c.NumericValue);
        Assert.Contains("A（ROM BAR 64 KiB）", c.Value);
        Assert.Contains("C（ROM BAR 32 KiB）", c.Value);
    }

    [Fact]
    public void 沒有自述有ROM時要說那與沒有選項ROM不同()
    {
        var facts = OptionRomFactsService.Collect(At, [D("A", false), D("B", false)]);
        var c = facts.Single(f => f.Key == OptionRomFactsService.CountKey);

        Assert.Contains("列舉到 2 個裝置", c.Value);
        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("「沒有自述有 ROM BAR」與「沒有選項 ROM」是不同的斷言", c.Value);
        Assert.Contains("停用了 ROM BAR", c.Value);
    }

    [Fact]
    public void 自述有ROM不代表讀到了內容()
    {
        var facts = OptionRomFactsService.Collect(At, [D("A", true, 65536)]);
        var c = facts.Single(f => f.Key == OptionRomFactsService.CountKey);

        Assert.Contains("不代表我讀到了它的內容", c.Value);
        Assert.Contains("讀取需要核心層權限，本服務不讀", c.Value);
    }

    [Fact]
    public void 清單要說出選項ROM與主機板韌體是兩套程式碼()
    {
        var facts = OptionRomFactsService.Collect(At, [D("A", true, 65536)]);
        var l = facts.Single(f => f.Key == OptionRomFactsService.ListKey);

        Assert.Contains("跟主機板韌體是兩套獨立的程式碼", l.Value);
        Assert.Contains("更新主機板 BIOS 不會更新它們", l.Value);
        Assert.Contains("要刷顯示卡", l.Value);
    }

    [Fact]
    public void 清單要說出它列的是什麼不是什麼()
    {
        var facts = OptionRomFactsService.Collect(At, [D("A", true, 65536)]);
        var l = facts.Single(f => f.Key == OptionRomFactsService.ListKey);

        Assert.Contains("列的是「哪些裝置自述有」，不是「它們是什麼版本」", l.Value);
    }

    [Fact]
    public void 沒有大小的裝置只列名稱()
    {
        var facts = OptionRomFactsService.Collect(At, [D("A", true), D("B", true, 32768)]);
        var l = facts.Single(f => f.Key == OptionRomFactsService.ListKey);

        // 斷言「A 沒有被附上大小」，而不是猜輸出裡的字串順序。
        //
        // 第一版寫的是 Assert.Contains("；A", l.Value.Replace("A（", "A（"))
        // ——那個 Replace 是把自己換成自己，一個什麼都不做卻看起來像在做事的方法呼叫。
        // **它掩蓋的是「我其實不確定該斷言什麼」**；而正確的做法是把
        // 真正要驗的事情說出來：A 出現，且它後面沒有 ROM BAR 的大小。
        Assert.Contains("A", l.Value);
        Assert.DoesNotContain("A（ROM BAR", l.Value);
        Assert.Contains("B（ROM BAR 32 KiB）", l.Value);
    }

    [Fact]
    public void 真實列舉要如實回空而不是假裝列舉過()
    {
        // 本專案不在這裡做 PCI 組態空間的硬體存取。
        // **一個假的列舉結果比沒有結果更糟，因為它會讓使用者以為這條路是通的。**
        var devices = OptionRomFactsService.ReadAll();
        Assert.Empty(devices);

        var facts = OptionRomFactsService.Collect(At);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = OptionRomFactsService.Collect(At, [D("A", true, 65536)]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "選項 ROM 事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
