using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 映像 PE32 模組清單的守門（ULTIMATE-1000 階段二 P2-035）。
///
/// <para>
/// <b>核心是「規格說『驅動與應用』，而 PE 只說子系統」。</b>
/// 本服務報子系統並說出落差，而不是把 NATIVE 直接標成「驅動」。
/// </para>
/// </summary>
public class UefiPeModuleFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 造一個最小的合法 PE：DOS 頭（e_lfanew@0x3C）＋ PE 簽章 ＋ 檔案標頭
    /// ＋ 選擇性標頭（魔術值 ＋ 子系統@+68）＋ 資料目錄。
    /// </summary>
    private static byte[] Pe(ushort machine = 0x8664, ushort subsystem = 1, ushort magic = 0x20B)
    {
        var b = new byte[0x200];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(b, 0x3C);          // e_lfanew
        b[0x80] = (byte)'P'; b[0x81] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(b, 0x84);        // Machine
        BitConverter.GetBytes((ushort)0).CopyTo(b, 0x86);      // NumberOfSections
        BitConverter.GetBytes((ushort)(magic == 0x20B ? 0xF0 : 0xE0)).CopyTo(b, 0x94); // SizeOfOptionalHeader
        int opt = 0x98;
        BitConverter.GetBytes(magic).CopyTo(b, opt);           // Magic
        BitConverter.GetBytes(subsystem).CopyTo(b, opt + 68);  // Subsystem
        return b;
    }

    /// <summary>PE32 區段：4 位元組表頭（size 3 bytes ＋ type）＋ PE 內容。</summary>
    private static byte[] Pe32Section(byte[] pe)
    {
        int size = 4 + pe.Length;
        var s = new byte[size];
        s[0] = (byte)size; s[1] = (byte)(size >> 8); s[2] = (byte)(size >> 16);
        s[3] = 0x10;   // EFI_SECTION_PE32
        Array.Copy(pe, 0, s, 4, pe.Length);
        return s;
    }

    private static (byte[] Image, IReadOnlyList<UefiFvInfo> Fvs) Build(byte[] pe)
    {
        var fv = UefiFvTestData.BuildFv(
            [UefiFvTestData.FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x07,
                [UefiFvTestData.UiSection("DxeDriver"), Pe32Section(pe)])]);
        return (fv, UefiFv.DecodeFvs(fv));
    }

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有解碼結果時要標不支援並說出不自己讀BIOS區()
    {
        var facts = UefiPeModuleFactsService.Collect(At, null, null);
        var c = facts.Single(f => f.Key == UefiPeModuleFactsService.CountKey);

        Assert.Equal(FactAvailability.NotSupported, c.Availability);
        Assert.Contains("不自己去讀 BIOS 區", c.UnavailableReason);
    }

    [Fact]
    public void 沒有FV時要說那與沒有PE32不同()
    {
        var facts = UefiPeModuleFactsService.Collect(At, [], []);
        var c = facts.Single(f => f.Key == UefiPeModuleFactsService.CountKey);

        Assert.Equal(FactAvailability.NotApplicable, c.Availability);
        Assert.Contains("「沒有 FV」與「FV 裡沒有 PE32」是不同的斷言", c.UnavailableReason);
    }

    [Fact]
    public void 有FV但沒有PE32時要說那來自一次成功的走訪()
    {
        var fv = UefiFvTestData.FvWithOneFile();
        var fvs = UefiFv.DecodeFvs(fv);
        var facts = UefiPeModuleFactsService.Collect(At, fvs, fv);
        var c = facts.Single(f => f.Key == UefiPeModuleFactsService.CountKey);

        Assert.Equal(FactAvailability.NotApplicable, c.Availability);
        Assert.Contains("「沒有 PE32 區段」與「走得進不去」是不同的斷言", c.UnavailableReason);
        Assert.Contains("整個映像被壓縮成單一區段", c.UnavailableReason);
    }

    // ── 解析成功 ────────────────────────────────────────────────────────

    [Fact]
    public void 要認出PE32並報機器與子系統()
    {
        var (image, fvs) = Build(Pe(subsystem: 1));
        var facts = UefiPeModuleFactsService.Collect(At, fvs, image);
        var c = facts.Single(f => f.Key == UefiPeModuleFactsService.CountKey);

        Assert.Equal(1.0, c.NumericValue);
        Assert.Contains("NATIVE", c.Value);
        Assert.Equal(FactAvailability.Present, c.Availability);
    }

    [Fact]
    public void 清單要含UI名與FV編號()
    {
        var (image, fvs) = Build(Pe());
        var facts = UefiPeModuleFactsService.Collect(At, fvs, image);
        var l = facts.Single(f => f.Key == UefiPeModuleFactsService.ListKey);

        Assert.Contains("DxeDriver", l.Value);
        Assert.Contains("FV#0", l.Value);
    }

    /// <summary>
    /// <b>這一條守著本項最重要的一句話。</b>規格說「驅動與應用」，
    /// 而 PE 只說子系統——本服務不得把 NATIVE 直接標成「驅動」。
    /// </summary>
    [Fact]
    public void 要說出子系統與驅動的落差而不是直接標成驅動()
    {
        var (image, fvs) = Build(Pe(subsystem: 1));
        var facts = UefiPeModuleFactsService.Collect(At, fvs, image);
        var c = facts.Single(f => f.Key == UefiPeModuleFactsService.CountKey);

        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("PE 的子系統只分 NATIVE／GUI／CUI", c.Value);
        Assert.Contains("是慣例不是定義", c.Value);
        Assert.Contains("不直接把 NATIVE 標成「驅動」", c.Value);
    }

    [Fact]
    public void 要說出只走頂層()
    {
        var (image, fvs) = Build(Pe());
        var facts = UefiPeModuleFactsService.Collect(At, fvs, image);
        var l = facts.Single(f => f.Key == UefiPeModuleFactsService.ListKey);

        Assert.Contains("只走頂層", l.Value);
        Assert.Contains("壓縮區段裡的 FV", l.Value);
        Assert.Contains("看得見的那一層", l.Value);
    }

    // ── 解析失敗 ────────────────────────────────────────────────────────

    [Fact]
    public void 不是PE的內容要如實標失敗並帶原因()
    {
        var (image, fvs) = Build(new byte[0x100]);   // 全零：不是 MZ 開頭
        var facts = UefiPeModuleFactsService.Collect(At, fvs, image);
        var c = facts.Single(f => f.Key == UefiPeModuleFactsService.CountKey);
        var l = facts.Single(f => f.Key == UefiPeModuleFactsService.ListKey);

        Assert.Equal(FactAvailability.Unknown, c.Availability);
        Assert.Contains("1 個解析失敗", c.Value);
        Assert.Contains("解析失敗：", l.Value);
        Assert.Contains("只代表本版解不開", c.UnavailableReason);
    }

    [Fact]
    public void 解析失敗的模組其餘欄位不代填()
    {
        var (image, fvs) = Build(new byte[0x100]);
        var facts = UefiPeModuleFactsService.Collect(At, fvs, image);
        var l = facts.Single(f => f.Key == UefiPeModuleFactsService.ListKey);

        // 不得憑空出現機器或子系統名稱。
        Assert.DoesNotContain("AMD64", l.Value);
        Assert.DoesNotContain("NATIVE", l.Value);
    }

    [Fact]
    public void 清單有上限時要說明只計數不列出()
    {
        var files = Enumerable.Range(0, 25)
            .Select(i => UefiFvTestData.FfsFile(
                $"{i:D8}-AD6B-4F3A-B60B-F59899003443", 0x07,
                [UefiFvTestData.UiSection($"M{i}"), Pe32Section(Pe())]))
            .ToList();
        var fv = UefiFvTestData.BuildFv(files);
        var fvs = UefiFv.DecodeFvs(fv);

        var facts = UefiPeModuleFactsService.Collect(At, fvs, fv);
        var c = facts.Single(f => f.Key == UefiPeModuleFactsService.CountKey);
        var l = facts.Single(f => f.Key == UefiPeModuleFactsService.ListKey);

        Assert.Equal(25.0, c.NumericValue);
        Assert.Contains("清單上限 20", l.Value);
        Assert.Contains("只計數不列出", l.Value);
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var (image, fvs) = Build(Pe());
        var ok = UefiPeModuleFactsService.Collect(At, fvs, image)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = UefiPeModuleFactsService.Collect(At, null, null)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var (image, fvs) = Build(Pe());
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = UefiPeModuleFactsService.Collect(At, fvs, image);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "PE32 模組事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
