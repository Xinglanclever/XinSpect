using System.Management;

namespace XinSpect;

/// <summary>一列 Thunderbolt / USB4 控制器資訊。</summary>
public sealed record TbUsb4Row(string DeviceName, string Protocol, string DriverVersion, string DeviceId);

/// <summary>
/// Thunderbolt / USB4 埠能力：WMI 列舉 <c>Win32_PnPEntity</c> 中裝置 ID 含 Thunderbolt 或 USB4 的控制器。
/// 零特權（不需系統管理員即可列舉 PnP 裝置）；埠級速率需要系統管理員——如實標示，不猜。
/// </summary>
public static class TbUsb4Service
{
    public static List<TbUsb4Row> Read()
    {
        var rows = new List<TbUsb4Row>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, Manufacturer FROM Win32_PnPEntity");
            foreach (var mo in searcher.Get().Cast<ManagementObject>())
            {
                string id = mo["DeviceID"]?.ToString() ?? "";
                string name = mo["Name"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(id)) continue;

                // Thunderbolt controller 的 PNPDeviceID 通常以 PCI\ 或 ACPI\ 開頭，含 "Thunderbolt" 或 "TBS"
                bool isTb = id.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase);
                // USB4 host router 的裝置 ID 通常含 "USB4"
                bool isUsb4 = id.Contains("USB4", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("USB4", StringComparison.OrdinalIgnoreCase);

                if (!isTb && !isUsb4) continue;
                string protocol = isTb ? "Thunderbolt" : "USB4";
                string drv = mo["Manufacturer"]?.ToString() ?? "";
                rows.Add(new TbUsb4Row(name, protocol, drv, id));
            }
        }
        catch { /* WMI 不可用——回空列 */ }
        return rows;
    }
}
