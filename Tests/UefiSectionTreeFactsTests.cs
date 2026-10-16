using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 映像結構樹的守門（ULTIMATE-1000 階段二 P2-034）。
///
/// <para>
/// <b>核心是「計數不等於完整」</b>：巢狀容器裡的內容沒有被攤開，
/// 而那件事必須說出來。
/// </para>
/// </summary>
public class UefiSectionTreeFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static UefiFvFile File(string type, params UefiFvSection[] sections)
        => new("AAAA0000-0000-0000-0000-000000000000", type, 0x100, true, false, "TestFile", sections);

    private static UefiFvInfo Fv(int index, params UefiFvFile[] files)
        => new(index, 0x1000, "BBBB0000-0000-0000-0000-000000000000", 0x10000, 2,
            files.Length, 0, 0, true, false, files);

    private static UefiFvSection S(string typeName, ulong size = 0x10, string? extra = null)
        => new(typeName, size, extra, 0x1000);

    [Fact]
    public void 沒有解碼結果時要標不支援()
    {
        var facts = UefiSectionTreeFactsService.Collect(At, null);
        var t = facts.Single(f => f.Key == UefiSectionTreeFactsService.TreeKey);

        Assert.Equal(FactAvailability.NotSupported, t.Availability);
        Assert.Contains("不自己去讀 BIOS 區", t.UnavailableReason);
    }

    [Fact]
    public void 沒有FV時要說那與沒有區段不同()
    {
        var facts = UefiSectionTreeFactsService.Collect(At, []);
        var t = facts.Single(f => f.Key == UefiSectionTreeFactsService.TreeKey);

        Assert.Equal(FactAvailability.NotApplicable, t.Availability);
        Assert.Null(t.NumericValue);
        Assert.Contains("「沒有 FV」與「FV 裡沒有區段」是不同的斷言", t.UnavailableReason);
    }

    [Fact]
    public void 三層計數都要報出來()
    {
        var facts = UefiSectionTreeFactsService.Collect(At,
        [
            Fv(0, File("PEIM", S("PE32"), S("USER_INTERFACE")), File("DRIVER", S("PE32"))),
            Fv(1, File("APPLICATION", S("PE32"))),
        ]);
        var t = facts.Single(f => f.Key == UefiSectionTreeFactsService.TreeKey);

        Assert.Contains("2 個 FV、3 個檔案、4 個區段", t.Value);
        Assert.Equal(4.0, t.NumericValue);
    }

    [Fact]
    public void 區段要依型別彙總()
    {
        var facts = UefiSectionTreeFactsService.Collect(At,
        [
            Fv(0, File("PEIM", S("PE32"), S("PE32"), S("USER_INTERFACE"))),
        ]);
        var t = facts.Single(f => f.Key == UefiSectionTreeFactsService.TreeKey);

        Assert.Contains("PE32×2", t.Value);
        Assert.Contains("USER_INTERFACE×1", t.Value);
    }

    /// <summary>
    /// <b>這一條守著本項最重要的一句話。</b>巢狀容器的內容沒有被攤開，
    /// 而那必須說出來——否則「N 個區段」會被讀成「全部」。
    /// </summary>
    [Fact]
    public void 巢狀容器要如實標示未被攤開()
    {
        var facts = UefiSectionTreeFactsService.Collect(At,
        [
            Fv(0, File("PEIM", S("PE32"), S("COMPRESSION"), S("GUID_DEFINED"),
                S("FIRMWARE_VOLUME_IMAGE"))),
        ]);
        var t = facts.Single(f => f.Key == UefiSectionTreeFactsService.TreeKey);

        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("有 3 個區段本身就是巢狀容器", t.Value);
        Assert.Contains("它們的內容沒有被攤開", t.Value);
    }

    [Fact]
    public void 要說出計數不等於完整()
    {
        var facts = UefiSectionTreeFactsService.Collect(At, [Fv(0, File("PEIM", S("PE32")))]);
        var t = facts.Single(f => f.Key == UefiSectionTreeFactsService.TreeKey);

        Assert.Contains("計數不等於完整", t.Value);
        Assert.Contains("看得見的三層有多大", t.Value);
        Assert.Contains("不是「這顆韌體有幾個區段」", t.Value);
    }

    [Fact]
    public void 型別超過十種時要說出等幾種()
    {
        var sections = Enumerable.Range(0, 12)
            .Select(i => S($"TYPE{i:00}")).ToArray();
        var facts = UefiSectionTreeFactsService.Collect(At, [Fv(0, File("PEIM", sections))]);
        var t = facts.Single(f => f.Key == UefiSectionTreeFactsService.TreeKey);

        Assert.Contains("等 12 種型別", t.Value);
    }

    [Fact]
    public void 形狀鍵要報出樹是扁的還是深的()
    {
        // FV 2 個、檔案 3 個（第二個 FV 有 2 個）、區段 4 個。
        var facts = UefiSectionTreeFactsService.Collect(At,
        [
            Fv(0, File("PEIM", S("PE32"), S("PE32"))),
            Fv(1, File("DRIVER", S("PE32")), File("APPLICATION", S("PE32"))),
        ]);
        var d = facts.Single(f => f.Key == UefiSectionTreeFactsService.DepthKey);

        // 檔案 3／FV 2 = 1.5；區段 4／檔案 3 = 1.3（**不是 1.5**——
        // 第一版我把兩個斷言都寫成 1.5，而第二個是錯的。
        // 被上一次 `4+4+4+2+20+4+1` 的教訓提醒，這次先算再加。）
        Assert.Contains("檔案 1.5/FV", d.Value);
        Assert.Contains("區段 1.3/檔案", d.Value);
    }

    [Fact]
    public void 形狀鍵要說出平均值不代表健康()
    {
        var facts = UefiSectionTreeFactsService.Collect(At, [Fv(0, File("PEIM", S("PE32")))]);
        var d = facts.Single(f => f.Key == UefiSectionTreeFactsService.DepthKey);

        Assert.Contains("平均值低不代表健康、高不代表有問題", d.Value);
        Assert.Contains("扁的還是深的", d.Value);
    }

    [Fact]
    public void 沒有FV時形狀鍵要回空而不是除以零()
    {
        var facts = UefiSectionTreeFactsService.Collect(At, []);
        var d = facts.Single(f => f.Key == UefiSectionTreeFactsService.DepthKey);

        Assert.StartsWith("（空）", d.Value);
        Assert.Null(d.NumericValue);
    }

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = UefiSectionTreeFactsService.Collect(At, [Fv(0, File("PEIM", S("PE32")))])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = UefiSectionTreeFactsService.Collect(At, null)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = UefiSectionTreeFactsService.Collect(At, [Fv(0, File("PEIM", S("PE32")))]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "結構樹事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
