using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 驗機實體證據的契約：① 機箱開啟偵測（SMBIOS Type 3 System Enclosure 的 Security Status
/// 欄位——「Intrusion detected」＝機殼曾被開啟的韌體級證據）② HPA 隱藏容量（ATA IDENTIFY
/// 的最大 LBA 對照 OS 可見容量——不一致即 HPA 作用中；DCO 需廠商私有命令，誠實聲明不做）。
/// 純解碼＋注入探測。
/// </summary>
public class ChassisAndHpaTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

    /// <summary>組一個 SMBIOS Type 3 結構（Security Status 在 offset 12）。</summary>
    private static byte[] Type3(byte securityStatus)
    {
        var s = new byte[16];
        s[0] = 3;      // Type = System Enclosure
        s[1] = 17;     // Length
        s[2] = 0x01; s[3] = 0x00;   // Handle
        s[4] = 1;      // Manufacturer string idx
        s[5] = 0x03;   // Chassis Type = Tower
        s[6] = 0; s[7] = 0; s[8] = 0;  // Version/Serial/Asset 無字串
        s[9] = 3; s[10] = 3; s[11] = 3;  // Boot/Power/Thermal = Safe
        s[12] = securityStatus;
        return s;
    }

    [Fact]
    public void Type3_安全狀態逐值釘死_含入侵偵測()
    {
        Assert.Equal("無（沒有入侵偵測事件）", XinSpect.ChassisFactsService.DescribeSecurityStatus(3));
        Assert.Equal("機殼曾被開啟（韌體記錄入侵事件）", XinSpect.ChassisFactsService.DescribeSecurityStatus(5));
        Assert.Equal("未實作", XinSpect.ChassisFactsService.DescribeSecurityStatus(7));
        Assert.Equal("未知", XinSpect.ChassisFactsService.DescribeSecurityStatus(2));
        Assert.Contains("未收錄", XinSpect.ChassisFactsService.DescribeSecurityStatus(0xEE));
    }

    [Fact]
    public void 機箱事實_入侵偵測醒目_無入侵也成列_讀不到三態()
    {
        var intrusion = XinSpect.ChassisFactsService.Collect(At,
            smbiosProbe: () => new[] { Type3(5) });
        var status = Assert.Single(intrusion, f => f.Key == "chassis.security_status");
        Assert.Equal(FactAvailability.Present, status.Availability);
        Assert.Contains("機殼曾被開啟", status.Value);

        var clean = XinSpect.ChassisFactsService.Collect(At,
            smbiosProbe: () => new[] { Type3(3) });
        Assert.Contains("沒有入侵偵測事件", Assert.Single(clean, f => f.Key == "chassis.security_status").Value);

        var missing = XinSpect.ChassisFactsService.Collect(At, smbiosProbe: () => []);
        Assert.Equal(FactAvailability.NotSupported,
            Assert.Single(missing, f => f.Key == "chassis.security_status").Availability);
        Assert.Contains("沒有 Type 3", Assert.Single(missing, f => f.Key == "chassis.security_status").UnavailableReason);
    }

    [Fact]
    public void HPA_韌體LBA大於OS可見即標_一致即無()
    {
        // 韌體可見 488,397,168 磁區（240 GB）；OS 只看到一半 → HPA 作用中
        var hpa = XinSpect.HpaFactsService.Evaluate(
            firmwareTotalLba: 488_397_168ul, osVisibleBytes: 240_000_000_000L);
        Assert.True(hpa.HiddenSectors > 0);
        Assert.Contains("HPA", hpa.Summary);

        var same = XinSpect.HpaFactsService.Evaluate(
            firmwareTotalLba: 488_397_168ul, osVisibleBytes: 488_397_168L * 512);
        Assert.False(same.HiddenSectors > 0);
        Assert.Contains("一致", same.Summary);
    }

    [Fact]
    public void HPA事實_有隱藏容量逐項_讀不到三態_DCO聲明不做()
    {
        var facts = XinSpect.HpaFactsService.Collect(At,
            probe: () => [new XinSpect.HpaFactsService.HpaDrive("PhysicalDrive0", 488_397_168ul, 240_000_000_000L)]);
        var hit = Assert.Single(facts, f => f.Key == "hpa.0");
        Assert.Contains("隱藏", hit.Value);

        var clean = XinSpect.HpaFactsService.Collect(At,
            probe: () => [new XinSpect.HpaFactsService.HpaDrive("PhysicalDrive0", 1000ul, 1000L * 512)]);
        Assert.Contains("一致", Assert.Single(clean, f => f.Key == "hpa.0").Value);

        var none = XinSpect.HpaFactsService.Collect(At, probe: () => null);
        Assert.Equal(FactAvailability.ReadError,
            Assert.Single(none, f => f.Key == "hpa.dco").Availability);
        Assert.Contains("DCO", Assert.Single(none, f => f.Key == "hpa.dco").Name);
    }
}
