using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>PCIe 鏈路暫存器解讀與判讀（純函式）。</summary>
public class PcieLinkTests
{
    // ── 位元欄位 ────────────────────────────────────────────────────────────

    [Fact]
    public void 能力暫存器_速度在低4位寬度在4到9位()
    {
        // Gen4（代碼 4）x16：width 16 << 4 = 0x100，再或上 speed 4
        var (speed, width) = PcieLinkDecoder.DecodeLinkCap(0x100 | 4);
        Assert.Equal(4, speed);
        Assert.Equal(16, width);
    }

    [Fact]
    public void 能力暫存器_忽略高位的其他欄位()
    {
        // 高位還有 ASPM、L0s／L1 延遲、埠號等欄位，不能滲進速度與寬度
        var (speed, width) = PcieLinkDecoder.DecodeLinkCap(0xFFFF_FC00 | 0x040 | 3);
        Assert.Equal(3, speed);
        Assert.Equal(4, width);
    }

    [Fact]
    public void 狀態暫存器_取出目前速度與協商寬度()
    {
        var (speed, width) = PcieLinkDecoder.DecodeLinkStatus((ushort)(0x040 | 1));
        Assert.Equal(1, speed);   // 閒置降到 Gen1
        Assert.Equal(4, width);
    }

    [Fact]
    public void 狀態暫存器_高位的訓練與時鐘位元不影響解讀()
    {
        var (speed, width) = PcieLinkDecoder.DecodeLinkStatus(0xFC00 | 0x100 | 4);
        Assert.Equal(4, speed);
        Assert.Equal(16, width);
    }

    // ── 名稱與文字 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, "Gen1")]
    [InlineData(2, "Gen2")]
    [InlineData(3, "Gen3")]
    [InlineData(4, "Gen4")]
    [InlineData(5, "Gen5")]
    [InlineData(6, "Gen6")]
    public void 速度代碼對應世代名稱(int code, string expected) => Assert.Equal(expected, PcieLinkDecoder.SpeedName(code));

    [Fact]
    public void 未知的速度代碼不硬掰成某個世代()
    {
        Assert.Equal("—", PcieLinkDecoder.SpeedName(0));
        Assert.Contains("代碼", PcieLinkDecoder.SpeedName(9));
        Assert.Equal(0, PcieLinkDecoder.GtPerSecond(9));
    }

    [Fact]
    public void 鏈路文字_資料不足時顯示破折號而不是Gen0x0()
    {
        Assert.Equal("Gen4 x16", PcieLinkDecoder.LinkText(4, 16));
        Assert.Equal("—", PcieLinkDecoder.LinkText(0, 16));
        Assert.Equal("—", PcieLinkDecoder.LinkText(4, 0));
    }

    // ── 頻寬 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 頻寬_Gen1與Gen2用8b10b編碼()
    {
        // Gen1 x1 = 2.5 GT/s × 0.8 ÷ 8 = 0.25 GB/s
        Assert.Equal(0.25, PcieLinkDecoder.BandwidthGbps(1, 1), 6);
        Assert.Equal(0.5, PcieLinkDecoder.BandwidthGbps(2, 1), 6);
    }

    [Fact]
    public void 頻寬_Gen3起用128b130b編碼()
    {
        // Gen3 x16 = 8 × 16 × 128/130 ÷ 8 ≈ 15.75 GB/s
        Assert.Equal(15.754, PcieLinkDecoder.BandwidthGbps(3, 16), 3);
        // Gen4 x16 ≈ 31.5 GB/s
        Assert.Equal(31.508, PcieLinkDecoder.BandwidthGbps(4, 16), 3);
    }

    [Fact]
    public void 頻寬_資料不足回0而不是丟例外()
    {
        Assert.Equal(0, PcieLinkDecoder.BandwidthGbps(0, 16));
        Assert.Equal(0, PcieLinkDecoder.BandwidthGbps(4, 0));
        Assert.Equal(0, PcieLinkDecoder.BandwidthGbps(9, 4));
    }

    [Fact]
    public void 頻寬佔比_相符時滿格寬度砍半時約一半()
    {
        Assert.Equal(1, PcieLinkDecoder.BandwidthFraction(4, 16, 4, 16), 6);
        Assert.Equal(0.25, PcieLinkDecoder.BandwidthFraction(4, 4, 4, 16), 6);
        // Gen1 x16 對 Gen4 x16：2.5/16 再乘上編碼效率差
        Assert.True(PcieLinkDecoder.BandwidthFraction(1, 16, 4, 16) < 0.14);
    }

    [Fact]
    public void 頻寬佔比_能力為0時回0且不超過1()
    {
        Assert.Equal(0, PcieLinkDecoder.BandwidthFraction(4, 16, 0, 0));
        // 韌體亂報時（目前比能力還高）夾在 1，不畫出超出範圍的長條
        Assert.Equal(1, PcieLinkDecoder.BandwidthFraction(5, 16, 4, 16), 6);
    }

    // ── 通訊埠類別 ──────────────────────────────────────────────────────────

    [Fact]
    public void 通訊埠類別_認得的照譯不認得的照實說是代碼()
    {
        Assert.Equal("端點", PcieLinkDecoder.PortTypeName(0x0));
        Assert.Equal("根埠", PcieLinkDecoder.PortTypeName(0x4));
        Assert.Equal("交換器下游埠", PcieLinkDecoder.PortTypeName(0x6));
        Assert.Contains("類別", PcieLinkDecoder.PortTypeName(0x3));
    }

    // ── 判讀 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 判讀_寬度不足是真的損失且說明成因()
    {
        var (text, severity) = PcieLinkDecoder.Judge(4, 4, 4, 16);
        Assert.Equal(2, severity);
        Assert.StartsWith("⚠", text);
        Assert.Contains("x4", text);
        Assert.Contains("x16", text);
        Assert.Contains("分流", text);
    }

    [Fact]
    public void 判讀_速度較低要明說閒置降速不是故障()
    {
        var (text, severity) = PcieLinkDecoder.Judge(1, 16, 4, 16);
        Assert.Equal(1, severity);
        Assert.DoesNotContain("⚠", text);
        Assert.Contains("不是故障", text);
        Assert.Contains("負載", text);   // 要求使用者在負載中重量一次
    }

    [Fact]
    public void 判讀_寬度不足優先於速度較低()
    {
        // 兩者都低時，先講寬度（那才是不會自己恢復的那個）
        var (text, severity) = PcieLinkDecoder.Judge(1, 4, 4, 16);
        Assert.Equal(2, severity);
        Assert.Contains("寬度", text);
    }

    [Fact]
    public void 判讀_相符時說已達能力並附上鏈路()
    {
        var (text, severity) = PcieLinkDecoder.Judge(4, 16, 4, 16);
        Assert.Equal(0, severity);
        Assert.Contains("已達裝置能力", text);
        Assert.Contains("Gen4 x16", text);
    }

    [Fact]
    public void 判讀_資料不足時不下任何結論()
    {
        var (noCap, s1) = PcieLinkDecoder.Judge(4, 16, 0, 0);
        Assert.Equal(0, s1);
        Assert.DoesNotContain("⚠", noCap);
        Assert.Contains("沒回報", noCap);

        var (noStatus, s2) = PcieLinkDecoder.Judge(0, 0, 4, 16);
        Assert.Equal(0, s2);
        Assert.Contains("讀不到", noStatus);
    }

    // ── 整體結論 ────────────────────────────────────────────────────────────

    private static PcieLinkRow Row(int curSpeed, int curWidth, int maxSpeed, int maxWidth)
    {
        var (verdict, severity) = PcieLinkDecoder.Judge(curSpeed, curWidth, maxSpeed, maxWidth);
        return new PcieLinkRow("測試裝置", "01:00.0", "端點", curSpeed, curWidth, maxSpeed, maxWidth, verdict, severity);
    }

    [Fact]
    public void 結論_沒讀到裝置時說沒讀到而不是說一切正常()
    {
        var text = PcieLinkDecoder.Summarize([]);
        Assert.Contains("沒有讀到", text);
        Assert.Contains("不代表", text);   // 明說「不代表機器沒有 PCIe 裝置」
    }

    [Fact]
    public void 結論_有寬度不足時優先點出來()
    {
        var text = PcieLinkDecoder.Summarize([Row(4, 4, 4, 16), Row(1, 16, 4, 16), Row(4, 16, 4, 16)]);
        Assert.Contains("1 條的寬度低於裝置能力", text);
        Assert.Contains("共 3 條", text);
    }

    [Fact]
    public void 結論_只有速度較低時明說是閒置降速()
    {
        var text = PcieLinkDecoder.Summarize([Row(1, 16, 4, 16), Row(4, 16, 4, 16)]);
        Assert.Contains("寬度全部與能力相符", text);
        Assert.Contains("閒置降速", text);
    }

    [Fact]
    public void 結論_全部相符時才說全部相符()
    {
        var text = PcieLinkDecoder.Summarize([Row(4, 16, 4, 16), Row(3, 4, 3, 4)]);
        Assert.Contains("全部與裝置能力相符", text);
    }

    // ── 裝置名稱 ────────────────────────────────────────────────────────────

    [Fact]
    public void 裝置ID_取出廠商與裝置代碼()
    {
        var key = PcieLinkService.ParseVenDev(@"PCI\VEN_10DE&DEV_2504&SUBSYS_40A11458&REV_A1\4&2d3f0c1d&0&0008");
        Assert.NotNull(key);
        Assert.Equal(0x10DE, (int)key!.Value.Ven);
        Assert.Equal(0x2504, (int)key.Value.Dev);
    }

    [Fact]
    public void 裝置ID_不分大小寫()
    {
        var key = PcieLinkService.ParseVenDev(@"pci\ven_8086&dev_7a60&subsys_00000000");
        Assert.NotNull(key);
        Assert.Equal(0x8086, (int)key!.Value.Ven);
        Assert.Equal(0x7A60, (int)key.Value.Dev);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"USB\VID_046D&PID_C52B")]
    [InlineData(@"PCI\VEN_10DE")]
    [InlineData(@"PCI\VEN_ZZZZ&DEV_2504")]
    [InlineData(@"PCI\VEN_10DE&DEV_25")]
    public void 裝置ID_格式不對時回null而不是丟例外(string id)
        => Assert.Null(PcieLinkService.ParseVenDev(id));
}
