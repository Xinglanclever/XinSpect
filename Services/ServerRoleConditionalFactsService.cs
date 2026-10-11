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
/// Hyper-V 虛擬交換器（SV-009）、IIS 應用程式池（SV-002）、DNS 區域（SV-004）。
/// </summary>
/// <remarks>
/// <para>
/// <b>與第一波的差別：</b>第一波（<see cref="ServerRoleDeepFactsService"/>）挑的是<b>任何 SKU 都讀得到</b>的事實；
/// 這一波是<b>角色條件式</b>——角色缺席時回「不適用」並帶原因，而不是 0 或錯誤。
/// 「沒裝這個角色」是環境事實，不是缺陷（見 CAPABILITY-DOMAINS §來源不可用的三態）。
/// </para>
/// <para>
/// <b>界線：</b>①三個來源各自可注入探針、互不影響；<b>探針回 <c>null</c> 代表「角色不在」→
/// <see cref="FactAvailability.NotApplicable"/>，擲例外才代表讀取失敗 → <see cref="FactAvailability.ReadError"/></b>，
/// 兩者不得混為一談；②IIS 事實讀設定檔（<c>applicationHost.config</c>），只報<b>已定義的池名與管線模式</b>，
/// 不報執行期狀態（設定檔不記載執行期）；③DNS 區域只讀<b>區域名稱</b>，不讀區域資料、不讀 AD 內容。
/// </para>
/// </remarks>
public static class ServerRoleConditionalFactsService
{
    public const string Category = "伺服器角色";
    public const string HyperVSwitchesKey = "sv.hyperv.switches";
    public const string IisAppPoolsKey = "sv.iis.apppools";
    public const string DnsZonesKey = "sv.dns.zones";

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
    /// 收集條件式角色事實。探針回 <c>null</c>＝該角色不在（不適用）；擲例外＝讀取失敗。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<VSwitch>?>? switchesProbe = null,
        Func<IReadOnlyList<AppPool>?>? appPoolsProbe = null,
        Func<IReadOnlyList<string>?>? dnsZonesProbe = null)
    {
        return
        [
            HyperVSwitchesFact(at, switchesProbe ?? ReadHyperVSwitches),
            IisAppPoolsFact(at, appPoolsProbe ?? ReadIisAppPools),
            DnsZonesFact(at, dnsZonesProbe ?? ReadDnsZones),
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
            $"{list.Count} 個虛擬交換器：{detail}。只報交換器名稱與型別，不報其網路設定。",
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
            $"DNS 區域 {zones.Count} 個：{detail}。只讀區域名稱，不讀區域資料、不讀 AD 內容。",
            "個", @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\DNS Server\Zones",
            FactTrustLevel.Reported, false, at, zones.Count);
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

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Category, name, "", "", "伺服器角色條件式讀取",
            FactTrustLevel.Unknown, false, at, null, availability, reason);
}
