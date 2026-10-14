using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>FPDT 解碼（FW-024）單元測試。重點在缺席與 0 秒的分別，以及負值不回 0。</summary>
public class AcpiFpdtFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    // ── 建表工具 ─────────────────────────────────────────────────────────

    private static byte[] Header(string sig, int length)
    {
        var b = new byte[36];
        System.Text.Encoding.ASCII.GetBytes(sig).CopyTo(b, 0);
        BitConverter.GetBytes((uint)length).CopyTo(b, 4);
        return b;
    }

    /// <summary>造一張 FPDT：主表頭＋一筆 FBPT＋一筆 Basic Boot 紀錄。</summary>
    private static byte[] BuildFpdt(long resetEnd, long osLoaderLoad, long exitBootEntry, long exitBootExit)
    {
        const int fbptLen = 16 + 4 + 44;
        var table = new byte[36 + fbptLen];
        System.Text.Encoding.ASCII.GetBytes("FPDT").CopyTo(table, 0);
        BitConverter.GetBytes((uint)table.Length).CopyTo(table, 4);
        System.Text.Encoding.ASCII.GetBytes("FBPT").CopyTo(table, 36);
        BitConverter.GetBytes((uint)fbptLen).CopyTo(table, 40);
        table[44] = 1;                                                  // Revision
        BitConverter.GetBytes(0x00000001u).CopyTo(table, 52);           // Type = Basic Boot
        int t = 56;                                                     // 表頭16＋型別4 之後
        // Reserved(4) 在 t..t+3，時間欄位從 t+4 起
        BitConverter.GetBytes(resetEnd).CopyTo(table, t + 4);
        BitConverter.GetBytes(osLoaderLoad).CopyTo(table, t + 12);
        BitConverter.GetBytes(exitBootEntry).CopyTo(table, t + 28);
        BitConverter.GetBytes(exitBootExit).CopyTo(table, t + 36);
        return table;
    }

    private sealed class FakeAcpi(byte[]? table, bool available = true, string? reason = null) : IAcpiTableSource
    {
        public bool Available => available;
        public string? UnavailableReason => reason;
        public IReadOnlyList<byte[]> ReadAll() => table is null ? [] : [table];
    }

    // ── 解碼 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 解碼_三個階段時長都算得出來()
    {
        // 時間欄位是「自開機起的絕對時間」，100 ns 單位。
        // resetEnd 1.0s、osLoaderLoad 3.5s、exitBootEntry 3.8s、exitBootExit 4.0s
        var table = BuildFpdt(10_000_000, 35_000_000, 38_000_000, 40_000_000);
        var phases = AcpiFpdtFactsService.Decode(table);
        Assert.Equal(3, phases.Count);
        Assert.Equal("Firmware Basic Boot", phases[0].Phase);
        Assert.Equal(2.5, phases[0].FirmwareSeconds!.Value, 3);   // 3.5 − 1.0
        Assert.Equal(0.5, phases[1].FirmwareSeconds!.Value, 3);   // 4.0 − 3.5
        Assert.Equal(0.2, phases[2].FirmwareSeconds!.Value, 3);   // 4.0 − 3.8
    }

    [Fact]
    public void 解碼_不是FPDT簽章回空()
        => Assert.Empty(AcpiFpdtFactsService.Decode(BuildFpdt(1, 2, 3, 4).Select((b, i) => i < 4 ? (byte)'X' : b).ToArray()));

    [Fact]
    public void 解碼_null或過短回空()
    {
        Assert.Empty(AcpiFpdtFactsService.Decode(null));
        Assert.Empty(AcpiFpdtFactsService.Decode(new byte[4]));
    }

    [Fact]
    public void 解碼_只有主表沒有FBPT紀錄回空()
    {
        var table = Header("FPDT", 36);
        Assert.Empty(AcpiFpdtFactsService.Decode(table));
    }

    [Fact]
    public void 解碼_時間軸回捲不回零而是略過()
    {
        // OS 載入時間早於 reset 結束＝紀錄沒填完；出 0 秒會被讀成「很快」。
        var table = BuildFpdt(50_000_000, 10_000_000, 0, 0);
        var phases = AcpiFpdtFactsService.Decode(table);
        Assert.DoesNotContain(phases, p => p.Phase == "Firmware Basic Boot");
    }

    [Fact]
    public void 解碼_全零欄位當成未填不當成零秒()
    {
        var table = BuildFpdt(0, 0, 0, 0);
        Assert.Empty(AcpiFpdtFactsService.Decode(table));
    }

    [Fact]
    public void 解碼_紀錄長度不合理時不會卡在同一筆()
    {
        // 把 FBPT 的 Length 寫成 0：解碼器必須還能把 offset 往前推（用最小長度），不能無限迴圈。
        var table = BuildFpdt(10_000_000, 35_000_000, 38_000_000, 40_000_000);
        BitConverter.GetBytes(0u).CopyTo(table, 40);
        var phases = AcpiFpdtFactsService.Decode(table);   // 不應拋例外或無限迴圈
        Assert.NotNull(phases);
    }

    // ── 事實值 ───────────────────────────────────────────────────────────

    [Fact]
    public void 事實_有表時列出各階段()
    {
        var acpi = new FakeAcpi(BuildFpdt(10_000_000, 35_000_000, 38_000_000, 40_000_000));
        var f = AcpiFpdtFactsService.Collect(acpi, At);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("共 3 個階段", f.Value);
        Assert.Contains("Firmware Basic Boot 2.500 秒", f.Value);
        Assert.Equal(2.5, f.NumericValue!.Value, 3);
    }

    [Fact]
    public void 事實_量測點不同是常態不是矛盾()
    {
        var acpi = new FakeAcpi(BuildFpdt(10_000_000, 35_000_000, 38_000_000, 40_000_000));
        var f = AcpiFpdtFactsService.Collect(acpi, At);
        Assert.Contains("是不同的量測點", f.Value);
        Assert.Contains("常態不是矛盾", f.Value);
    }

    [Fact]
    public void 事實_不定義幾秒才算慢()
    {
        var acpi = new FakeAcpi(BuildFpdt(10_000_000, 35_000_000, 38_000_000, 40_000_000));
        var f = AcpiFpdtFactsService.Collect(acpi, At);
        Assert.Contains("不定義「幾秒才算慢」", f.Value);
        Assert.Contains("拿這台機器自己的歷史比", f.Value);
    }

    [Fact]
    public void 事實_沒有FPDT表是不適用不是零秒()
    {
        var acpi = new FakeAcpi(Header("DSDT", 36));
        var f = AcpiFpdtFactsService.Collect(acpi, At);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不是 0 秒", f.UnavailableReason);
        Assert.Contains("天差地別", f.UnavailableReason);
        Assert.Equal("", f.Value);
    }

    [Fact]
    public void 事實_表在但沒有BasicBoot紀錄也是不適用()
    {
        var acpi = new FakeAcpi(Header("FPDT", 36));
        var f = AcpiFpdtFactsService.Collect(acpi, At);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("缺席就不出值", f.UnavailableReason);
    }

    [Fact]
    public void 事實_來源不可用是權限態並帶原因()
    {
        var acpi = new FakeAcpi(null, available: false, reason: "需要管理員權限");
        var f = AcpiFpdtFactsService.Collect(acpi, At);
        Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability);
        Assert.Contains("需要管理員權限", f.UnavailableReason);
    }

    [Fact]
    public void 事實_單位是秒且信任度是自報()
    {
        var acpi = new FakeAcpi(BuildFpdt(10_000_000, 35_000_000, 38_000_000, 40_000_000));
        var f = AcpiFpdtFactsService.Collect(acpi, At);
        Assert.Equal("秒", f.Unit);
        // 韌體自報 → Reported 不是 Measured。
        Assert.Equal(FactTrustLevel.Reported, f.Trust);
    }
}
