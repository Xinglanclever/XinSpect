using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 映像截斷與突變耐受的守門（ULTIMATE-1000 階段二 P2-037）。
///
/// <para>
/// <b>這一組的後半是突變測試</b>——而它與本專案其他測試是不同的類型：
/// 其他測試驗「欄位讀對了沒」，這一組驗「<b>讀不到的時候會不會出事</b>」。
/// </para>
/// </summary>
public class UefiParseGapsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static UefiFvInfo Fv(int index, int skipped, int files = 3, ulong offset = 0x1000,
        ulong length = 0x10000)
        => new(index, offset, "00000000-0000-0000-0000-000000000000", length, 2,
            files, skipped, 0, true, false, []);

    // ── 缺口事實 ────────────────────────────────────────────────────────

    [Fact]
    public void 沒有解碼結果時要標不支援並說出不自己讀BIOS區()
    {
        var facts = UefiParseGapsFactsService.Collect(At, null);
        var g = facts.Single(f => f.Key == UefiParseGapsFactsService.GapsKey);

        Assert.Equal(FactAvailability.NotSupported, g.Availability);
        Assert.Contains("不自己去讀 BIOS 區", g.UnavailableReason);
    }

    [Fact]
    public void 沒有FV時要說那與沒有缺口不同()
    {
        var facts = UefiParseGapsFactsService.Collect(At, []);
        var g = facts.Single(f => f.Key == UefiParseGapsFactsService.GapsKey);

        Assert.Equal(FactAvailability.NotApplicable, g.Availability);
        Assert.Contains("「沒有 FV」與「FV 裡沒有缺口」是不同的斷言", g.UnavailableReason);
        Assert.Contains("根本沒有走訪可以談", g.UnavailableReason);
    }

    [Fact]
    public void 全部自然結束時零是有意義的()
    {
        var facts = UefiParseGapsFactsService.Collect(At, [Fv(0, 0), Fv(1, 0)]);
        var g = facts.Single(f => f.Key == UefiParseGapsFactsService.GapsKey);

        Assert.Equal(0.0, g.NumericValue);
        Assert.Contains("2 個 FV 的走訪都自然結束", g.Value);
        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("「走到自由空間」不是缺口", g.Value);
        Assert.Contains("0 這個數字是有意義的", g.Value);
    }

    [Fact]
    public void 有缺口時要列出逐FV的細節()
    {
        var facts = UefiParseGapsFactsService.Collect(At,
            [Fv(0, 0), Fv(1, 2, files: 5, offset: 0x20000, length: 0x8000)]);
        var g = facts.Single(f => f.Key == UefiParseGapsFactsService.GapsKey);

        Assert.Equal(2.0, g.NumericValue);
        Assert.Contains("1 個 FV 有缺口，共 2 處", g.Value);
        Assert.Contains("FV #1 跳過 2 處", g.Value);
        Assert.Contains("解出 5 個檔案", g.Value);
        Assert.DoesNotContain("FV #0", g.Value);
    }

    [Fact]
    public void 有缺口時要說那不必然代表韌體壞了()
    {
        var facts = UefiParseGapsFactsService.Collect(At, [Fv(0, 1)]);
        var g = facts.Single(f => f.Key == UefiParseGapsFactsService.GapsKey);

        Assert.Contains("缺口不是「這顆韌體壞了」", g.Value);
        Assert.Contains("高度壓縮的映像", g.Value);
        Assert.Contains("不判斷那是不是問題", g.Value);
    }

    // ── 突變耐受 ────────────────────────────────────────────────────────

    [Fact]
    public void 突變耐受要說出它證明的範圍()
    {
        var facts = UefiParseGapsFactsService.Collect(At, [Fv(0, 0)]);
        var t = facts.Single(f => f.Key == UefiParseGapsFactsService.TolerantKey);

        Assert.Equal(FactAvailability.Present, t.Availability);
        Assert.Contains("只證明「這幾種輸入不丟例外」", t.Value);
        Assert.Contains("不證明「所有異常輸入都安全」", t.Value);
        Assert.Contains("測試通過的範圍就是它證明的範圍", t.Value);
    }

    /// <summary>
    /// <b>真正的突變組。</b>這一條直接打解碼器，餵它各種壞輸入。
    /// 沒有這一條，上面那句「不丟例外」就只是宣稱。
    /// </summary>
    [Fact]
    public void 解碼器對各種異常輸入都不得丟例外()
    {
        var cases = new List<(string Name, byte[] Data)>
        {
            ("空陣列", []),
            ("1 byte", [0x5F]),
            ("只有 _FVH 前綴", [(byte)'_', (byte)'F', (byte)'V', (byte)'H']),
            ("全零 64 bytes", new byte[64]),
            ("全 FF 4 KiB", Enumerable.Repeat((byte)0xFF, 4096).ToArray()),
            ("亂數 1 KiB", Random(1024, seed: 1)),
            ("亂數 64 KiB", Random(65536, seed: 2)),
            ("合法前綴後截斷", TruncatedFv()),
            ("長度欄位極大", HugeLengthFv()),
        };

        var failures = new List<string>();
        foreach (var (name, data) in cases)
        {
            try
            {
                var fvs = UefiFv.DecodeFvs(data);
                // 不要求結果正確，只要求「有結果且不丟例外」。
                Assert.NotNull(fvs);
            }
            catch (Exception ex)
            {
                failures.Add($"{name}：{ex.GetType().Name}——{ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            "解碼器對異常輸入丟出了例外（**那是缺陷，不該被吞掉**）：\n"
            + string.Join("\n", failures.Select(f => "  • " + f)));
    }

    [Fact]
    public void 截斷的輸入要停在合理的地方而不是漫遊()
    {
        var full = TruncatedFv();
        var half = full[..(full.Length / 2)];

        var fvs = UefiFv.DecodeFvs(half);

        // 不要求它認出什麼，只要求它「很快停下來」——
        // 沒有這個界線，一個壞長度欄位會讓它掃過整段記憶體。
        Assert.NotNull(fvs);
        Assert.True(fvs.Count <= 1, $"截斷輸入解出了 {fvs.Count} 個 FV——那代表它沒有如實停下");
    }

    [Fact]
    public void 缺口計數要與解碼器的輸出一致()
    {
        // 第一版用隨機輸入跑 DecodeFvs 再比對——那是**對一個我不控制的輸入斷言**：
        // 亂數剛好解不出 FV 時，走的是「沒有 FV」那條分支，NumericValue 是 null，
        // 而那時測試期望 0，於是紅燈。**修正的方向是控制輸入，不是放寬期望。**
        var fvs = UefiFv.DecodeFvs(ValidFv());
        Assert.True(fvs.Count > 0, "合成輸入應該要能解出至少一個 FV——否則這一條驗不到東西");

        var facts = UefiParseGapsFactsService.Collect(At, fvs);
        var g = facts.Single(f => f.Key == UefiParseGapsFactsService.GapsKey);

        double expected = fvs.Sum(f => (double)f.SkippedCount);
        Assert.Equal(expected, g.NumericValue);
    }

    /// <summary>
    /// 沒有走訪時，缺口數必須是 <c>null</c> 而不是 <c>0</c>。
    ///
    /// <para>
    /// <b>這是那一條線的另一半：</b>「沒有 FV」與「FV 裡沒有缺口」是不同的斷言，
    /// 而 <c>0</c> 會讓前者看起來像後者。
    /// </para>
    /// </summary>
    [Fact]
    public void 沒有走訪時缺口數必須是null而不是零()
    {
        var facts = UefiParseGapsFactsService.Collect(At, []);
        var g = facts.Single(f => f.Key == UefiParseGapsFactsService.GapsKey);

        Assert.Null(g.NumericValue);
        Assert.Equal(FactAvailability.NotApplicable, g.Availability);
    }

    // ── 輔助 ────────────────────────────────────────────────────────────

    private static byte[] Random(int size, int seed)
    {
        var r = new Random(seed);
        var b = new byte[size];
        r.NextBytes(b);
        return b;
    }

    /// <summary>
    /// 合成一個合法的 FV——**用 <see cref="UefiFvTestData"/> 的共用建構器**。
    ///
    /// <para>
    /// 第一版我在這裡自己猜了一個 FV 格式，漏了 FileSystemGuid、Attributes、
    /// block map 與表頭校驗和四個欄位，被自己的守門擋下。
    /// **兩份 FV 合成器就是兩處漂移的來源**——所以直接複用既有的那一份。
    /// </para>
    /// </summary>
    private static byte[] ValidFv() => UefiFvTestData.FvWithOneFile();

    /// <summary>一個合法 FV 被切掉後半——考驗走訪遇到截斷時停得下來。</summary>
    private static byte[] TruncatedFv()
    {
        var full = UefiFvTestData.FvWithOneFile();
        return full[..(full.Length * 3 / 4)];
    }

    /// <summary>長度欄位被填成極大值，考驗走訪會不會漫遊出去。</summary>
    private static byte[] HugeLengthFv()
    {
        var b = new byte[4096];
        int pos = 40;
        System.Text.Encoding.ASCII.GetBytes("_FVH").CopyTo(b, pos + 40);
        BitConverter.GetBytes((uint)0xFFFFFFF0).CopyTo(b, pos + 32);
        return b;
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = UefiParseGapsFactsService.Collect(At, [Fv(0, 0)])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = UefiParseGapsFactsService.Collect(At, null)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = UefiParseGapsFactsService.Collect(At, [Fv(0, 1)]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "缺口事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
