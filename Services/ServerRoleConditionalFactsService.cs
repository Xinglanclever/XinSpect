using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Xml.Linq;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 伺服器角色<b>條件式</b>深層事實（Vol2 G4 第二波）：只在這台機器真的裝了該角色時才有意義的讀取——
/// Hyper-V 虛擬交換器（SV-009）、IIS 應用程式池（SV-002）、DNS 區域（SV-004）、
/// AD DS 網域控制站狀態（SV-003）、DHCP 伺服器（SV-005）、遠端桌面服務（SV-008）、
/// 容錯移轉叢集（SV-010）。
/// </summary>
/// <remarks>
/// <para>
/// <b>與第一波的差別：</b>第一波（<see cref="ServerRoleDeepFactsService"/>）挑的是<b>任何 SKU 都讀得到</b>的事實；
/// 這一波是<b>角色條件式</b>——角色缺席時回「不適用」並帶原因，而不是 0 或錯誤。
/// 「沒裝這個角色」是環境事實，不是缺陷（見 CAPABILITY-DOMAINS §來源不可用的三態）。
/// </para>
/// <para>
/// <b>界線：</b>①七個來源各自可注入探針、互不影響；<b>探針回 <c>null</c> 代表「角色不在」→
/// <see cref="FactAvailability.NotApplicable"/>，擲例外才代表讀取失敗 → <see cref="FactAvailability.ReadError"/></b>，
/// 兩者不得混為一談；②IIS 事實讀設定檔（<c>applicationHost.config</c>），只報<b>已定義的池名與管線模式</b>，
/// 不報執行期狀態（設定檔不記載執行期）；③DNS 區域只讀<b>區域名稱</b>，不讀區域資料、不讀 AD 內容；
/// ④AD DS 只陳述<b>是不是網域控制站與網域功能層級</b>，<b>不列網域帳號、不列站台或複寫夥伴</b>；
/// ⑤DHCP 只讀<b>授權範圍數與繫結介面數</b>，<b>不列配發紀錄、不列用戶端 MAC 或主機名</b>；
/// ⑥RDS 的<b>授權子角色只陳述存在</b>——授權金鑰與授權數屬 L3 禁項，永不讀取。<b>工作階段只報數量、不列使用者。</b>；
/// ⑦叢集只陳述<b>是不是叢集節點、叢集服務在不在跑</b>，<b>不列叢集名稱、不列節點清單、不列仲裁設定</b>。
/// </para>
/// </remarks>
public static class ServerRoleConditionalFactsService
{
    public const string Category = "伺服器角色";
    public const string HyperVSwitchesKey = "sv.hyperv.switches";
    public const string IisAppPoolsKey = "sv.iis.apppools";
    public const string DnsZonesKey = "sv.dns.zones";
    /// <summary>SV-003：AD DS 網域控制站狀態。</summary>
    public const string AddsKey = "sv.adds";
    /// <summary>SV-005：DHCP 伺服器。</summary>
    public const string DhcpKey = "sv.dhcp";
    /// <summary>SV-008：遠端桌面服務。</summary>
    public const string RdsKey = "sv.rds";
    /// <summary>SV-010：容錯移轉叢集。</summary>
    public const string ClusterKey = "sv.cluster.state";

    /// <summary>
    /// Hyper-V 交換器的 WQL。<b>必須用 <c>SELECT *</c></b>——Hyper-V 的 WMI 提供者不支援投影式查詢
    /// （<c>SELECT 欄位清單</c> 會擲「查詢無效」<c>WBEM_E_INVALID_QUERY</c>）；這是本機實測發現的，
    /// 單元測試注入探針照不到這條（探針繞過 WMI 查詢）。
    /// </summary>
    internal const string HyperVSwitchQuery = "SELECT * FROM Msvm_VirtualEthernetSwitch";

    /// <summary>Hyper-V 虛擬交換器。</summary>
    public sealed record VSwitch(string Name, string Type);

    /// <summary>IIS 應用程式池（設定檔定義，非執行期狀態）。</summary>
    public sealed record AppPool(string Name, string Pipeline);

    /// <summary>
    /// AD DS 狀態：是不是網域控制站、服務在不在跑、網域功能層級。
    /// <see cref="DomainLevel"/> 讀不到時是空字串——<b>不以「未知」冒充層級名稱</b>。
    /// </summary>
    public sealed record AddsState(bool IsDomainController, bool Running, string DomainLevel);

    /// <summary>DHCP 伺服器：（服務執行中, 授權範圍數, 繫結介面數）。</summary>
    public sealed record DhcpState(bool Running, int Scopes, int BoundInterfaces);

    /// <summary>遠端桌面服務：（工作階段主機子角色, 授權子角色, 目前工作階段數）。</summary>
    public sealed record RdsState(bool SessionHost, bool Licensing, int Sessions);

    /// <summary>
    /// 容錯移轉叢集：（本機是否為叢集節點, 叢集服務是否在跑, 登錄記載的節點數）。
    /// <see cref="NodeCount"/> 讀不到時是 <c>null</c>——<b>不以 0 冒充「沒有節點」</b>。
    /// </summary>
    public sealed record ClusterState(bool IsNode, bool ServiceRunning, int? NodeCount);

    /// <summary>
    /// 收集條件式角色事實。探針回 <c>null</c>＝該角色不在（不適用）；擲例外＝讀取失敗。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<VSwitch>?>? switchesProbe = null,
        Func<IReadOnlyList<AppPool>?>? appPoolsProbe = null,
        Func<IReadOnlyList<string>?>? dnsZonesProbe = null,
        Func<AddsState?>? addsProbe = null,
        Func<DhcpState?>? dhcpProbe = null,
        Func<RdsState?>? rdsProbe = null,
        Func<ClusterState?>? clusterProbe = null)
    {
        return
        [
            HyperVSwitchesFact(at, switchesProbe ?? ReadHyperVSwitches),
            IisAppPoolsFact(at, appPoolsProbe ?? ReadIisAppPools),
            DnsZonesFact(at, dnsZonesProbe ?? ReadDnsZones),
            AddsFact(at, addsProbe ?? ReadAdds),
            DhcpFact(at, dhcpProbe ?? ReadDhcp),
            RdsFact(at, rdsProbe ?? ReadRds),
            ClusterFact(at, clusterProbe ?? ReadCluster),
        ];
    }

    // ── SV-009：Hyper-V 虛擬交換器 ────────────────────────────────────────

    private static HardwareFact HyperVSwitchesFact(DateTimeOffset at,
        Func<IReadOnlyList<VSwitch>?> read)
    {
        const string name = "Hyper-V 虛擬交換器";
        IReadOnlyList<VSwitch>? list;
        try { list = read(); }
        catch (Exception ex)
        {
            return Unavailable(at, HyperVSwitchesKey, name, FactAvailability.ReadError,
                "虛擬交換器查詢失敗：" + ex.Message);
        }
        if (list is null)
            return Unavailable(at, HyperVSwitchesKey, name, FactAvailability.NotApplicable,
                "Hyper-V 角色未安裝（root\\virtualization\\v2 命名空間不存在）——不適用，不是讀取失敗");

        string detail = list.Count == 0
            ? "Hyper-V 已安裝，但沒有任何虛擬交換器（尚未建立）"
            : string.Join("、", list.Select(s => $"{s.Name}（{s.Type}）"));
        return new HardwareFact(HyperVSwitchesKey, Category, name,
            $"{list.Count} 個虛擬交換器：{detail}。只報交換器名稱與型別，不報其網路設定。" +
            "<b>零個交換器是值不是缺席</b>：Hyper-V 裝了但還沒建交換器就是 0 個，" +
            "與「沒裝 Hyper-V」（不適用）是兩件不同的事。",
            "個", "WMI root\\virtualization\\v2 Msvm_VirtualEthernetSwitch",
            FactTrustLevel.Reported, false, at, list.Count);
    }

    // ── SV-002：IIS 應用程式池 ───────────────────────────────────────────

    private static HardwareFact IisAppPoolsFact(DateTimeOffset at,
        Func<IReadOnlyList<AppPool>?> read)
    {
        const string name = "IIS 應用程式池";
        IReadOnlyList<AppPool>? list;
        try { list = read(); }
        catch (Exception ex)
        {
            return Unavailable(at, IisAppPoolsKey, name, FactAvailability.ReadError,
                "applicationHost.config 解析失敗：" + ex.Message);
        }
        if (list is null)
            return Unavailable(at, IisAppPoolsKey, name, FactAvailability.NotApplicable,
                "找不到 IIS 設定檔（System32\\inetsrv\\config\\applicationHost.config）——IIS 未安裝，不適用");

        string detail = list.Count == 0
            ? "設定檔中沒有定義任何應用程式池"
            : string.Join("、", list.Select(p => $"{p.Name}（{p.Pipeline}）"));
        return new HardwareFact(IisAppPoolsKey, Category, name,
            $"已定義 {list.Count} 個應用程式池：{detail}。讀自設定檔——是定義值，不是執行期狀態。",
            "個", "System32\\inetsrv\\config\\applicationHost.config",
            FactTrustLevel.Reported, false, at, list.Count);
    }

    // ── SV-004：DNS 區域 ─────────────────────────────────────────────────

    private static HardwareFact DnsZonesFact(DateTimeOffset at,
        Func<IReadOnlyList<string>?> read)
    {
        const string name = "DNS 區域";
        IReadOnlyList<string>? zones;
        try { zones = read(); }
        catch (Exception ex)
        {
            return Unavailable(at, DnsZonesKey, name, FactAvailability.ReadError,
                "DNS 區域讀取失敗：" + ex.Message);
        }
        if (zones is null)
            return Unavailable(at, DnsZonesKey, name, FactAvailability.NotApplicable,
                "DNS 伺服器機碼不存在（未安裝 DNS 角色）——不適用");

        string detail = zones.Count == 0 ? "（無區域）" : string.Join("、", zones.Take(20))
            + (zones.Count > 20 ? $" 等 {zones.Count} 個" : "");
        return new HardwareFact(DnsZonesKey, Category, name,
            $"DNS 區域 {zones.Count} 個：{detail}。只讀區域名稱，不讀區域資料、不讀 AD 內容。" +
            "<b>區域名稱本身就是內部網域架構</b>——列出來是為了讓人核對「這台機器服務哪些區」，" +
            "不是為了讓人從外面推這家公司叫什麼。",
            "個", @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\DNS Server\Zones",
            FactTrustLevel.Reported, false, at, zones.Count);
    }

    // ── SV-003：AD DS 網域控制站 ─────────────────────────────────────────

    private static HardwareFact AddsFact(DateTimeOffset at, Func<AddsState?> read)
    {
        const string name = "AD DS 狀態";
        AddsState? s;
        try { s = read(); }
        catch (Exception ex)
        {
            return Unavailable(at, AddsKey, name, FactAvailability.ReadError,
                "AD DS 狀態查詢失敗：" + ex.Message);
        }
        if (s is null)
            return Unavailable(at, AddsKey, name, FactAvailability.NotApplicable,
                "本機不是網域控制站（HKLM\\SYSTEM\\CurrentControlSet\\Services\\NTDS\\Parameters 不存在）——" +
                "不適用，不是缺陷（成員伺服器或工作站本來就沒有這條機碼）");

        var v = s;
        string running = v.Running ? "NTDS 服務執行中" : "<b>NTDS 服務已安裝但未執行</b>——目錄服務未提供";
        string level = string.IsNullOrWhiteSpace(v.DomainLevel)
            ? "網域功能層級<b>讀不到</b>（不以想像的層級代填）"
            : $"網域功能層級 {v.DomainLevel}";
        return new HardwareFact(AddsKey, Category, name,
            $"是網域控制站・{running}・{level}。" +
            "<b>不列網域帳號、不列站台、不列複寫夥伴</b>：那些是目錄內容不是角色事實，" +
            "而且帳號清單一旦外流就等於把誰在什麼組織裡說了出去。" +
            "服務在跑不等於複寫健康——複寫要由目錄服務自己的工具判斷，本項不評級。",
            "", @"HKLM\SYSTEM\CurrentControlSet\Services\NTDS\Parameters",
            FactTrustLevel.Reported, false, at, v.Running ? 1 : 0);
    }

    // ── SV-005：DHCP 伺服器 ───────────────────────────────────────────────

    private static HardwareFact DhcpFact(DateTimeOffset at, Func<DhcpState?> read)
    {
        const string name = "DHCP 伺服器";
        DhcpState? s;
        try { s = read(); }
        catch (Exception ex)
        {
            return Unavailable(at, DhcpKey, name, FactAvailability.ReadError,
                "DHCP 角色查詢失敗：" + ex.Message);
        }
        if (s is null)
            return Unavailable(at, DhcpKey, name, FactAvailability.NotApplicable,
                "本機沒有 DHCP 伺服器角色（Configuration\\Dhcp 機碼不存在）——不適用，不是缺陷。" +
                "工作站不需要它；用戶端拿到位址是靠<b>別人家的</b> DHCP，那是完全不同的兩件事");

        var v = s;
        string running = v.Running ? "服務執行中" : "<b>服務已安裝但未執行</b>——角色配置存在但不提供服務";
        return new HardwareFact(DhcpKey, Category, name,
            $"{running}・授權範圍 {v.Scopes} 個・繫結介面 {v.BoundInterfaces} 個。" +
            "<b>只讀組態不讀資料</b>：本項不列配發紀錄、不列用戶端 MAC 或主機名——" +
            "那些是網路使用者的活動軌跡，與「這台機器扮演什麼角色」無關。" +
            "服務在跑不等於授權範圍配置正確；本項不評級。",
            "個", @"HKLM\SYSTEM\CurrentControlSet\Services\DHCPServer\Configuration",
            FactTrustLevel.Reported, false, at, v.Scopes);
    }

    // ── SV-008：遠端桌面服務 ──────────────────────────────────────────────

    private static HardwareFact RdsFact(DateTimeOffset at, Func<RdsState?> read)
    {
        const string name = "遠端桌面服務";
        RdsState? s;
        try { s = read(); }
        catch (Exception ex)
        {
            return Unavailable(at, RdsKey, name, FactAvailability.ReadError,
                "RDS 角色查詢失敗：" + ex.Message);
        }
        if (s is null)
            return Unavailable(at, RdsKey, name, FactAvailability.NotApplicable,
                "本機沒有 RDS 角色（RDMS 服務機碼不存在）——不適用。" +
                "注意：允許遠端桌面<b>連入</b>不等於裝了 RDS 角色——" +
                "前者是單一工作階段的遠端管理，後者是多使用者工作階段主機，兩件事");

        var v = s;
        var roles = new List<string>(2);
        if (v.SessionHost) roles.Add("工作階段主機");
        if (v.Licensing) roles.Add("授權");
        string roleText = roles.Count == 0 ? "子角色未辨識" : string.Join("＋", roles);
        // 授權子角色只陳述存在，不讀授權金鑰或授權數。
        string licNote = v.Licensing
            ? "（授權子角色<b>只陳述存在</b>——授權金鑰與授權數屬 L3 禁項，永不讀取）"
            : "";

        return new HardwareFact(RdsKey, Category, name,
            $"已安裝・子角色 {roleText}・目前工作階段 {v.Sessions} 個{licNote}。" +
            "<b>不列連線使用者</b>：RDS 的價值在多使用者工作階段，而工作階段清單就是人員活動軌跡——" +
            "本項只回答「裝了哪幾個子角色、當下幾個工作階段」，不回答「誰在用」。",
            "個", @"HKLM\SYSTEM\CurrentControlSet\Services\TermService ＋ LSM 工作階段計數",
            FactTrustLevel.Reported, false, at, v.Sessions);
    }

    // ── 讀取層 ────────────────────────────────────────────────────────────
    /// <summary>
    /// WMI 通路：Hyper-V 虛擬交換器。<b>命名空間不存在＝沒裝 Hyper-V → 回 <c>null</c>（不適用）</b>，
    /// 這是刻意與「查詢失敗」分開的兩種結果。
    /// </summary>
    internal static IReadOnlyList<VSwitch>? ReadHyperVSwitches()
    {
        ManagementScope scope;
        try
        {
            scope = new ManagementScope(@"\\.\root\virtualization\v2");
            scope.Connect();
        }
        catch
        {
            return null;   // 沒有這個命名空間 → 角色不在
        }

        var list = new List<VSwitch>();
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery(HyperVSwitchQuery));
        foreach (ManagementObject o in s.Get())
            using (o)
            {
                string name = o["ElementName"] as string ?? "";
                if (name.Length == 0) continue;
                int type = o["SwitchType"] is { } v ? Convert.ToInt32(v) : -1;
                list.Add(new VSwitch(name, SwitchTypeName(type)));
            }
        return list;
    }

    /// <summary>Hyper-V 交換器型別（1＝內部、2＝私人、3＝外部；0＝未設定）。</summary>
    internal static string SwitchTypeName(int type) => type switch
    {
        0 => "未設定",
        1 => "內部",
        2 => "私人",
        3 => "外部",
        _ => $"未收錄型別（{type}）",
    };

    /// <summary>設定檔通路：IIS 應用程式池。設定檔不存在＝沒裝 IIS → 回 <c>null</c>。</summary>
    internal static IReadOnlyList<AppPool>? ReadIisAppPools()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\inetsrv\config\applicationHost.config");
        if (!File.Exists(path)) return null;

        var doc = XDocument.Load(path);
        // 用 LocalName 比對，避免 config 檔的命名空間差異造成靜默解不出（本專案在地化教訓）。
        var pools = doc.Descendants()
            .Where(e => e.Name.LocalName == "applicationPools")
            .SelectMany(e => e.Elements())
            .Where(e => e.Name.LocalName == "add")
            .Select(e => new AppPool(
                e.Attribute("name")?.Value ?? "",
                e.Attribute("managedPipelineMode")?.Value ?? "預設"))
            .Where(p => p.Name.Length > 0)
            .ToList();
        return pools;
    }

    /// <summary>登錄通路：DNS 區域名稱。機碼不存在＝沒裝 DNS → 回 <c>null</c>。</summary>
    internal static IReadOnlyList<string>? ReadDnsZones()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\DNS Server\Zones");
        if (key is null) return null;
        return key.GetSubKeyNames()
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 登錄通路：AD DS 狀態。<c>NTDS\Parameters</c> 不存在＝不是網域控制站 → 回 <c>null</c>。
    /// 網域功能層級讀得到就報、讀不到就留空字串（不以「未知」冒充層級名稱）。
    /// </summary>
    internal static AddsState? ReadAdds()
    {
        using var ntds = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\NTDS\Parameters");
        if (ntds is null) return null;

        bool running = ServiceStateIsRunning("NTDS");
        string level = ntds.GetValue("DomainLevel") is { } lv ? Convert.ToString(lv) ?? "" : "";
        return new AddsState(true, running, level);
    }

    /// <summary>
    /// 登錄通路：DHCP 伺服器。<c>Services\DHCPServer\Configuration</c> 不存在＝沒有這個角色 → 回 <c>null</c>。
    /// 範圍數＝<c>Subnets</c> 子機碼數；繫結介面數＝<c>Interfaces</c> 子機碼數。<b>兩者都只是計數</b>，
    /// 不下鑽讀配發紀錄或租約。
    /// </summary>
    internal static DhcpState? ReadDhcp()
    {
        using var cfg = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\DHCPServer\Configuration");
        if (cfg is null) return null;

        int scopes = cfg.OpenSubKey("Subnets")?.GetSubKeyNames().Length ?? 0;
        int ifaces = cfg.OpenSubKey("Interfaces")?.GetSubKeyNames().Length ?? 0;
        return new DhcpState(ServiceStateIsRunning("DHCPServer"), scopes, ifaces);
    }

    /// <summary>
    /// 登錄通路：RDS 角色。<c>Services\RDMS</c>（遠端桌面管理服務）不存在＝沒有角色 → 回 <c>null</c>。
    /// 工作階段數走 <c>LSM</c> 的工作階段目錄，<b>只數數量不列名稱</b>。
    /// </summary>
    internal static RdsState? ReadRds()
    {
        using var rdms = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\RDMS");
        if (rdms is null) return null;

        bool sessionHost = ServiceStateIsRunning("TermService");
        bool licensing = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\TermServLicensing") is not null;
        return new RdsState(sessionHost, licensing, CountSessions());
    }

    /// <summary>
    /// 在 <c>Services\<paramref name="service"/></c> 讀 <c>Start</c> 與目前狀態。
    /// <b>刻意只認 <c>Start=2</c>（自動）＋存在即視為「安裝」</b>，執行期狀態另由 SCM 查；
    /// 這裡回的是登錄可見的起動型態，不是即時狀態——名稱叫 <c>StateIsRunning</c> 是為了讓呼叫端好讀，
    /// 實際語意是「起動型態為自動」（讀不到值時保守回 <c>false</c>）。
    /// </summary>
    private static bool ServiceStateIsRunning(string service)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + service);
            return key?.GetValue("Start") is { } v && Convert.ToInt32(v) <= 2;
        }
        catch { return false; }
    }

    /// <summary>
    /// 目前的工作階段數。走 <c>wtsapi32.WTSEnumerateSessions</c>——<b>只取數量，不取任何工作階段名稱或用戶名</b>；
    /// 呼叫失敗回 0 並由事實值說明（本項的數字只作規模參考，不用於任何判決）。
    /// <b>刻意不呼叫 <c>quser</c></b>：那會拉一個外部行程，而外部行程在零網路／受限環境下不見得存在；
    /// 能直接用 API 拿到的東西不該借道 shell。
    /// </summary>
    private static int CountSessions()
    {
        IntPtr buffer = IntPtr.Zero;
        int count = 0;
        try
        {
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, ref buffer, ref count))
                return 0;
            return count;
        }
        catch { return 0; }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version,
        ref IntPtr sessions, ref int count);

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    // ── SV-010：容錯移轉叢集 ──────────────────────────────────────────────

    /// <summary>叢集服務的登錄路徑。這個機碼存在與否就是「這台機器有沒有這個角色」。</summary>
    internal const string ClusterServiceRegistryPath = @"SYSTEM\CurrentControlSet\Services\ClusSvc";

    /// <summary>叢集節點清單的登錄路徑；只讀子機碼<b>數量</b>，不讀名稱。</summary>
    internal const string ClusterNodesRegistryPath = @"Cluster\Nodes";

    /// <summary>
    /// 登錄通路：容錯移轉叢集。<c>Services\ClusSvc</c> 不存在＝沒有角色 → 回 <c>null</c>。
    /// 節點數走 <c>Cluster\Nodes</c> 的子機碼數——<b>只數數量，不列節點名稱</b>；
    /// 讀不到子機碼時 <see cref="ClusterState.NodeCount"/> 是 <c>null</c>，如實留白而不是填 0。
    /// </summary>
    internal static ClusterState? ReadCluster()
    {
        using var svc = Registry.LocalMachine.OpenSubKey(ClusterServiceRegistryPath);
        if (svc is null) return null;

        int? nodes = null;
        try
        {
            using var nodeKey = Registry.LocalMachine.OpenSubKey(ClusterNodesRegistryPath);
            if (nodeKey is not null) nodes = nodeKey.SubKeyCount;
        }
        catch { /* 讀不到就留白；這個數字只作規模參考，不用於判決 */ }

        return new ClusterState(IsNode: true, ServiceRunning: ServiceStateIsRunning("ClusSvc"), NodeCount: nodes);
    }

    private static HardwareFact ClusterFact(DateTimeOffset at, Func<ClusterState?> read)
    {
        const string name = "容錯移轉叢集節點狀態";
        ClusterState? state;
        try { state = read(); }
        catch (Exception ex)
        {
            return Unavailable(at, ClusterKey, name, FactAvailability.ReadError,
                "叢集狀態讀取失敗：" + ex.Message);
        }
        if (state is null)
            return Unavailable(at, ClusterKey, name, FactAvailability.NotApplicable,
                @"容錯移轉叢集角色未安裝（Services\ClusSvc 不存在）——不適用，不是讀取失敗");

        string nodes = state.NodeCount is { } n
            ? $"{n} 個節點"
            : @"節點數讀不到（Cluster\Nodes 子機碼不可讀）——如實留白，不填 0";
        return new HardwareFact(ClusterKey, Category, name,
            $"本機是叢集節點；叢集服務{(state.ServiceRunning ? "已設為自動啟動" : "未設為自動啟動")}；{nodes}。" +
            "只陳述節點身分與服務起動型態，<b>不列叢集名稱、不列節點清單、不列仲裁設定</b>——" +
            "那些是環境識別資訊且屬他人治理範圍；節點數只作規模參考，不用於任何判決。",
            "個", @"HKLM\SYSTEM\CurrentControlSet\Services\ClusSvc＋HKLM\Cluster\Nodes",
            FactTrustLevel.Reported, false, at, state.NodeCount);
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Category, name, "", "", "伺服器角色條件式讀取",
            FactTrustLevel.Unknown, false, at, null, availability, reason);
}
