using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// ACPI 根指標表這一層的金標（U10 第三圈 AC-0001／AC-0002）：
/// FADT 指標欄逐欄、DSDT 兩個欄位一致與否、以及「根指標表不在列舉裡」這件事怎麼寫。
/// </summary>
public class AcpiRootFactsTests
{
    // ── 合成表 ──────────────────────────────────────────────────────────

    private static byte[] Facp(byte revision = 6, uint firmwareCtrl = 0x4D19C000, uint dsdt = 0x4C031000,
        ulong? xFirmwareCtrl = 0x4D19C000, ulong? xDsdt = 0x4C031000, int length = 276)
    {
        var b = new byte[Math.Max(length, 148)];
        Encoding.ASCII.GetBytes("FACP").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4, 4), (uint)length);
        b[8] = revision;
        Encoding.ASCII.GetBytes("ALASKA").CopyTo(b, 10);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36, 4), firmwareCtrl);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40, 4), dsdt);
        if (length > 131) b[131] = 0;
        if (xFirmwareCtrl is { } xf) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(132, 8), xf);
        if (xDsdt is { } xd) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(140, 8), xd);
        return b[..length];
    }

    private static byte[] Table(string signature, int length = 40)
    {
        var b = new byte[length];
        Encoding.ASCII.GetBytes(signature).CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4, 4), (uint)length);
        return b;
    }

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    // ── 解碼器 ──────────────────────────────────────────────────────────

    [Fact]
    public void FADT_逐欄比對()
    {
        var f = AcpiFadt.Parse(Facp());
        Assert.NotNull(f);
        Assert.Equal("FACP", f.Signature);
        Assert.Equal(6, f.Revision);
        Assert.Equal(0x4D19C000u, f.FirmwareCtrl);
        Assert.Equal(0x4C031000u, f.Dsdt);
        Assert.Equal(0x4D19C000UL, f.XFirmwareCtrl);
        Assert.Equal(0x4C031000UL, f.XDsdt);
        Assert.True(f.HasExtendedFields);
    }

    [Fact]
    public void FADT_短表沒有64位欄()
    {
        var f = AcpiFadt.Parse(Facp(length: 120))!;
        Assert.Null(f.XDsdt);
        Assert.Null(f.XFirmwareCtrl);
        Assert.Null(f.DsdtAgrees);
        Assert.False(f.HasExtendedFields);
    }

    [Fact]
    public void FADT_簽章不符回null()
    {
        var b = Facp();
        Encoding.ASCII.GetBytes("APIC").CopyTo(b, 0);
        Assert.Null(AcpiFadt.Parse(b));
    }

    [Fact]
    public void 四GiB界線_剛好等於就已在界外()
    {
        Assert.True(AcpiFadt.IsBelow4GiB(0xFFFF_FFFF));
        Assert.False(AcpiFadt.IsBelow4GiB(0x1_0000_0000));
    }

    // ── 服務 ────────────────────────────────────────────────────────────

    [Fact]
    public void 根指標表不在列舉裡_要說成OS沒給()
    {
        var facts = AcpiRootFactsService.Collect([Facp(), Table("APIC"), Table("MCFG")], DateTimeOffset.UnixEpoch);
        var summary = Fact(facts, AcpiRootFactsService.SummaryKey);
        Assert.Equal(FactAvailability.Present, summary.Availability);
        Assert.Equal(3d, summary.NumericValue!.Value);
        Assert.Contains("RSDT 與 XSDT 都不在列舉裡", summary.Value);
        Assert.Contains("OS 沒給", summary.Value);
        Assert.Contains("FADT 修訂 6", summary.Value);
        Assert.Contains("至少這 3 張", summary.Value);
    }

    [Fact]
    public void 只有XSDT與只有RSDT要分得出來()
    {
        var onlyXsdt = Fact(AcpiRootFactsService.Collect([Facp(), Table("XSDT")], DateTimeOffset.UnixEpoch),
            AcpiRootFactsService.SummaryKey);
        Assert.Contains("XSDT 在列舉裡、RSDT 不在", onlyXsdt.Value);

        var onlyRsdt = Fact(AcpiRootFactsService.Collect([Facp(), Table("RSDT")], DateTimeOffset.UnixEpoch),
            AcpiRootFactsService.SummaryKey);
        Assert.Contains("RSDT 在列舉裡、XSDT 不在", onlyRsdt.Value);
    }

    [Fact]
    public void DSDT兩欄相同_在4GiB以下()
    {
        var f = Fact(AcpiRootFactsService.Collect([Facp()], DateTimeOffset.UnixEpoch),
            AcpiRootFactsService.DsdtKey);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("FACS 0x4D19C000", f.Value);
        Assert.Contains("DSDT 0x4C031000", f.Value);
        Assert.Contains("32 位與 64 位欄**相同**", f.Value);
        Assert.Contains("在 4 GiB 以下", f.Value);
        Assert.Contains("說得出位址不等於看得到內容", f.Value);
    }

    [Fact]
    public void DSDT兩欄不同_只有64位指得到()
    {
        var f = Fact(AcpiRootFactsService.Collect(
            [Facp(dsdt: 0x0FFF_FFFF, xDsdt: 0x1_0000_0000)], DateTimeOffset.UnixEpoch),
            AcpiRootFactsService.DsdtKey);
        Assert.Contains("**32 位與 64 位欄不同**", f.Value);
        Assert.Contains("**位址在 4 GiB 以上**", f.Value);
    }

    [Fact]
    public void 沒有FADT_標不適用而不是讀取錯誤()
    {
        var facts = AcpiRootFactsService.Collect([Table("APIC")], DateTimeOffset.UnixEpoch);
        var dsdt = Fact(facts, AcpiRootFactsService.DsdtKey);
        Assert.Equal(FactAvailability.NotApplicable, dsdt.Availability);
        Assert.Contains("不是「沒有 DSDT」", dsdt.UnavailableReason);
    }

    [Fact]
    public void 空表清單_兩把都標不適用但界線仍在()
    {
        var facts = AcpiRootFactsService.Collect([], DateTimeOffset.UnixEpoch);
        Assert.Equal(FactAvailability.NotApplicable, Fact(facts, AcpiRootFactsService.SummaryKey).Availability);
        Assert.Equal(FactAvailability.NotApplicable, Fact(facts, AcpiRootFactsService.DsdtKey).Availability);
        Assert.Equal(FactAvailability.Present, Fact(facts, AcpiRootFactsService.BoundaryKey).Availability);
    }

    [Fact]
    public void 界線宣告_明說雙總和驗不了與實體記憶體錯誤碼()
    {
        var b = Fact(AcpiRootFactsService.Collect([Facp()], DateTimeOffset.UnixEpoch),
            AcpiRootFactsService.BoundaryKey);
        Assert.Null(b.UnavailableReason);
        Assert.Contains("前 20 位元組、前 36 位元組各自加總為 0", b.Value);
        Assert.Contains("ERROR_FILE_NOT_FOUND", b.Value);
        Assert.Contains("ERROR_INVALID_HANDLE", b.Value);
        Assert.Contains("這一層是 OS 沒給", b.Value);
    }
}
