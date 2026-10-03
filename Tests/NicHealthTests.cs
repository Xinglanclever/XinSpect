using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 網路補缺的契約：網卡錯誤／丟棄計數（WMI MSFT_NetAdapterStatistics，usermode）——
/// 錯誤不為零的介面逐條攤開（驅動劣化／線材／交換器的指紋）；MAC OUI 查表
/// （比照 SuperIoKnowledge 知識庫模式，只收錄大廠）。通路注入探測。
/// </summary>
public class NicHealthTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

    private static XinSpect.NicStatsEntry N(string name, ulong rxErr, ulong txErr, ulong rxDrop, ulong txDrop) =>
        new(name, rxErr, txErr, rxDrop, txDrop);

    [Fact]
    public void 網卡健康_有錯誤的逐條攤開_乾淨的歸零計數()
    {
        var facts = XinSpect.NicHealthFactsService.Collect(At, probe: () =>
        [
            N("乙太網路", 12, 0, 340, 0),
            N("Wi-Fi", 0, 0, 0, 0),
        ]);

        var count = Assert.Single(facts, f => f.Key == "nic.count");
        Assert.Equal(2u, count.NumericValue);
        var dirty = Assert.Single(facts, f => f.Key == "nic.dirty_count");
        Assert.Equal(1u, dirty.NumericValue);
        var detail = Assert.Single(facts, f => f.Key == "nic.dirty.0");
        Assert.Contains("乙太網路", detail.Value);
        Assert.Contains("RX 錯誤 12", detail.Value);
        Assert.Contains("RX 丟棄 340", detail.Value);
    }

    [Fact]
    public void 網卡健康_全乾淨與讀不到分清()
    {
        var clean = XinSpect.NicHealthFactsService.Collect(At, probe: () =>
        [
            N("乙太網路", 0, 0, 0, 0),
        ]);
        Assert.Contains("0 個", Assert.Single(clean, f => f.Key == "nic.dirty_count").Value);

        var fail = XinSpect.NicHealthFactsService.Collect(At, probe: () => null);
        Assert.Equal(FactAvailability.ReadError,
            Assert.Single(fail, f => f.Key == "nic.count").Availability);
    }

    [Theory]
    [InlineData("A0-AF-1D-11-22-33", "Intel")]
    [InlineData("00-E0-4C-11-22-33", "Realtek")]
    [InlineData("04-D9-F5-AA-BB-CC", "ASUS")]
    [InlineData("94-DE-80-AA-BB-CC", "GIGABYTE")]
    [InlineData("00-15-5D-AA-BB-CC", "Microsoft（Hyper-V 虛擬）")]
    public void MAC_OUI_收錄大廠逐條釘值(string mac, string expected)
        => Assert.Contains(expected, XinSpect.OuiKnowledge.VendorOf(mac));

    [Fact]
    public void MAC對照_逐介面帶廠商_讀不到三態()
    {
        var facts = XinSpect.NicHealthFactsService.CollectMacVendors(At, probe: () =>
        [
            ("乙太網路", "A0-AF-1D-11-22-33"),
        ]);
        var first = Assert.Single(facts, f => f.Key == "nic.mac.0");
        Assert.Contains("Intel", first.Value);
        Assert.Contains("A0-AF-1D", first.Value);

        var fail = XinSpect.NicHealthFactsService.CollectMacVendors(At, probe: () => null);
        Assert.Equal(FactAvailability.ReadError,
            Assert.Single(fail, f => f.Key == "nic.mac.0").Availability);
    }

    [Fact]
    public void MAC_OUI_未收錄與壞格式如實標()
    {
        Assert.Contains("未收錄", XinSpect.OuiKnowledge.VendorOf("AA-BB-CC-DD-EE-FF"));
        Assert.Contains("無法解析", XinSpect.OuiKnowledge.VendorOf("not-a-mac"));
        Assert.Contains("無法解析", XinSpect.OuiKnowledge.VendorOf(""));
    }
}
