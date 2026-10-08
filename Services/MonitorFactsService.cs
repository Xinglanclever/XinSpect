using System;
using System.Collections.Generic;
using System.Management;

namespace XinSpect;

/// <summary>
/// 顯示器真偽判讀的<b>讀取</b>層：把 WMI 的三份資料湊成 <see cref="MonitorSample"/>。
/// </summary>
/// <remarks>
/// <para>
/// 三個來源，全部唯讀、usermode 零特權：
/// ① <c>WmiMonitorID</c>——EDID 解析結果（製造商三字母碼、產品名稱、製造年週）；
/// ② <c>WmiMonitorConnectionParams</c>——連接介面；
/// ③ <c>Win32_DesktopMonitor</c>——實例清單（含沒有 EDID 的那些預設物件）。
/// </para>
/// <para>
/// <b>為什麼要合併三份：</b>只看 <c>WmiMonitorID</c> 會漏掉「作業系統的預設監視器物件」
/// （它們不在 EDID 清單裡），於是「接了 1 台螢幕」與「接了 3 台螢幕但只有 1 台有 EDID」
/// 在這一份資料上長得一樣。以 <c>Win32_DesktopMonitor</c> 的實例清單為主，
/// 逐筆去對 EDID 與連接介面，才分得出差別。
/// </para>
/// </remarks>
public static class MonitorFactsService
{
    private const string Category = "顯示";

    /// <summary>收集顯示器事實。測試以注入樣本取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<MonitorSample>>? probe = null)
    {
        IReadOnlyList<MonitorSample> monitors;
        try { monitors = (probe ?? ReadAll)(); }
        catch (Exception ex)
        {
            return
            [
                new HardwareFact("monitor.summary", Category, "顯示器組成", "", "",
                    "WMI WmiMonitorID ＋ WmiMonitorConnectionParams ＋ Win32_DesktopMonitor",
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    "讀取失敗：" + ex.Message),
            ];
        }

        if (monitors.Count == 0)
            return
            [
                new HardwareFact("monitor.summary", Category, "顯示器組成", "", "",
                    "WMI WmiMonitorID ＋ Win32_DesktopMonitor", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError,
                    "沒有回報任何顯示器實例——讀不到就是不猜（可能是 WMI 未回應）"),
            ];

        var list = new List<HardwareFact>
        {
            new("monitor.summary", Category, "顯示器組成",
                MonitorJudge.Summarize(monitors), "",
                "WMI WmiMonitorID（EDID）＋ WmiMonitorConnectionParams ＋ Win32_DesktopMonitor",
                FactTrustLevel.Derived, false, at, null, FactAvailability.Present),
        };

        foreach (var m in monitors)
        {
            var v = MonitorJudge.Judge(m);
            string conn = m.VideoOutputTechnology == int.MinValue
                ? "連接介面讀不到"
                : MonitorConnectionDecoder.DescribeVideoOutput(m.VideoOutputTechnology);
            list.Add(new HardwareFact($"monitor.{m.InstanceName}", Category, m.InstanceName,
                v.Headline, "", $"WMI（{conn}）", FactTrustLevel.Measured, false, at, null,
                v.Kind == MonitorJudge.MonitorKind.Unknown
                    ? FactAvailability.ReadError : FactAvailability.Present,
                v.Kind == MonitorJudge.MonitorKind.Unknown ? v.Evidence : null));
        }

        return list;
    }

    /// <summary>WMI 通路：合併三份資料。個別查詢失敗不讓整批失敗，缺的欄位以空值表達。</summary>
    internal static IReadOnlyList<MonitorSample> ReadAll()
    {
        // ① EDID 解析結果：InstanceName → (製造商, 名稱, 年, 週)
        var edid = new Dictionary<string, (string Mfg, string Name, int Year, int Week)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher("root\\wmi",
                "SELECT InstanceName, ManufacturerName, UserFriendlyName, YearOfManufacture, WeekOfManufacture "
                + "FROM WmiMonitorID");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string id = o["InstanceName"] as string ?? "";
                    if (id.Length == 0) continue;
                    edid[id] = (EdidString(o["ManufacturerName"]), EdidString(o["UserFriendlyName"]),
                                Int(o, "YearOfManufacture"), Int(o, "WeekOfManufacture"));
                }
        }
        catch (Exception ex)
        {
            Diag.Swallow("EDID 識別資料查詢", ex, "顯示器識別資料讀不到，判讀改以 PNP 路徑為準");
        }

        // ② 連接介面
        var conn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher("root\\wmi",
                "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string id = o["InstanceName"] as string ?? "";
                    if (id.Length == 0) continue;
                    conn[id] = Int(o, "VideoOutputTechnology");
                }
        }
        catch (Exception ex)
        {
            Diag.Swallow("顯示器連接介面查詢", ex, "連接介面讀不到，如實標示");
        }

        // ③ 實例清單（含沒有 EDID 的預設物件）——以這份為主
        var result = new List<MonitorSample>();
        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name, DeviceID, PNPDeviceID FROM Win32_DesktopMonitor");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string pnp = o["PNPDeviceID"] as string ?? "";
                    string deviceId = o["DeviceID"] as string ?? "";
                    // WmiMonitorID 的 InstanceName 帶 _0 後綴，Win32_DesktopMonitor 的 PNPDeviceID 不帶
                    var key = edid.Keys.FirstOrDefault(k =>
                        k.StartsWith(pnp, StringComparison.OrdinalIgnoreCase)
                        || (pnp.Length > 0 && pnp.StartsWith(k.TrimEnd('0', '_'), StringComparison.OrdinalIgnoreCase)));
                    bool hasEdid = key is not null;

                    result.Add(new MonitorSample(
                        deviceId.Length > 0 ? deviceId : pnp,
                        hasEdid ? edid[key!].Mfg : "",
                        hasEdid ? edid[key!].Name : "",
                        hasEdid ? edid[key!].Year : 0,
                        hasEdid ? edid[key!].Week : 0,
                        hasEdid,
                        pnp.Length > 0 && conn.TryGetValue(pnp + "_0", out int t) ? t : int.MinValue,
                        pnp.StartsWith("DISPLAY\\", StringComparison.OrdinalIgnoreCase)));
                }
        }
        catch (Exception ex)
        {
            Diag.Swallow("顯示器實例查詢", ex, "顯示器清單讀不到");
            return [];
        }

        result.Sort((a, b) => string.CompareOrdinal(a.InstanceName, b.InstanceName));
        return result;
    }

    /// <summary>
    /// WMI 把 EDID 字串回成 <c>ushort[]</c>（每個字元一個元素、後面補 0）。
    /// 直接 <c>ToString()</c> 會得到「System.UInt16[]」——必須逐元素轉字元並去掉補零。
    /// </summary>
    internal static string EdidString(object? value)
    {
        if (value is not Array arr) return "";
        var chars = new List<char>();
        foreach (object? item in arr)
        {
            if (item is null) continue;
            ushort c = Convert.ToUInt16(item);
            if (c == 0) break;
            chars.Add((char)c);
        }
        return new string(chars.ToArray()).Trim();
    }

    private static int Int(ManagementObject o, string prop)
        => o[prop] is { } v ? Convert.ToInt32(v) : 0;
}
