using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace XinSpect;

/// <summary>
/// 伺服器角色<b>深層</b>事實（Vol2 批次 D／G4 第一波）：把「這台機器扮演哪些伺服器角色」
/// 從安裝清單推進到<b>實際運作的角色服務、授權狀態與共用</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>與既有能力的關係：</b>角色盤點（SV-001，<see cref="RoleSurfaceFactsService"/>）、
/// Server Core 判定（SV-011，<see cref="ServerModeFactsService"/>）與合規摘要（SV-014，
/// <see cref="ServerComplianceFactsService"/>）在本專案<b>早已存在</b>。本項<b>不重做</b>那一層——
/// 只補三個原本沒有事實鍵的面向：角色服務是否真的在跑（SV-013）、授權與啟用（SV-012）、
/// 檔案與列印共用（SV-006／SV-007）。
/// </para>
/// <para>
/// <b>界線：</b>①「服務在跑」不等於「角色配置正確」——只陳述服務狀態，不評級、不下紅黃判決；
/// ②授權事實<b>刻意不讀產品金鑰</b>（金鑰屬 L3 禁項，查詢只取 Name／LicenseStatus／Description，
/// 不取 PartialProductKey）；③共用事實只列<b>共用名稱</b>，<b>刻意不披露本機路徑</b>
/// （路徑可能含使用者資料夾名稱）；所列共用含系統預設 <c>C$</c>／<c>ADMIN$</c>／<c>IPC$</c>，
/// 有無共用不等於網路是否可及。讀不到一律三態，不以 0 冒充。
/// </para>
/// </remarks>
public static class ServerRoleDeepFactsService
{
    public const string Category = "伺服器角色";
    public const string RoleServicesKey = "sv.roles.services";
    public const string LicensingKey = "sv.licensing";
    public const string SharesKey = "sv.shares";

    private const string Source = "WMI root\\CIMV2（Win32_Service／SoftwareLicensingProduct／Win32_Share）";
    private const long ShareTypeMask = 0xFF;

    /// <summary>一個角色服務在掃描中的狀態。</summary>
    public sealed record RoleServiceState(string Name, string DisplayName, bool Running);

    /// <summary>Windows 授權產品（不含金鑰）。</summary>
    public sealed record LicenseInfo(string Name, int LicenseStatus, string Description);

    /// <summary>
    /// 一個伺服器共用（只帶名稱與型別，不帶本機路徑）。
    /// <c>Type</c> 用 <see cref="long"/> 承載：系統預設共用（<c>C$</c>／<c>ADMIN$</c>）帶特殊位元 <c>0x80000000</c>，
    /// 以 <c>int</c> 承接會溢位；由此回 <see cref="FactAvailability.ReadError"/>（本機實測發現）。
    /// </summary>
    public sealed record ShareEntry(string Name, long Type);

    /// <summary>
    /// 這台機器上「角色定義型」的固定服務清單：服務名稱 → 它代表的角色。
    /// 只認固定清單，不把一般服務塞進來——「有裝這個角色」必須是可列舉、可稽核的集合。
    /// </summary>
    private static readonly (string Service, string Role)[] RoleServices =
    [
        ("NTDS", "AD DS（網域控制站）"),
        ("DNS", "DNS 伺服器"),
        ("DHCPServer", "DHCP 伺服器"),
        ("W3SVC", "IIS 網站（Web 伺服器）"),
        ("WAS", "IIS 應用程式池（WAS）"),
        ("LanmanServer", "檔案伺服器"),
        ("Spooler", "列印伺服器"),
        ("vmms", "Hyper-V"),
        ("clussvc", "容錯移轉叢集"),
        ("WsusService", "Windows Server Update Services"),
        ("TermService", "遠端桌面（RDS）"),
        ("WinTarget", "iSCSI 目標伺服器"),
        ("Replicator", "儲存體複本"),
        ("SDRSVC", "Windows Server Backup"),
        ("Dedup", "資料重複資料刪除"),
        ("MSiSCSI", "多重路徑 I/O"),
        ("NfsService", "NFS 伺服器"),
        ("MSDTC", "分散式交易協調器"),
    ];

    /// <summary>
    /// 收集角色深層事實。三個來源各自可透過探針注入，互不影響；任一來源讀不到只影響它自己那把鍵。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<RoleServiceState>?>? servicesProbe = null,
        Func<LicenseInfo?>? licenseProbe = null,
        Func<IReadOnlyList<ShareEntry>?>? sharesProbe = null)
    {
        var (services, servicesError) = TryRead(servicesProbe ?? ReadRoleServices);
        var (license, licenseError) = TryRead(licenseProbe ?? ReadLicensing);
        var (shares, sharesError) = TryRead(sharesProbe ?? ReadShares);

        return
        [
            RoleServicesFact(at, services, servicesError),
            LicensingFact(at, license, licenseError),
            SharesFact(at, shares, sharesError),
        ];
    }

    // ── SV-013：角色服務 ─────────────────────────────────────────────────

    private static HardwareFact RoleServicesFact(DateTimeOffset at,
        IReadOnlyList<RoleServiceState>? services, string? error)
    {
        const string name = "伺服器角色服務";
        if (error is not null || services is null)
            return Unavailable(at, RoleServicesKey, name, FactAvailability.ReadError,
                error is null
                    ? "服務清單讀不到（未回傳且無例外）——不猜、不畫成 0 個角色"
                    : "服務清單讀取失敗：" + error);

        var byName = services.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var found = new List<(string Role, string Service, bool Running)>();
        foreach (var (service, role) in RoleServices)
            if (byName.TryGetValue(service, out var st))
                found.Add((role, st.Name, st.Running));

        int running = found.Count(f => f.Running);
        string detail = found.Count == 0
            ? "固定清單中的角色服務一個都沒偵測到（此機可能不是以伺服器角色運作）"
            : string.Join("、", found.Select(f => $"{f.Role}（{f.Service}，{(f.Running ? "執行中" : "已安裝未執行")}）"));

        return new HardwareFact(RoleServicesKey, Category, name,
            $"偵測到 {found.Count} 個角色服務，其中 {running} 個執行中：{detail}。" +
            "只列固定角色清單中的服務，不代表全部服務；服務在跑不等於角色配置正確，本項不評級。",
            "個", Source, FactTrustLevel.Reported, false, at, found.Count);
    }

    // ── SV-012：授權與啟用（不含金鑰）──────────────────────────────────────

    private static HardwareFact LicensingFact(DateTimeOffset at, LicenseInfo? license, string? error)
    {
        const string name = "授權與啟用";
        if (error is not null)
            return Unavailable(at, LicensingKey, name, FactAvailability.ReadError,
                "授權查詢失敗：" + error);
        if (license is null)
            return Unavailable(at, LicensingKey, name, FactAvailability.NotApplicable,
                "找不到已安裝的 Windows 授權產品（SoftwareLicensingProduct 無符合列）——如實標不適用，不猜授權狀態");

        string stateText = license.LicenseStatus switch
        {
            0 => "未啟用（Unlicensed）",
            1 => "已啟用（Licensed）",
            2 => "初始寬限期（OOBGrace）",
            3 => "寬限期已過（OOTGrace）",
            4 => "非正版寬限期（NonGenuineGrace）",
            5 => "通知模式（Notification）",
            6 => "延長寬限期（ExtendedGrace）",
            _ => $"未收錄的授權狀態（{license.LicenseStatus}）",
        };
        string desc = string.IsNullOrWhiteSpace(license.Description) ? "" : $"・{license.Description.Trim()}";

        return new HardwareFact(LicensingKey, Category, name,
            $"{license.Name.Trim()}・{stateText}（LicenseStatus={license.LicenseStatus}）{desc}。" +
            "授權事實刻意不讀產品金鑰（金鑰屬禁項），只報狀態與版本描述。",
            "", "WMI root\\CIMV2 SoftwareLicensingProduct（不含金鑰）",
            FactTrustLevel.Reported, false, at, license.LicenseStatus);
    }

    // ── SV-006／SV-007：檔案與列印共用 ────────────────────────────────────

    private static HardwareFact SharesFact(DateTimeOffset at,
        IReadOnlyList<ShareEntry>? shares, string? error)
    {
        const string name = "檔案與列印共用";
        if (error is not null || shares is null)
            return Unavailable(at, SharesKey, name, FactAvailability.ReadError,
                error is null
                    ? "共用清單讀不到（未回傳且無例外）——不畫成 0 個共用"
                    : "共用清單讀取失敗：" + error);

        var disk = shares.Where(s => (s.Type & ShareTypeMask) == 0).ToList();
        var print = shares.Where(s => (s.Type & ShareTypeMask) == 1).ToList();

        // 只披露共用名稱，不含本機路徑——路徑可能帶出使用者資料夾名稱。
        string diskNames = disk.Count == 0 ? "—" : string.Join("、", disk.Select(s => s.Name));
        return new HardwareFact(SharesKey, Category, name,
            $"檔案共用 {disk.Count} 個（{diskNames}）、列印共用 {print.Count} 個。" +
            "含系統預設 C$／ADMIN$／IPC$；只列共用名稱、不披露本機路徑；有共用不等於網路可及。",
            "個", "WMI root\\CIMV2 Win32_Share", FactTrustLevel.Reported, false, at, disk.Count);
    }

    // ── 讀取層（WMI，零外部程序、零特權需求）──────────────────────────────

    /// <summary>WMI 通路：列舉所有服務並回傳名稱／顯示名稱／狀態。</summary>
    internal static IReadOnlyList<RoleServiceState> ReadRoleServices()
    {
        var list = new List<RoleServiceState>();
        using var s = new ManagementObjectSearcher("root\\CIMV2",
            "SELECT Name, DisplayName, State FROM Win32_Service");
        foreach (ManagementObject o in s.Get())
            using (o)
            {
                string name = o["Name"] as string ?? "";
                if (name.Length == 0) continue;
                string display = o["DisplayName"] as string ?? name;
                string state = o["State"] as string ?? "";
                list.Add(new RoleServiceState(name, display,
                    state.Equals("Running", StringComparison.OrdinalIgnoreCase)));
            }
        return list;
    }

    /// <summary>
    /// WMI 通路：取 Windows 授權產品。<b>刻意不 SELECT PartialProductKey</b>——
    /// 金鑰屬 L3 禁項，查詢只取狀態與描述。查不到回 <c>null</c>（不是錯誤，是這台沒有這條列）。
    /// </summary>
    internal static LicenseInfo? ReadLicensing()
    {
        using var s = new ManagementObjectSearcher("root\\CIMV2",
            "SELECT Name, LicenseStatus, Description FROM SoftwareLicensingProduct " +
            "WHERE PartialProductKey IS NOT NULL AND ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f'");
        foreach (ManagementObject o in s.Get())
            using (o)
            {
                string name = o["Name"] as string ?? "";
                if (name.Length == 0) continue;
                int status = o["LicenseStatus"] is { } v ? Convert.ToInt32(v) : -1;
                return new LicenseInfo(name, status, o["Description"] as string ?? "");
            }
        return null;
    }

    /// <summary>WMI 通路：列舉共用。<c>Type</c> 取低 8 位元區分磁碟(0)／印表(1)／裝置(2)／IPC(3)。</summary>
    internal static IReadOnlyList<ShareEntry> ReadShares()
    {
        var list = new List<ShareEntry>();
        using var s = new ManagementObjectSearcher("root\\CIMV2",
            "SELECT Name, Type FROM Win32_Share");
        foreach (ManagementObject o in s.Get())
            using (o)
            {
                string name = o["Name"] as string ?? "";
                if (name.Length == 0) continue;
                list.Add(new ShareEntry(name, ShareTypeOf(o["Type"])));
            }
        return list;
    }

    /// <summary>
    /// WMI <c>Win32_Share.Type</c> 轉成 <see cref="long"/>：這是本機實測抓到的溢位點——
    /// 系統預設共用（<c>C$</c>／<c>ADMIN$</c>）帶特殊位元 <c>0x80000000</c>，以 <c>int</c> 承接會擲
    /// <c>OverflowException</c>，整個 <c>sv.shares</c> 就會變成 <see cref="FactAvailability.ReadError"/>。
    /// </summary>
    internal static long ShareTypeOf(object? raw) => raw is null ? 0 : Convert.ToInt64(raw);

    // ── 小工具 ────────────────────────────────────────────────────────────

    private static (T? Value, string? Error) TryRead<T>(Func<T?> read) where T : class
    {
        try { return (read(), null); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Category, name, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason);
}
