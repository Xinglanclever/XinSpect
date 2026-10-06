using System.Management;

namespace XinSpect;

/// <summary>
/// UPS／電池備援事實（R7）：WMI Win32_Battery 的標準屬性（usermode、唯讀）。
/// UPS 透過 USB HID Battery Class 或序列埠接上時會出現在這裡；沒有 UPS／電池時整組 NotApplicable。
/// 廠商私有資料（各品牌事件日誌、逐電池健康）不在 WMI 標準層——如實不提供。
/// </summary>
public static class UpsFactsService
{
    private const string Category = "電源";
    private const string Source = "WMI Win32_Battery（HID Battery Class）";

    /// <summary>一台電池／UPS 的標準屬性快照。</summary>
    public sealed record UpsBatterySnapshot(int? Percent, ushort? StatusCode, int? RuntimeMinutes);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<UpsBatterySnapshot>?>? probe = null)
    {
        var batteries = (probe ?? FetchBatteries)();
        string noDevice = "WMI Win32_Battery 沒有實例——這台機器沒有電池或 UPS（透過 HID Battery Class 回報的裝置）";
        if (batteries is null || batteries.Count == 0)
            return
            [
                new("ups.battery.percent", Category, "UPS 電量", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable, noDevice),
                new("ups.status", Category, "UPS 狀態", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable, noDevice),
                new("ups.runtime_minutes", Category, "UPS 預估續航", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable, noDevice),
            ];

        var b = batteries[0]; // 桌上型最多一台 UPS；多實例時報第一台並註明
        string note = batteries.Count > 1 ? $"（共 {batteries.Count} 台，報第一台）" : "";
        return
        [
            new("ups.battery.percent", Category, "UPS 電量",
                b.Percent is { } p ? $"{p} %" : "—（裝置未回報）", "%", Source + note,
                FactTrustLevel.Measured, false, at, b.Percent),
            new("ups.status", Category, "UPS 狀態",
                b.StatusCode is { } s ? DescribeBatteryStatus(s) : "—（裝置未回報）", "", Source + note,
                FactTrustLevel.Measured, false, at, b.StatusCode),
            new("ups.runtime_minutes", Category, "UPS 預估續航",
                b.RuntimeMinutes is { } r and > 0 and < 0x7FFF ? $"{r} 分鐘" : "—（裝置未回報或市電正常時無估計）", "分鐘",
                Source + note, FactTrustLevel.Measured, false, at, b.RuntimeMinutes),
        ];
    }

    /// <summary>BatteryStatus 代碼（WMI SDK：Win32_Battery.BatteryStatus）。未收錄如實報原始碼。</summary>
    public static string DescribeBatteryStatus(ushort code) => code switch
    {
        1 => "放電中（未接市電）",
        2 => "市電正常（電池未在充放電）",
        3 => "市電正常（已充飽）",
        4 => "電量低（市電正常）",
        5 => "電量危急（市電正常）",
        6 => "充電中",
        7 => "充電中且電量高",
        8 => "充電中且電量低",
        9 => "充電中且電量危急",
        10 => "狀態不明",
        11 => "部分充電",
        _ => $"BatteryStatus 0x{code:X2}（未收錄）",
    };

    private static IReadOnlyList<UpsBatterySnapshot>? FetchBatteries()
    {
        var list = new List<UpsBatterySnapshot>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT EstimatedChargeRemaining, BatteryStatus, EstimatedRunTime FROM Win32_Battery");
            foreach (var obj in searcher.Get())
            {
                list.Add(new UpsBatterySnapshot(
                    Percent: obj["EstimatedChargeRemaining"] is { } p ? Convert.ToInt32(p) : null,
                    StatusCode: obj["BatteryStatus"] is { } s ? Convert.ToUInt16(s) : null,
                    RuntimeMinutes: obj["EstimatedRunTime"] is { } r ? Convert.ToInt32(r) : null));
            }
        }
        catch (Exception)
        {
            return null; // WMI 不可用（權限／服務異常）→ 三態，不是「沒有 UPS」
        }
        return list;
    }
}
