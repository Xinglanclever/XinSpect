using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace XinSpect;

/// <summary>
/// 已安裝角色與功能的<b>讀取</b>層：列舉選用功能、標出伺服器角色類、並查這些角色帶起來的服務狀態。
/// </summary>
/// <remarks>
/// <para>
/// 兩條路：<c>Win32_OptionalFeature</c>（所有 SKU 都有，回 InstallState）與
/// 伺服器 SKU 上的 <c>Get-WindowsFeature</c>（更完整，但要走 PowerShell）。
/// 本層走前者——零特權、無外部程序；後者只在需要時才由使用者自行執行。
/// </para>
/// <para>
/// 服務狀態用 <c>Win32_Service</c> 的 <c>Name</c> 比對角色的名稱字根，
/// 比對不到就回 -1（未查）而不是 0——「沒查到服務」與「服務數量是零」是兩件事。
/// </para>
/// </remarks>
public static class RoleSurfaceFactsService
{
    private const string Category = "系統與軟體";

    /// <summary>收集角色事實。測試以注入清單取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<FeatureEntry>>? probe = null)
    {
        IReadOnlyList<FeatureEntry> features;
        try { features = (probe ?? ReadAll)(); }
        catch (Exception ex)
        {
            return
            [
                new HardwareFact("role.surface", Category, "已安裝角色", "", "",
                    "WMI Win32_OptionalFeature", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError, "讀取失敗：" + ex.Message),
            ];
        }

        var v = RoleSurfaceJudge.Judge(features);
        var list = new List<HardwareFact>
        {
            new("role.surface", Category, "已安裝角色", v.Headline, "",
                "WMI Win32_OptionalFeature ＋ Win32_Service", FactTrustLevel.Reported, false, at,
                v.InstalledRoles.Count, FactAvailability.Present),
            new("role.surface.evidence", Category, "已安裝角色依據", v.Evidence, "",
                "WMI Win32_OptionalFeature", FactTrustLevel.Derived, false, at, null,
                FactAvailability.Present),
        };

        foreach (var f in features.Where(x => x.Installed))
        {
            string services = f.ServicesRunning < 0 && f.ServicesStopped < 0
                ? "服務狀態未查"
                : $"服務執行中 {Math.Max(0, f.ServicesRunning)}、已安裝未執行 {Math.Max(0, f.ServicesStopped)}";
            list.Add(new HardwareFact($"role.installed.{f.Name}", Category, f.DisplayName,
                RoleSurfaceJudge.IsServerRoleName(f.Name) ? $"已安裝（伺服器角色）；{services}" : $"已安裝；{services}",
                "", $"WMI Win32_OptionalFeature（{f.Name}）", FactTrustLevel.Reported, false, at, null));
        }

        return list;
    }

    /// <summary>WMI 通路：列舉功能並補上服務統計。</summary>
    internal static IReadOnlyList<FeatureEntry> ReadAll()
    {
        var result = new List<FeatureEntry>();

        // 服務狀態先查一次，避免每個功能各查一遍
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stopped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool servicesQueried = false;
        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name, State FROM Win32_Service");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string name = o["Name"] as string ?? "";
                    if (name.Length == 0) continue;
                    string state = o["State"] as string ?? "";
                    if (state.Equals("Running", StringComparison.OrdinalIgnoreCase)) running.Add(name);
                    else stopped.Add(name);
                }
            servicesQueried = true;
        }
        catch (Exception ex) { Diag.Swallow("服務清單查詢", ex, "服務狀態未查，角色只報安裝狀態"); }

        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name, InstallState FROM Win32_OptionalFeature");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string name = o["Name"] as string ?? "";
                    if (name.Length == 0) continue;
                    uint state = o["InstallState"] is { } v ? Convert.ToUInt32(v) : 3;
                    bool installed = state == 1;

                    int run = -1, stop = -1;
                    if (servicesQueried)
                    {
                        string stem = ServiceStem(name);
                        run = running.Count(svc => svc.StartsWith(stem, StringComparison.OrdinalIgnoreCase));
                        stop = stopped.Count(svc => svc.StartsWith(stem, StringComparison.OrdinalIgnoreCase));
                    }

                    result.Add(new FeatureEntry(name, name, installed,
                                                RoleSurfaceJudge.IsServerRoleName(name), run, stop));
                }
        }
        catch (Exception ex)
        {
            Diag.Swallow("選用功能列舉", ex, "功能清單讀不到");
            return [];
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>
    /// 功能名稱 → 服務名稱字根。用於把角色與它帶起來的服務對起來。
    /// 對不上時回一個不會匹配到任何服務的字串——寧可回 0 也不要亂配。
    /// </summary>
    internal static string ServiceStem(string featureName) => featureName switch
    {
        "Hyper-V" => "vm",
        "Failover-Clustering" => "clussvc",
        "FS-FileServer" => "LanmanServer",
        "FS-Data-Deduplication" => "Dedup",
        "FS-iSCSITarget-Server" => "WinTarget",
        "Multipath-IO" => "MSiSCSI",
        "Windows-Server-Backup" => "SDRSVC",
        "DNS" => "DNS",
        "DHCP" => "DHCPServer",
        "AD-Domain-Services" => "NTDS",
        "Print-Services" => "Spooler",
        "Web-Server" => "W3SVC",
        "Containers" => "hns",
        "Storage-Replica" => "Replicator",
        "UpdateServices" => "WsusService",
        _ => "\u0000no-match",
    };
}
