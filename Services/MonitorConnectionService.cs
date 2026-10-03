using System.Management;

namespace XinSpect;

/// <summary>
/// WP10 顯示面：螢幕連接介面（WMI root\wmi WmiMonitorConnectionParams，usermode）。
/// 「這台螢幕是走 DP 還是 HDMI」是週邊診斷的第一件事——WMI 有答就直接講，讀不到三態。
/// </summary>
public static class MonitorConnectionService
{
    private const string Category = "週邊匯流排";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<MonitorConnection>?>? probe = null)
    {
        var monitors = (probe ?? FetchWmi)();
        if (monitors is null)
            return new[] { "mon.count", "mon.dp", "mon.0" }.Select(key =>
                new HardwareFact(key, Category, NameOf(key), "", "",
                    "WMI root\\wmi WmiMonitorConnectionParams（螢幕連接介面）",
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    "WMI 查詢失敗——螢幕連接介面讀不到就是不猜")).ToList();

        var facts = new List<HardwareFact>
        {
            new("mon.count", Category, "偵測到的顯示器", monitors.Count.ToString(), "台",
                "WMI root\\wmi WmiMonitorConnectionParams", FactTrustLevel.Reported, false, at, monitors.Count),
        };
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            facts.Add(new HardwareFact($"mon.{i}", Category, $"顯示器 {i}（{m.InstanceName}）",
                MonitorConnectionDecoder.DescribeVideoOutput(m.VideoOutputTechnology), "",
                "WMI root\\wmi WmiMonitorConnectionParams", FactTrustLevel.Reported, false, at, null));
        }
        facts.Add(new HardwareFact("mon.dp", Category, "DisplayPort 連接的顯示器",
            monitors.Count(m => unchecked((uint)m.VideoOutputTechnology & 0x7FFF_FFFF) is 0x0A or 0x0B).ToString(),
            "台", "WMI root\\wmi WmiMonitorConnectionParams", FactTrustLevel.Derived, false, at,
            monitors.Count(m => unchecked((uint)m.VideoOutputTechnology & 0x7FFF_FFFF) is 0x0A or 0x0B)));
        return facts;
    }

    private static string NameOf(string key) => key switch
    {
        "mon.count" => "偵測到的顯示器",
        "mon.dp" => "DisplayPort 連接的顯示器",
        _ => "顯示器",
    };

    /// <summary>WMI 通路（極薄）：失敗回 null。</summary>
    public static IReadOnlyList<MonitorConnection>? FetchWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams");
            var monitors = new List<MonitorConnection>();
            foreach (var m in searcher.Get())
            {
                if (m["VideoOutputTechnology"] is not null)
                    monitors.Add(new MonitorConnection(m["InstanceName"]?.ToString() ?? "",
                        Convert.ToInt32(m["VideoOutputTechnology"])));
            }
            return monitors;
        }
        catch { return null; }
    }
}
