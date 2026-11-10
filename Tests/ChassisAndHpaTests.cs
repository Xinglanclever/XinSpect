using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// SMBIOS Type 3 安全狀態與 ATA/OS 容量對照，不把靜態欄位當成入侵或 HPA 證據。
/// </summary>
public class ChassisAndHpaTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

    /// <summary>組一個 SMBIOS Type 3 結構（Security Status 在 offset 12）。</summary>
    private static byte[] Type3(byte securityStatus)
    {
        var s = new byte[16];
        s[0] = 3;      // Type = System Enclosure
        s[1] = 16;     // Length
        s[2] = 0x01; s[3] = 0x00;   // Handle
        s[4] = 1;      // Manufacturer string idx
        s[5] = 0x03;   // Chassis Type = Desktop
        s[6] = 0; s[7] = 0; s[8] = 0;  // Version/Serial/Asset 無字串
        s[9] = 3; s[10] = 3; s[11] = 3;  // Boot/Power/Thermal = Safe
        s[12] = securityStatus;
        return s;
    }

    [Fact]
    public void Type3_安全狀態遵循規格而不是入侵紀錄()
    {
        Assert.Equal("無（None）", XinSpect.ChassisFactsService.DescribeSecurityStatus(3));
        Assert.Equal("外部介面已啟用", XinSpect.ChassisFactsService.DescribeSecurityStatus(5));
        Assert.Equal("外部介面已鎖定", XinSpect.ChassisFactsService.DescribeSecurityStatus(4));
        Assert.Contains("未收錄", XinSpect.ChassisFactsService.DescribeSecurityStatus(7));
        Assert.Equal("未知", XinSpect.ChassisFactsService.DescribeSecurityStatus(2));
        Assert.Contains("未收錄", XinSpect.ChassisFactsService.DescribeSecurityStatus(0xEE));
    }

    [Fact]
    public void 機箱事實不宣稱拆機證據_讀不到三態()
    {
        var intrusion = XinSpect.ChassisFactsService.Collect(At,
            smbiosProbe: () => new[] { Type3(5) });
        var status = Assert.Single(intrusion, f => f.Key == "chassis.security_status");
        Assert.Equal(FactAvailability.Present, status.Availability);
        Assert.Contains("外部介面已啟用", status.Value);
        Assert.DoesNotContain("拆機的韌體級證據", status.Value);
        Assert.DoesNotContain("機殼曾被開啟", status.Value);

        var clean = XinSpect.ChassisFactsService.Collect(At,
            smbiosProbe: () => new[] { Type3(3) });
        Assert.Contains("無（None）", Assert.Single(clean, f => f.Key == "chassis.security_status").Value);

        var missing = XinSpect.ChassisFactsService.Collect(At, smbiosProbe: () => []);
        Assert.Equal(FactAvailability.NotSupported,
            Assert.Single(missing, f => f.Key == "chassis.security_status").Availability);
        Assert.Contains("沒有 Type 3", Assert.Single(missing, f => f.Key == "chassis.security_status").UnavailableReason);
    }

    [Fact]
    public void 容量差異不能直接判定HPA_一致也不能排除HPA()
    {
        // 兩個來源不一致只證明差異，未讀取 native maximum 就不能確認 HPA。
        var hpa = XinSpect.HpaFactsService.Evaluate(
            firmwareTotalLba: 488_397_168ul, osVisibleBytes: 240_000_000_000L);
        Assert.True(hpa.HiddenSectors > 0);
        Assert.Contains("不能確認 HPA", hpa.Summary);
        Assert.DoesNotContain("HPA 作用中", hpa.Summary);

        var same = XinSpect.HpaFactsService.Evaluate(
            firmwareTotalLba: 488_397_168ul, osVisibleBytes: 488_397_168L * 512);
        Assert.False(same.HiddenSectors > 0);
        Assert.Contains("一致", same.Summary);
        Assert.Contains("不能排除 HPA", same.Summary);
    }

    [Fact]
    public void HPA事實_有隱藏容量逐項_讀不到三態_DCO聲明不做()
    {
        var facts = XinSpect.HpaFactsService.Collect(At,
            probe: () => [new XinSpect.HpaFactsService.HpaDrive("PhysicalDrive0", 488_397_168ul, 240_000_000_000L)]);
        var hit = Assert.Single(facts, f => f.Key == "hpa.0");
        Assert.Contains("容量差異", hit.Value);

        var clean = XinSpect.HpaFactsService.Collect(At,
            probe: () => [new XinSpect.HpaFactsService.HpaDrive("PhysicalDrive0", 1000ul, 1000L * 512)]);
        Assert.Contains("一致", Assert.Single(clean, f => f.Key == "hpa.0").Value);

        var none = XinSpect.HpaFactsService.Collect(At, probe: () => null);
        Assert.Equal(FactAvailability.ReadError,
            Assert.Single(none, f => f.Key == "hpa.dco").Availability);
        Assert.Contains("DCO", Assert.Single(none, f => f.Key == "hpa.dco").Name);
    }

    [Fact]
    public void CapacityComparisonUsesLogicalSectorSizeAndRejectsMissingOrReverseEvidence()
    {
        var same = HpaFactsService.Evaluate(1_000_000, 4_096_000_000L, 4096);
        Assert.Equal(1_000_000ul, same.OsVisibleSectors);
        Assert.Equal(0, same.HiddenSectors);
        Assert.Contains("一致", same.Summary);
        Assert.Contains("不能排除 HPA", same.Summary);
        Assert.Contains("讀不到", HpaFactsService.Evaluate(0, 512).Summary);
        Assert.Contains("讀不到", HpaFactsService.Evaluate(1000, 0).Summary);
        Assert.DoesNotContain("一致", HpaFactsService.Evaluate(1000, 2000 * 512L).Summary);
        Assert.DoesNotContain("一致", HpaFactsService.Evaluate(1000, 512_001L).Summary);
        var missing = HpaFactsService.Collect(At, () => [new("disk", 0, 512)]).Single(f => f.Key == "hpa.0");
        Assert.NotEqual(FactAvailability.Present, missing.Availability);
        Assert.Null(missing.NumericValue);
    }
}
