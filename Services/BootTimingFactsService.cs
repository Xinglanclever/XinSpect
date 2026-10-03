using System.Diagnostics.Eventing.Reader;
using System.Management;

namespace XinSpect;

/// <summary>
/// WP26 開機計時（usermode）：開機耗時取自 Diagnostics-Performance 事件記錄 Event 100 的
/// BootTime 屬性（毫秒）——Windows 開機時自己量的，本工具只解讀、**不評級**（快慢是使用者的判斷）；
/// 最近開機時間戳取 Win32_OperatingSystem.LastBootUpTime。事件記錄被清＝NotSupported，
/// 查詢失敗＝ReadError——兩種「沒有」分得清楚。
/// </summary>
public static class BootTimingFactsService
{
    private const string Category = "系統與軟體";
    private const string Source = "事件記錄 Diagnostics-Performance Event 100（BootTime）＋Win32_OperatingSystem";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<uint?>? bootEventProbe = null, Func<DateTimeOffset?>? lastBootProbe = null)
    {
        uint? durationMs;
        bool eventProbeFailed = false;
        try { durationMs = (bootEventProbe ?? FetchLastBootDuration)(); }
        catch { durationMs = null; eventProbeFailed = true; }
        var durationFact = durationMs is { } ms
            ? new HardwareFact("boot.duration_ms", Category, "開機耗時",
                $"{ms / 1000.0:F1} 秒（{ms} ms）", "ms", Source,
                FactTrustLevel.Reported, false, at, ms)
            : Unavailable(at, "boot.duration_ms", "開機耗時",
                eventProbeFailed ? "事件記錄查詢失敗" : "沒有開機事件（記錄可能被清理，或自安裝後尚無完整開機）",
                eventProbeFailed ? FactAvailability.ReadError : FactAvailability.NotSupported);

        DateTimeOffset? lastBoot;
        bool lastBootFailed = false;
        try { lastBoot = (lastBootProbe ?? FetchLastBootTime)(); }
        catch { lastBoot = null; lastBootFailed = true; }
        var lastFact = lastBoot is { } t
            ? new HardwareFact("boot.last_time", Category, "最近一次開機", $"{t:yyyy-MM-dd HH:mm:ss} UTC", "",
                "WMI Win32_OperatingSystem（LastBootUpTime）", FactTrustLevel.Reported, false, at, null)
            : Unavailable(at, "boot.last_time", "最近一次開機",
                lastBootFailed ? "WMI 查詢失敗" : "WMI 沒有回報 LastBootUpTime", lastBootFailed ? FactAvailability.ReadError : FactAvailability.NotSupported);

        return [durationFact, lastFact];
    }

    /// <summary>事件記錄通路（極薄）：最近一次 Event 100 的 BootTime（ms）。記錄不存在回 null、查詢失敗標記後回 null。</summary>
    public static uint? FetchLastBootDuration()
    {
        try
        {
            var query = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName,
                "*[System[EventID=100]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            if (reader.ReadEvent() is not { } evt)
                return null;
            using (evt)
            {
                object? raw = evt.Properties.Count > 19 ? evt.Properties[19].Value : null;
                return raw is null ? null : Convert.ToUInt32(raw);
            }
        }
        catch { return null; }
    }

    private static DateTimeOffset? FetchLastBootTime()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT LastBootUpTime FROM Win32_OperatingSystem");
            foreach (var m in searcher.Get())
                return ManagementDateTimeConverter.ToDateTime(m["LastBootUpTime"].ToString()!);
            return null;
        }
        catch { return null; }
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name, string reason, FactAvailability availability) =>
        new(key, Category, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
