using System.Management;

namespace XinSpect;

/// <summary>一列藍牙裝置：名稱、狀態、（可選）電量百分比。</summary>
public sealed class BluetoothDeviceRow
{
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public int? BatteryPct { get; init; }
    public string BatteryText => BatteryPct is int b ? $"{b}%" : "—";
}

/// <summary>
/// 藍牙外設電量與裝置樹：
/// 1) 列舉 <c>Win32_PnPEntity</c> 中 PNPClass＝Bluetooth 的裝置；
/// 2) 嘗試從 <c>root\wmi\BatteryStatus</c> 讀到對應的電量（Windows 10 1809+ 已把 GATT Battery
///    Service 映射到 WMI）。全部 best-effort：讀不到電量就以 — 呈現，不猜。
/// 純 WMI 查詢、零特權、不啟動任何子行程。
/// </summary>
public static class BluetoothBatteryService
{
    public static List<BluetoothDeviceRow> Read()
    {
        var rows = new List<BluetoothDeviceRow>();
        try
        {
            // 藍牙裝置列舉
            var btDevices = new List<(string Name, string Status, string Id)>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name, Status, PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'"))
            {
                foreach (var mo in searcher.Get().Cast<ManagementObject>())
                {
                    string name = mo["Name"]?.ToString() ?? "";
                    string status = mo["Status"]?.ToString() ?? "";
                    string id = mo["PNPDeviceID"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    btDevices.Add((name, status, id));
                }
            }

            // 電量（GATT Battery → WMI 映射）
            var batteryByDevice = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var bs = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, EstimatedChargeCapacity FROM BatteryStatus");
                foreach (var mo in bs.Get().Cast<ManagementObject>())
                {
                    string inst = mo["InstanceName"]?.ToString() ?? "";
                    if (mo["EstimatedChargeCapacity"] is uint cap)
                        batteryByDevice[inst] = (int)cap;
                }
            }
            catch { /* BatteryStatus 類別不存在或讀取失敗——不影響裝置列舉 */ }

            foreach (var d in btDevices)
            {
                int? pct = null;
                // 嘗試以裝置 ID 或名稱模糊比對 BatteryStatus 的 InstanceName
                foreach (var kv in batteryByDevice)
                {
                    // InstanceName 通常含裝置名或 BTHENUM\ 裝置 ID 子字串
                    if (kv.Key.Contains(d.Name, StringComparison.OrdinalIgnoreCase)
                        || (d.Id.Length > 8 && kv.Key.Contains(d.Id[8..], StringComparison.OrdinalIgnoreCase)))
                    { pct = kv.Value; break; }
                }
                rows.Add(new BluetoothDeviceRow { Name = d.Name, Status = d.Status, DeviceId = d.Id, BatteryPct = pct });
            }
        }
        catch { /* WMI 不可用——回空列，呼叫端判斷 */ }
        return rows;
    }
}
