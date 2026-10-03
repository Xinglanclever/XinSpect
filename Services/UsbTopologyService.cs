using System.Management;

namespace XinSpect;

/// <summary>一條 USB 控制器→裝置相依（WMI Win32_USBControllerDevice 的引用對，展開成 instance path）。</summary>
public sealed record UsbEdge(string ControllerId, string DeviceId);

/// <summary>
/// WP10 周邊匯流排：USB 拓撲摘要（WMI Win32_USBControllerDevice，usermode 零特權）。
/// 摘要三件事：控制器數、裝置數、最忙碌控制器的掛載數——「誰掛得最多」是排查供電與頻寬
/// 衝突的實用指紋。WMI 的相依對只有一層（controller→device），更深層級的 hub 樹不猜。
/// </summary>
public static class UsbTopologyService
{
    private const string Category = "週邊匯流排";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<UsbEdge>?>? probe = null)
    {
        var edges = (probe ?? FetchWmi)();
        if (edges is null)
            return new[] { "usb.controllers", "usb.devices", "usb.busiest_controller" }.Select(key =>
                new HardwareFact(key, Category, NameOf(key), "", "",
                    "WMI Win32_USBControllerDevice（USB 拓撲相依對）",
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    "WMI 查詢失敗——USB 拓撲讀不到就是不猜")).ToList();

        var byController = edges.GroupBy(e => e.ControllerId, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ToList();

        var facts = new List<HardwareFact>
        {
            new("usb.controllers", Category, "USB 控制器", edges.Select(e => e.ControllerId).Distinct().Count().ToString(),
                "個", "WMI Win32_USBControllerDevice（USB 拓撲相依對）", FactTrustLevel.Reported, false, at,
                (double)edges.Select(e => e.ControllerId).Distinct().Count()),
            new("usb.devices", Category, "USB 裝置", edges.Count.ToString(), "個",
                "WMI Win32_USBControllerDevice（USB 拓撲相依對）", FactTrustLevel.Reported, false, at, (double)edges.Count),
        };
        if (byController.Count > 0)
        {
            var busiest = byController[0];
            int busiestCount = busiest.Count();
            facts.Add(new HardwareFact("usb.busiest_controller", Category, "掛載最多的控制器",
                $"{busiestCount} 個裝置（{busiest.Key}）", "",
                "WMI Win32_USBControllerDevice（USB 拓撲相依對）", FactTrustLevel.Derived, false, at, (double)busiestCount));
        }
        return facts;
    }

    private static string NameOf(string key) => key switch
    {
        "usb.controllers" => "USB 控制器",
        "usb.devices" => "USB 裝置",
        _ => "掛載最多的控制器",
    };

    /// <summary>WMI 通路（極薄）：相依對展開成 instance path；失敗回 null。</summary>
    public static IReadOnlyList<UsbEdge>? FetchWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Antecedent, Dependent FROM Win32_USBControllerDevice");
            var edges = new List<UsbEdge>();
            foreach (var m in searcher.Get())
            {
                string? controller = ExtractDeviceId(m["Antecedent"]?.ToString());
                string? device = ExtractDeviceId(m["Dependent"]?.ToString());
                if (controller is not null && device is not null)
                    edges.Add(new UsbEdge(controller, device));
            }
            return edges;
        }
        catch { return null; }
    }

    /// <summary>從 WMI 引用字串（「\\.\root\...:Win32_USBHub.DeviceID="..."」）抽出 DeviceID。</summary>
    public static string? ExtractDeviceId(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        int start = reference.IndexOf('"');
        int end = reference.LastIndexOf('"');
        return start >= 0 && end > start ? reference[(start + 1)..end] : null;
    }
}
