using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 開機模式與韌體版本事實的守門（ULTIMATE-1000 階段二 P2-040／P2-041）。
///
/// <para>
/// <b>核心是「目前用哪一個」與「支援哪一個」之別。</b>
/// 一台機器可以同時有 CSM 與 UEFI，而「這台是 UEFI 機器」這句話
/// 在兩種讀法下意思完全不同。本服務只回答前者。
/// </para>
/// </summary>
public class FirmwareModeFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static FirmwareModeFactsService.ModeInputs In(
        int? fwType = null, string? biosVendor = null, string? biosVersion = null,
        string? biosDate = null, string? systemVendor = null)
        => new(fwType, biosVendor, biosVersion, biosDate, systemVendor);

    // ── 開機模式 ────────────────────────────────────────────────────────

    [Fact]
    public void UEFI要標Present並說那是這一次的路徑()
    {
        var f = FirmwareModeFactsService.Collect(At, In(fwType: 2))
            .Single(x => x.Key == FirmwareModeFactsService.ModeKey);

        // 值一定是「<值>。<說明>」——說明是這一項的實質內容，所以比對前綴而非全等；
        // 而說明住在 Value 裡（不變式：Present 的事實不得帶理由欄）。
        Assert.StartsWith("UEFI", f.Value);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("這一次開機走的路徑", f.Value);
        Assert.Contains("不是「這台機器支援什麼」", f.Value);
    }

    [Fact]
    public void Legacy要說出它影響什麼()
    {
        var f = FirmwareModeFactsService.Collect(At, In(fwType: 1))
            .Single(x => x.Key == FirmwareModeFactsService.ModeKey);

        Assert.Contains("Legacy BIOS", f.Value);
        Assert.Contains("Secure Boot", f.Value);
        Assert.Contains("2 TiB", f.Value);
        Assert.Contains("不代表沒有 UEFI 支援", f.Value);
    }

    [Fact]
    public void 未知型別要標Unknown而不是猜()
    {
        var f = FirmwareModeFactsService.Collect(At, In(fwType: 0))
            .Single(x => x.Key == FirmwareModeFactsService.ModeKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("未知", f.Value);
    }

    [Fact]
    public void 取不到型別時要說不從其他跡象推測()
    {
        var f = FirmwareModeFactsService.Collect(At, In(fwType: null))
            .Single(x => x.Key == FirmwareModeFactsService.ModeKey);

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("不從其他跡象推測", f.UnavailableReason);
        Assert.Contains("只說明「支援」而不是「這次用了哪一個」", f.UnavailableReason);
    }

    // ── 廠商：兩個不同的東西 ────────────────────────────────────────────

    [Fact]
    public void BIOS廠與系統廠要並列不合併()
    {
        var f = FirmwareModeFactsService.Collect(At,
            In(biosVendor: "American Megatrends Inc.", systemVendor: "Dell Inc."))
            .Single(x => x.Key == FirmwareModeFactsService.VendorKey);

        Assert.Contains("BIOS 廠 American Megatrends Inc.", f.Value);
        Assert.Contains("系統廠 Dell Inc.", f.Value);
        Assert.Contains("是不同的兩件事", f.Value);
        Assert.Contains("誰做的韌體", f.Value);
    }

    [Fact]
    public void 只有一個廠商時只報那一個()
    {
        var f = FirmwareModeFactsService.Collect(At, In(biosVendor: "AMI"))
            .Single(x => x.Key == FirmwareModeFactsService.VendorKey);

        Assert.Contains("BIOS 廠 AMI", f.Value);
        // **不能直接比對「系統廠」那三個字**：不變式把說明併進了 Value，
        // 而那段說明本身就在講「BIOS 廠與系統廠是不同的兩件事」。
        // 要驗的是「有沒有多出一個系統廠欄位」——而欄位的形式是「系統廠 <名字>」
        // （兩個廠商並列時用「；」分隔）。
        Assert.DoesNotContain("系統廠 ", f.Value);
    }

    [Fact]
    public void 兩個廠商都沒有時要說合併會讓問題失去答案()
    {
        var f = FirmwareModeFactsService.Collect(At, In())
            .Single(x => x.Key == FirmwareModeFactsService.VendorKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("合成一個「廠商」欄位", f.UnavailableReason);
    }

    // ── 版本與日期 ──────────────────────────────────────────────────────

    [Fact]
    public void 韌體版本要說它與SMBIOS是兩個獨立來源()
    {
        var f = FirmwareModeFactsService.Collect(At, In(biosVersion: "2.85"))
            .Single(x => x.Key == FirmwareModeFactsService.VersionKey);

        Assert.StartsWith("2.85", f.Value);
        Assert.Contains("兩個獨立來源", f.Value);
        Assert.Contains("常被 OEM 覆寫成自己的版號", f.Value);
        Assert.Contains("不是錯誤", f.Value);
    }

    [Fact]
    public void 沒有版本時要指出SMBIOS是另一個來源()
    {
        var f = FirmwareModeFactsService.Collect(At, In())
            .Single(x => x.Key == FirmwareModeFactsService.VersionKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("SMBIOS Type 0 是另一個獨立來源", f.UnavailableReason);
    }

    [Fact]
    public void 發行日要拆成可讀格式並保留原始字串()
    {
        var f = FirmwareModeFactsService.Collect(At, In(biosDate: "20240315000000.000000+000"))
            .Single(x => x.Key == FirmwareModeFactsService.DateKey);

        Assert.Contains("2024-03-15", f.Value);
        Assert.Contains("原始 20240315000000.000000+000", f.Value);
    }

    [Fact]
    public void 發行日要說那是版本發行日不是更新時間()
    {
        var f = FirmwareModeFactsService.Collect(At, In(biosDate: "20240315000000.000000+000"))
            .Single(x => x.Key == FirmwareModeFactsService.DateKey);

        Assert.Contains("不是「這台機器上一次更新韌體的時間」", f.Value);
    }

    [Fact]
    public void 非預期格式的日期要原樣呈現不強行解析()
    {
        var f = FirmwareModeFactsService.Collect(At, In(biosDate: "unknown"))
            .Single(x => x.Key == FirmwareModeFactsService.DateKey);

        Assert.StartsWith("unknown", f.Value);
        // 「原樣呈現」的保證由「沒有（原始 …）後綴」把守
        // （解析過的版本是「2024-03-15（原始 …）」）；說明被併進 Value 不影響這件事。
        Assert.DoesNotContain("（原始", f.Value);
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = FirmwareModeFactsService.Collect(At, In(fwType: 2, biosVendor: "A", biosVersion: "1"))
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = FirmwareModeFactsService.Collect(At, In())
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = FirmwareModeFactsService.Collect(At,
            In(fwType: 2, biosVendor: "A", biosVersion: "1", biosDate: "20240101000000.000000+000",
               systemVendor: "B"));
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "韌體事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
