using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace XinSpect;

/// <summary>
/// 以 System.Net.NetworkInformation 讀取網路介面卡靜態資訊，並每秒計算即時上/下行流量。
/// 初始化時建立列（就地更新），之後 Refresh() 依累計位元組差值換算速率。
/// </summary>
public sealed class NetworkService : ObservableObject
{
    private sealed class Prev { public long Rx, Tx; public long Ticks; }

    private readonly Dictionary<string, Prev> _prev = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public ObservableCollection<NetAdapterRow> Adapters { get; } = new();
    public MetricHistory TotalDown { get; } = new(90, "", null, "0");
    public MetricHistory TotalUp { get; } = new(90, "", null, "0");

    public double TotalDownBps { get; private set; }
    public double TotalUpBps { get; private set; }
    public string TotalDownText => NetAdapterRow.Rate(TotalDownBps);
    public string TotalUpText => NetAdapterRow.Rate(TotalUpBps);

    public NetworkService()
    {
        try { Build(); } catch { /* best-effort */ }
    }

    private void Build()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!Qualifies(nic)) continue;
            var row = new NetAdapterRow(nic.Id, nic.Name)
            {
                Description = nic.Description,
                TypeText = TypeZh(nic.NetworkInterfaceType),
                Mac = FormatMac(nic.GetPhysicalAddress()),
                LinkSpeedText = nic.Speed > 0 ? $"{nic.Speed / 1_000_000.0:0} Mbps" : "—",
            };
            FillIps(nic, row);
            FillConfig(nic, row);
            Adapters.Add(row);

            // 以「開機至今累計位元組」作為基準，避免首次 Refresh() 將整段歷史流量除以極短時距，
            // 誤報一筆遠超實際頻寬的虛假尖峰。讀不到統計時退回 0（首拍仍可能偏高，但屬少數例外）。
            long rx0 = 0, tx0 = 0;
            try { var st = nic.GetIPStatistics(); rx0 = st.BytesReceived; tx0 = st.BytesSent; } catch { }
            _prev[nic.Id] = new Prev { Rx = rx0, Tx = tx0, Ticks = _clock.ElapsedTicks };
        }
    }

    public void Refresh()
    {
        double totalDown = 0, totalUp = 0;
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return; }

        var byId = nics.ToDictionary(n => n.Id, n => n);

        foreach (var row in Adapters)
        {
            if (!byId.TryGetValue(row.Id, out var nic)) continue;

            long rx, tx;
            try { var st = nic.GetIPStatistics(); rx = st.BytesReceived; tx = st.BytesSent; }
            catch { continue; }

            long now = _clock.ElapsedTicks;
            if (_prev.TryGetValue(row.Id, out var p) && p.Ticks > 0)
            {
                double secs = (now - p.Ticks) / (double)Stopwatch.Frequency;
                if (secs > 0.05)
                {
                    double down = Math.Max(0, rx - p.Rx) / secs;
                    double up = Math.Max(0, tx - p.Tx) / secs;
                    row.DownBps = down;
                    row.UpBps = up;
                    row.DownHistory.Push(down);
                    row.UpHistory.Push(up);
                    totalDown += down;
                    totalUp += up;
                }
            }
            _prev[row.Id] = new Prev { Rx = rx, Tx = tx, Ticks = now };
            FillIps(nic, row);
        }

        TotalDownBps = totalDown;
        TotalUpBps = totalUp;
        TotalDown.Push(totalDown);
        TotalUp.Push(totalUp);

        OnPropertyChanged(nameof(TotalDownBps));
        OnPropertyChanged(nameof(TotalUpBps));
        OnPropertyChanged(nameof(TotalDownText));
        OnPropertyChanged(nameof(TotalUpText));
    }

    // 虛擬 / 篩選偽介面的描述特徵：WFP LightWeight Filter、QoS 排程器、WAN Miniport、
    // 核心偵錯介面等；這些常與實體網卡共用 MAC、無 IP，屬雜訊，應排除只留實體網卡。
    private static readonly string[] _pseudoMarkers =
    {
        "filter",       // 涵蓋 WFP / NDIS Light-Weight / VirtualBox NDIS / Npcap 等篩選偽介面
        "wfp",          // WFP Native / 802.3 MAC Layer
        "qos packet",   // QoS Packet Scheduler
        "kernel debug", // Microsoft Kernel Debug Network Adapter
        "miniport",     // WAN Miniport（PPP / IP / IPv6 協定樁）
        "pseudo",
    };

    private static bool Qualifies(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up) return false;
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return false;
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return false;

        string desc = (nic.Description ?? string.Empty).ToLowerInvariant();
        string name = (nic.Name ?? string.Empty).ToLowerInvariant();
        foreach (var m in _pseudoMarkers)
            if (desc.Contains(m) || name.Contains(m)) return false;

        return true;
    }

    private static void FillIps(NetworkInterface nic, NetAdapterRow row)
    {
        try
        {
            var props = nic.GetIPProperties();
            string v4 = "—", v6 = "—";
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && v4 == "—")
                    v4 = ua.Address.ToString();
                else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                         !ua.Address.IsIPv6LinkLocal && v6 == "—")
                    v6 = ua.Address.ToString();
            }
            row.Ipv4 = v4;
            row.Ipv6 = v6;
        }
        catch { }
    }

    /// <summary>讀取閘道 / 子網 / DNS / DHCP / MTU / DNS 尾碼（靜態組態，開機讀取一次）。</summary>
    private static void FillConfig(NetworkInterface nic, NetAdapterRow row)
    {
        try
        {
            var props = nic.GetIPProperties();

            // 預設閘道（取首個非空者）
            var gw = props.GatewayAddresses.FirstOrDefault(g => g?.Address != null)?.Address;
            if (gw != null) row.Gateway = gw.ToString();

            // 子網路遮罩：由 IPv4 單播位址的前綴長度換算為點分十進位
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                row.SubnetMask = PrefixToMask(ua.PrefixLength);
                break;
            }

            // DNS 伺服器（可多筆，以逗號分隔）
            var dns = props.DnsAddresses
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(a => a.ToString()).ToList();
            if (dns.Count > 0) row.Dns = string.Join("、", dns);

            // 連線特定 DNS 尾碼
            if (!string.IsNullOrWhiteSpace(props.DnsSuffix)) row.DnsSuffix = props.DnsSuffix;

            // DHCP 啟用狀態與伺服器、MTU（僅 IPv4 屬性提供）
            try
            {
                var v4 = props.GetIPv4Properties();
                if (v4 != null)
                {
                    string dhcp = v4.IsDhcpEnabled ? "啟用" : "停用（靜態）";
                    var dhcpSrv = props.DhcpServerAddresses.FirstOrDefault();
                    if (v4.IsDhcpEnabled && dhcpSrv != null) dhcp += $"（{dhcpSrv}）";
                    row.DhcpText = dhcp;
                    if (v4.Mtu > 0) row.MtuText = $"{v4.Mtu} 位元組";
                }
            }
            catch { /* 部分介面不提供 IPv4 屬性 */ }
        }
        catch { }
    }

    /// <summary>IPv4 前綴長度 → 點分十進位遮罩（例：24 → 255.255.255.0）。</summary>
    private static string PrefixToMask(int prefix)
    {
        if (prefix is < 0 or > 32) return "—";
        uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
        return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
    }

    private static string FormatMac(PhysicalAddress mac)
    {
        var b = mac.GetAddressBytes();
        return b.Length == 0 ? "—" : string.Join(":", b.Select(x => x.ToString("X2")));
    }

    private static string TypeZh(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Ethernet => "乙太網路",
        NetworkInterfaceType.Wireless80211 => "無線網路",
        NetworkInterfaceType.GigabitEthernet => "Gigabit 乙太網路",
        NetworkInterfaceType.Ppp => "PPP",
        _ => t.ToString(),
    };
}
