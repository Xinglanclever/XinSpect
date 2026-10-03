using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP10 周邊匯流排的契約：USB 拓撲摘要（WMI Win32_USBControllerDevice 的控制器→裝置相依對，
/// 純函式建樹）＋螢幕連接介面（root\wmi WmiMonitorConnectionParams 的 VideoOutputTechnology
/// 碼，解碼器只收錄有把握子集）。通路層以注入探測替代。
/// </summary>
public class PeripheralBusTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    private static XinSpect.UsbEdge E(string controller, string device) => new(controller, device);

    [Fact]
    public void USB拓撲_控制器與裝置與每控制器最多掛載釘值()
    {
        var edges = new List<XinSpect.UsbEdge>
        {
            E("ctrl0", "dev1"), E("ctrl0", "dev2"), E("ctrl0", "dev3"),
            E("ctrl1", "dev4"),
        };
        var facts = XinSpect.UsbTopologyService.Collect(At, probe: () => edges);

        Assert.All(facts, f => Assert.Equal("週邊匯流排", f.Category));
        Assert.Equal(2u, Assert.Single(facts, f => f.Key == "usb.controllers").NumericValue);
        Assert.Equal(4u, Assert.Single(facts, f => f.Key == "usb.devices").NumericValue);
        var busiest = Assert.Single(facts, f => f.Key == "usb.busiest_controller");
        Assert.Equal(3u, busiest.NumericValue);
        Assert.Contains("ctrl0", busiest.Value);
    }

    [Fact]
    public void USB拓撲_空與讀不到分得清楚()
    {
        var empty = XinSpect.UsbTopologyService.Collect(At, probe: () => []);
        Assert.All(empty, f => Assert.Equal(FactAvailability.Present, f.Availability));

        var fail = XinSpect.UsbTopologyService.Collect(At, probe: () => null);
        Assert.All(fail, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }

    [Theory]
    [InlineData(unchecked((int)0x80000006), "HDMI")]
    [InlineData(unchecked((int)0x8000000A), "DisplayPort（外接）")]
    [InlineData(unchecked((int)0x8000000B), "DisplayPort（內嵌）")]
    [InlineData(unchecked((int)0x80000005), "DVI")]
    [InlineData(unchecked((int)0x80000001), "VGA（HD-15）")]
    [InlineData(unchecked((int)0x8000000D), "Miracast（無線）")]
    public void 螢幕連接介面_收錄子集解碼(int vot, string expected)
        => Assert.Equal(expected, XinSpect.MonitorConnectionDecoder.DescribeVideoOutput(vot));

    [Fact]
    public void 螢幕連接介面_未收錄如實標()
    {
        Assert.Contains("未收錄", XinSpect.MonitorConnectionDecoder.DescribeVideoOutput(0x1234));
        Assert.Contains("未收錄", XinSpect.MonitorConnectionDecoder.DescribeVideoOutput(-1));
    }

    [Fact]
    public void 螢幕連接_逐台解碼與DP統計_讀不到三態()
    {
        var monitors = new List<XinSpect.MonitorConnection>
        {
            new("DISPLAY1", unchecked((int)0x8000000A)),
            new("DISPLAY2", unchecked((int)0x80000006)),
        };
        var facts = XinSpect.MonitorConnectionService.Collect(At, probe: () => monitors);

        var dp = Assert.Single(facts, f => f.Key == "mon.dp");
        Assert.Equal(1u, dp.NumericValue);
        var mon = Assert.Single(facts, f => f.Key == "mon.0");
        Assert.Contains("DisplayPort（外接）", mon.Value);

        var fail = XinSpect.MonitorConnectionService.Collect(At, probe: () => null);
        Assert.All(fail, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }
}
