namespace XinSpect;

/// <summary>Win32_NetworkAdapterConfiguration 一列（只攜帶既有服務沒拿的欄位）。</summary>
/// <remarks>刻意<b>不含</b> MACAddress／IPAddress／閘道／DNS——那些已由別的服務取得（見類別摘要查重）。</remarks>
public sealed record NetWmiConfigRow(
    bool IpEnabled,
    int? NetbiosOptions,
    bool DhcpEnabled,
    bool LeaseTimesReadable,
    bool WinsProxyEnabled);

/// <summary>
/// U10-OS-0040（候選鍵 <c>wmi.net.gap</c>；實際鍵 <see cref="NetWmiGapKey"/> 及其固定子鍵）：<c>Win32_NetworkAdapterConfiguration</c> 的
/// <b>補充盤點</b>——判定文檔原句「補 Win32_NetworkAdapterConfiguration」，全庫此前 0 命中。
///
/// <para>
/// <b>查重收口（只做既有服務沒拿的欄位）</b>：
/// MAC——<see cref="NicHealthFactsService"/> 已用 Win32_NetworkAdapter.MACAddress；
/// IP／閘道／子網／DNS／DHCP 啟用狀態／MTU／DNS 尾碼——Services/NetworkService.cs 的
/// <c>FillConfig</c> 已由 NetworkInterface 取得；虛擬網卡分類——OperationsInventoryFactsService 已列舉
/// Win32_NetworkAdapter。重複的面一律不再取。本服務只補 Configuration 類別上、
/// 上述服務都沒碰過的欄位：<c>IpEnabled</c>、<c>TcpipNetbiosOptions</c>、
/// <c>DHCPLeaseObtained／DHCPLeaseExpires</c>（租約起訖可讀性）、<c>WINSEnableProxy</c>。
/// </para>
///
/// <para>
/// 敏感度：整組只輸出<b>計數與存在性</b>，不輸出位址、名稱或租約時間原值。
/// 成員固定，故以固定子鍵出（不用家族前綴）。
/// </para>
/// </summary>
public static class NetWmiGapFactsService
{
    public const string NetWmiGapKey = "net.wmi.gap";
    public const string NetWmiGapIpEnabledKey = "net.wmi.gap.ipenabled";
    public const string NetWmiGapNetbiosKey = "net.wmi.gap.netbios";
    public const string NetWmiGapLeaseKey = "net.wmi.gap.lease";
    public const string NetWmiGapWinsKey = "net.wmi.gap.wins";

    private const string Category = "網路";
    private const string Source = "WMI Win32_NetworkAdapterConfiguration（唯讀）";

    public sealed record NetWmiGapState(
        int Interfaces, int IpEnabled, int NetbiosReported, int NetbiosEnabled, int NetbiosDisabled,
        int DhcpEnabled, int LeaseTimesReadable, int WinsProxyEnabled);

    public delegate IReadOnlyList<NetWmiConfigRow>? NetWmiConfigProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, NetWmiConfigProbe? probe = null)
    {
        IReadOnlyList<NetWmiConfigRow>? rows;
        try { rows = probe is null ? ReadConfig() : probe(); } catch { rows = null; }
        if (rows is null)
        {
            return
            [
                Unavailable(at, NetWmiGapKey, "NetworkAdapterConfiguration 補充盤點", "WMI 列舉失敗——不猜介面數"),
                Unavailable(at, NetWmiGapIpEnabledKey, "IP 啟用介面數", "WMI 列舉失敗——不猜介面數"),
                Unavailable(at, NetWmiGapNetbiosKey, "NetBIOS over TCP/IP 設定", "WMI 列舉失敗——不猜 NetBIOS 分佈"),
                Unavailable(at, NetWmiGapLeaseKey, "DHCP 租約起訖可讀性", "WMI 列舉失敗——不猜租約"),
                Unavailable(at, NetWmiGapWinsKey, "WINS Proxy 啟用數", "WMI 列舉失敗——不猜 WINS"),
            ];
        }

        int ipEnabled = rows.Count(r => r.IpEnabled);
        int netbiosReported = rows.Count(r => r.NetbiosOptions is not null);
        int netbiosEnabled = rows.Count(r => r.NetbiosOptions is 0 or 1);   // 0=DHCP 啟用、1=啟用
        int netbiosDisabled = rows.Count(r => r.NetbiosOptions == 2);       // 2=停用
        int dhcp = rows.Count(r => r.DhcpEnabled);
        int leaseReadable = rows.Count(r => r.DhcpEnabled && r.LeaseTimesReadable);
        int winsProxy = rows.Count(r => r.WinsProxyEnabled);

        return
        [
            new HardwareFact(NetWmiGapKey, Category, "NetworkAdapterConfiguration 補充盤點",
                $"介面 {rows.Count} 個（IP 啟用 {ipEnabled}）。MAC／IP／閘道／DNS／MTU 已由既有服務取得，此處不重複；" +
                "本盤點只補 Configuration 類別上既有服務沒拿的欄位（IpEnabled／TcpipNetbiosOptions／DHCP 租約起訖／WINSEnableProxy）。",
                "個", Source, FactTrustLevel.Reported, false, at, rows.Count),
            new HardwareFact(NetWmiGapIpEnabledKey, Category, "IP 啟用介面數",
                $"{ipEnabled} / {rows.Count} 個介面 IpEnabled=True。", "個", Source,
                FactTrustLevel.Reported, false, at, ipEnabled),
            new HardwareFact(NetWmiGapNetbiosKey, Category, "NetBIOS over TCP/IP 設定",
                $"TcpipNetbiosOptions：有回報 {netbiosReported} 個（啟用 {netbiosEnabled}、停用 {netbiosDisabled}）、未提供 {rows.Count - netbiosReported} 個。" +
                "未提供不等於停用。", "個", Source, FactTrustLevel.Reported, false, at, netbiosReported),
            new HardwareFact(NetWmiGapLeaseKey, Category, "DHCP 租約起訖可讀性",
                $"DHCP 啟用介面 {dhcp} 個，其中租約起訖時間可解析 {leaseReadable} 個。只報可讀性，不列時間原值。",
                "個", Source, FactTrustLevel.Reported, false, at, dhcp),
            new HardwareFact(NetWmiGapWinsKey, Category, "WINS Proxy 啟用數",
                $"WINSEnableProxy=True 的介面 {winsProxy} 個。存在性不推斷用途。", "個", Source,
                FactTrustLevel.Reported, false, at, winsProxy),
        ];
    }

    /// <summary>查詢字面（internal 供守門測試釘住「不重複既有服務已取的面」）。</summary>
    internal const string ReadConfigQuery =
        "SELECT IpEnabled, TcpipNetbiosOptions, DHCPEnabled, DHCPLeaseObtained, DHCPLeaseExpires, WINSEnableProxy FROM Win32_NetworkAdapterConfiguration";

    /// <summary>WMI 通路（極薄）：Configuration 面的補充欄位；查詢失敗回 null 標三態。</summary>
    internal static IReadOnlyList<NetWmiConfigRow>? ReadConfig()
    {
        try
        {
            var list = new List<NetWmiConfigRow>();
            using var s = new System.Management.ManagementObjectSearcher(ReadConfigQuery);
            foreach (System.Management.ManagementObject o in s.Get()) using (o)
            {
                // 租約時間是 DMTF 字串；解析失敗如實標「不可讀」，不猜值。
                bool leaseReadable = false;
                try
                {
                    if (o["DHCPLeaseObtained"] is not null && o["DHCPLeaseExpires"] is not null)
                    {
                        _ = System.Management.ManagementDateTimeConverter.ToDateTime(o["DHCPLeaseObtained"].ToString()!);
                        _ = System.Management.ManagementDateTimeConverter.ToDateTime(o["DHCPLeaseExpires"].ToString()!);
                        leaseReadable = true;
                    }
                }
                catch { leaseReadable = false; }

                list.Add(new NetWmiConfigRow(
                    o["IpEnabled"] is bool ip && ip,
                    o["TcpipNetbiosOptions"] is null ? null : Convert.ToInt32(o["TcpipNetbiosOptions"]),
                    o["DHCPEnabled"] is bool dhcp && dhcp,
                    leaseReadable,
                    o["WINSEnableProxy"] is bool wins && wins));
            }
            return list;
        }
        catch { return null; }
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name, string reason)
        => new(key, Category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null,
            FactAvailability.ReadError, reason);
}
