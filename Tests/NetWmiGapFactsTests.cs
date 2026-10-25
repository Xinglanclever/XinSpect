using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// U10-OS-0040（<c>net.wmi.gap</c>＋固定子鍵）：Win32_NetworkAdapterConfiguration 補充盤點。
/// 合成資料（probe 注入）；重點：只取既有服務沒拿的欄位（查重收口）、只輸出計數與存在性、
/// 讀不到五把鍵全部如實 ReadError 且不帶數值。
/// </summary>
public class NetWmiGapFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static NetWmiConfigRow Row(bool ip = true, int? netbios = 1, bool dhcp = true,
        bool lease = true, bool wins = false) =>
        new(ip, netbios, dhcp, lease, wins);

    [Fact]
    public void 五把鍵齊_計數對得上()
    {
        var facts = NetWmiGapFactsService.Collect(At, () =>
        [
            Row(netbios: 1, dhcp: true, lease: true),
            Row(netbios: 2, dhcp: true, lease: false),
            Row(netbios: null, dhcp: false, lease: false, wins: true),
        ]);
        Assert.Equal(5, facts.Count);
        Assert.Equal("net.wmi.gap", facts[0].Key);
        Assert.Equal("net.wmi.gap.ipenabled", facts[1].Key);
        Assert.Equal("net.wmi.gap.netbios", facts[2].Key);
        Assert.Equal("net.wmi.gap.lease", facts[3].Key);
        Assert.Equal("net.wmi.gap.wins", facts[4].Key);

        Assert.Equal(3, facts[0].NumericValue);   // 介面數
        Assert.Equal(3, facts[1].NumericValue);   // IpEnabled 全 true
        Assert.Equal(2, facts[2].NumericValue);   // NetbiosOptions 有回報 2 個
        Assert.Contains("啟用 1", facts[2].Value); // 值 1
        Assert.Contains("停用 1", facts[2].Value); // 值 2
        Assert.Contains("未提供 1 個", facts[2].Value);
        Assert.Equal(2, facts[3].NumericValue);   // DHCP 啟用 2 個
        Assert.Contains("可解析 1 個", facts[3].Value);
        Assert.Equal(1, facts[4].NumericValue);   // WINS proxy 1 個
    }

    [Fact]
    public void 不重複既有服務已取的面_MAC_IP不出現在盤點欄位()
    {
        // 查重收口：MAC／IP／閘道／DNS／MTU 已由 Win32_NetworkAdapter 與 NetworkInterface 取得，
        // 本盤點的讀取字面不得出現這些欄位。
        string source = NetWmiGapFactsService.ReadConfigQuery;
        foreach (string banned in new[] { "MACAddress", "IPAddress", "DefaultIPGateway", "DNSServerSearchOrder", "MTU", "DefaultTTL" })
            Assert.DoesNotContain(banned, source);
        Assert.Contains("TcpipNetbiosOptions", source);
        Assert.Contains("DHCPLeaseExpires", source);
        Assert.Contains("WINSEnableProxy", source);
    }

    [Fact]
    public void 讀不到_五把鍵全部ReadError且不帶數值()
    {
        var facts = NetWmiGapFactsService.Collect(At, () => null);
        Assert.Equal(5, facts.Count);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Null(f.NumericValue);
            Assert.False(f.Sensitive);
        });
    }

    [Fact]
    public void 只輸出計數_值裡不帶位址或名稱原值()
    {
        var facts = NetWmiGapFactsService.Collect(At, () => [Row()]);
        Assert.All(facts, f => Assert.False(f.Sensitive));
        Assert.All(facts, f => Assert.DoesNotContain("192.168.", f.Value));
    }
}
