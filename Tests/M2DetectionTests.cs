using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// M.2／U.2 判定：<b>BIOS 標的</b>與<b>依通道數推測的</b>必須分開，
/// 而兩者都拿不到時要如實說「未回報」——不是留白，也不是硬說是 M.2。
/// </summary>
/// <remarks>
/// 測試資料取自本機實測（2026-10-27）：SMBIOS 只列出四個「PCI Express」插槽
/// （PCIEX16_1 x16 使用中、PCIEX16_2 x16、PCIEX4 x4、PCIEX16_3 x8），
/// 而磁碟有三顆 NVMe（其中一顆型號含 SFF）。
/// </remarks>
public class M2DetectionTests
{
    private static SmbiosSlotRow Slot(string name, string type, string width, string usage = "可用")
        => new(name, type, width, usage);

    [Theory]
    [InlineData("M.2 Socket 3")]
    [InlineData("SFF-8639 (U.2)")]
    [InlineData("U.2")]
    [InlineData("m.2 socket 2")]
    public void BIOS標成型別才算量到的事實(string type)
        => Assert.True(M2Detection.IsTypedM2OrU2(type));

    [Theory]
    [InlineData("PCI Express")]
    [InlineData("PCI")]
    [InlineData("")]
    [InlineData(null)]
    public void 一般插槽不算(string? type)
        => Assert.False(M2Detection.IsTypedM2OrU2(type));

    [Fact]
    public void 只有x4的PCIe才是可能為M2的推測()
    {
        var x4 = Slot("PCIEX4", "PCI Express", "x4");
        var x16 = Slot("PCIEX16_1", "PCI Express", "x16");
        var x8 = Slot("PCIEX16_3", "PCI Express", "x8");

        Assert.True(M2Detection.IsInferredM2(x4, isTyped: false));
        Assert.False(M2Detection.IsInferredM2(x16, isTyped: false));   // 顯示卡插槽不是 M.2
        Assert.False(M2Detection.IsInferredM2(x8, isTyped: false));
        Assert.False(M2Detection.IsInferredM2(x4, isTyped: true));     // 已標型別的不重複列
    }

    [Fact]
    public void 本機的插槽形狀要推成一個可能M2並說出是推測()
    {
        SmbiosSlotRow[] slots =
        [
            Slot("PCIEX16_1", "PCI Express", "x16", "使用中"),
            Slot("PCIEX16_2", "PCI Express", "x16"),
            Slot("PCIEX4", "PCI Express", "x4"),
            Slot("PCIEX16_3", "PCI Express", "x8"),
        ];

        var typed = slots.Where(s => M2Detection.IsTypedM2OrU2(s.Type)).ToList();
        var inferred = slots.Where(s => M2Detection.IsInferredM2(s, M2Detection.IsTypedM2OrU2(s.Type))).ToList();

        Assert.Empty(typed);
        Assert.Single(inferred);
        Assert.Equal("PCIEX4", inferred[0].Designation);

        string text = M2Detection.Summarize(typed, inferred, nvmeDrives: 3);
        Assert.Contains("沒有把任何插槽標成", text);
        Assert.Contains("PCIEX4", text);
        Assert.Contains("推測", text);          // 一定要標明是推論
        Assert.Contains("3 顆 NVMe", text);     // 磁碟數也要一起說
    }

    [Fact]
    public void BIOS有標型別時以量到的事實為主()
    {
        var typed = new[] { Slot("M2_1", "M.2 Socket 3", "x4") };
        string text = M2Detection.Summarize(typed, [], 1);
        Assert.Contains("BIOS 標示 1 個", text);
        Assert.DoesNotContain("推測", text);
    }

    [Fact]
    public void 什麼都沒有時要如實說未回報而不是留白()
    {
        string text = M2Detection.Summarize([], [], 0);
        Assert.Contains("未回報", text);
        Assert.Contains("不代表沒有", text);
    }

    // ── 磁碟外型 ──────────────────────────────────────────────────────

    [Fact]
    public void 型號帶SFF的NVMe要標成U2並說出依據()
    {
        string text = M2Detection.DescribeDriveForm(DiskKind.NvmeSsd, "INTEL SSDPE2ME012T4D SFF NVME 1200GB");
        Assert.Contains("U.2", text);
        Assert.Contains("型號字串", text);      // 依據要寫出來，不能說成 Windows 的判定
    }

    [Fact]
    public void 一般NVMe要說Windows不區分M2與U2()
    {
        string text = M2Detection.DescribeDriveForm(DiskKind.NvmeSsd, "INTEL SSDPELKX010T8");
        Assert.Contains("M.2", text);
        Assert.Contains("U.2", text);
        Assert.Contains("未回報", text);
    }

    [Fact]
    public void SATA碟也要說明外型未回報()
    {
        Assert.Contains("外型未回報", M2Detection.DescribeDriveForm(DiskKind.SataSsd, "INTEL SSDSC2BF180A5L"));
        Assert.Contains("外型未回報", M2Detection.DescribeDriveForm(DiskKind.Hdd, "ST1000LM024"));
    }
}
