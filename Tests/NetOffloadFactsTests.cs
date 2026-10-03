using System.Management;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 網路卸載狀態事實（WP13）的契約：Checksum 與 RSS 的三態呈現（開／停／屬性未提供）、
/// WMI 不可用整組三態。以假 WMI 列驗證，不碰真 WMI。
/// </summary>
public class NetOffloadFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    private static WmiRow Row(params (string Key, object Value)[] props)
    {
        var r = new WmiRow();
        foreach (var (k, v) in props) r[k] = v;
        return r;
    }

    [Fact]
    public void 一般配接卡_TX_RX與RSS成列()
    {
        var facts = NetOffloadFactsService.Collect(At, wmiQuery: wql =>
        {
            if (wql.Contains("ChecksumOffload"))
                return [Row(("InstanceName", "Intel(R) I219-V"), ("TransmitChecksumOffloadEnabled", true),
                    ("TransmitChecksumOffloadSupported", true), ("ReceiveChecksumOffloadEnabled", true),
                    ("ReceiveChecksumOffloadSupported", true))];
            return [Row(("InstanceName", "Intel(R) I219-V"), ("Enabled", false))];
        });

        var ck = Assert.Single(facts, f => f.Key == "net.offload.checksum.0");
        Assert.Contains("Intel(R) I219-V", ck.Name);
        Assert.Contains("TX：開", ck.Value);
        Assert.Contains("RX：開", ck.Value);

        var rss = Assert.Single(facts, f => f.Key == "net.offload.rss.0");
        Assert.Equal("停用", rss.Value);
    }

    [Fact]
    public void 屬性缺席與支援未啟用_三態呈現不冒充()
    {
        var facts = NetOffloadFactsService.Collect(At, wmiQuery: wql =>
        {
            if (wql.Contains("ChecksumOffload"))
                return [Row(("InstanceName", "NIC-A"), ("ReceiveChecksumOffloadSupported", true))]; // Enabled 全缺
            return [Row(("InstanceName", "NIC-A"))]; // RSS Enabled 缺
        });

        var ck = Assert.Single(facts, f => f.Key == "net.offload.checksum.0");
        Assert.Contains("TX：屬性未提供", ck.Value);
        Assert.Contains("RX：未啟用（支援）", ck.Value);
        var rss = Assert.Single(facts, f => f.Key == "net.offload.rss.0");
        Assert.Contains("屬性未提供", rss.Value);
        Assert.Contains("不猜", rss.Value);
    }

    [Fact]
    public void WMI全空_單一三態事實()
    {
        var facts = NetOffloadFactsService.Collect(At, wmiQuery: _ => []);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("查不到", f.UnavailableReason);

        var thrown = NetOffloadFactsService.Collect(At, wmiQuery: _ => throw new ManagementException());
        var t = Assert.Single(thrown);
        Assert.Equal(FactAvailability.NotSupported, t.Availability);
    }
}
