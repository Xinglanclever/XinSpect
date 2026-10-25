using System.Diagnostics.Eventing.Reader;

namespace XinSpect;

/// <summary>
/// U10-SE-0061（候選鍵 <c>sec.logon.hw.correlate</c>）：裝置插入×登入事件的<b>時間窗交集</b>。
/// 與 <c>sa.logon.summary</c>／<c>sa.logon.deep</c> 的差異在於——既有只數 4624/4625/4740 事件，
/// 裝置插入與登入的時間交集未實作。
///
/// <para>
/// <b>重用既有讀取通路</b>：與 <see cref="SecurityAuditSupplementFactsService"/> 的登入計數
/// （Security log 4624，EventLogQuery／EventLogReader、反向讀、封頂）與
/// <see cref="RecoveryDisplayFactsService"/> 的 Kernel-PnP 事件計數同一條
/// System.Diagnostics.Eventing.Reader 唯讀通路，只取時間戳、不讀事件內容。
/// </para>
///
/// <para>
/// <b>敏感度（涉帳號面）</b>：只做「時間窗交集的存在性與次數」——
/// 輸出只有次數與時間窗長度，<b>不輸出帳號名、SID、來源 IP 或時間點原值</b>
/// （沿倉庫 L2/L3 遮蔽慣例）。交集不構成因果陳述：裝置在登入後被配置是常態，不是警訊。
/// </para>
/// </summary>
public static class LogonHardwareCorrelationFactsService
{
    public const string LogonHwCorrelateKey = "sec.logon.hw.correlate";

    /// <summary>時間窗：裝置事件落在某次登入後多少分鐘內算「交集」。刻意保守取寬窗，避免暗示偵測能力。</summary>
    public const int WindowMinutes = 10;

    /// <summary>逐 log 讀取封頂，與既有登入計數通路同一量級。</summary>
    public const int MaxEvents = 1000;

    private const string Category = "安全性";
    private const string LastDays = "7";

    /// <summary>交集統計。CorrelatedArrivals＝裝置事件中、落在任一登入後時間窗內的次數。</summary>
    public sealed record CorrelationState(int LogonEvents, int DeviceArrivalEvents, int CorrelatedArrivals);

    public static HardwareFact Collect(DateTimeOffset at,
        Func<IReadOnlyList<DateTimeOffset>?>? logonProbe = null,
        Func<IReadOnlyList<DateTimeOffset>?>? deviceProbe = null)
    {
        IReadOnlyList<DateTimeOffset>? logons, devices;
        try { logons = logonProbe is null ? FetchLogonTimes() : logonProbe(); } catch { logons = null; }
        try { devices = deviceProbe is null ? FetchDeviceArrivalTimes() : deviceProbe(); } catch { devices = null; }

        if (logons is null || devices is null)
        {
            string which = logons is null && devices is null ? "Security log 與 Kernel-PnP/Configuration log 都"
                : logons is null ? "Security log（登入事件）" : "Kernel-PnP/Configuration log（裝置事件）";
            return new HardwareFact(LogonHwCorrelateKey, Category, "登入×裝置插入時間交集", "", "",
                "Security 4624×Kernel-PnP 400/410（只讀時間戳）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, $"{which}讀不到——不猜交集");
        }

        var s = Correlate(logons, devices);
        string detail =
            $"近 {LastDays} 天登入事件 {s.LogonEvents} 次、裝置插入／配置事件 {s.DeviceArrivalEvents} 次，" +
            $"其中 {s.CorrelatedArrivals} 次落在某次登入後 {WindowMinutes} 分鐘內。" +
            "只做時間窗交集的存在性與次數；不輸出帳號名或 SID。交集不是因果陳述。";
        return new HardwareFact(LogonHwCorrelateKey, Category, "登入×裝置插入時間交集", detail, "次",
            "Security 4624×Kernel-PnP 400/410（只讀時間戳）", FactTrustLevel.Reported, false, at,
            s.CorrelatedArrivals);
    }

    /// <summary>純交集：裝置事件時間落在任一登入後 window 內的次數。不讀任何硬體。</summary>
    public static CorrelationState Correlate(IReadOnlyList<DateTimeOffset> logonTimes,
        IReadOnlyList<DateTimeOffset> deviceArrivalTimes, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(logonTimes);
        ArgumentNullException.ThrowIfNull(deviceArrivalTimes);
        var w = window ?? TimeSpan.FromMinutes(WindowMinutes);
        int correlated = 0;
        foreach (var d in deviceArrivalTimes)
            if (logonTimes.Any(l => l <= d && d - l <= w)) correlated++;
        return new(logonTimes.Count, deviceArrivalTimes.Count, correlated);
    }

    /// <summary>登入事件通路（極薄）：Security log 4624 的時間戳；讀不到回 null 標三態。與既有登入計數同一條通路。</summary>
    internal static IReadOnlyList<DateTimeOffset>? FetchLogonTimes()
    {
        try
        {
            var q = new EventLogQuery("Security", PathType.LogName, "*[System[(EventID=4624)]]") { ReverseDirection = true };
            using var r = new EventLogReader(q);
            var list = new List<DateTimeOffset>();
            for (int i = 0; i < MaxEvents; i++)
            {
                using var e = r.ReadEvent(); if (e is null) break;
                if (e.TimeCreated is { } t) list.Add(new DateTimeOffset(t, TimeSpan.Zero));
            }
            return list;
        }
        catch { return null; }
    }

    /// <summary>裝置事件通路（極薄）：Kernel-PnP/Configuration log 400（配置）／410（啟動）的時間戳；讀不到回 null 標三態。</summary>
    internal static IReadOnlyList<DateTimeOffset>? FetchDeviceArrivalTimes()
    {
        try
        {
            var q = new EventLogQuery("Microsoft-Windows-Kernel-PnP/Configuration", PathType.LogName,
                "*[System[(EventID=400 or EventID=410)]]") { ReverseDirection = true };
            using var r = new EventLogReader(q);
            var list = new List<DateTimeOffset>();
            for (int i = 0; i < MaxEvents; i++)
            {
                using var e = r.ReadEvent(); if (e is null) break;
                if (e.TimeCreated is { } t) list.Add(new DateTimeOffset(t, TimeSpan.Zero));
            }
            return list;
        }
        catch { return null; }
    }
}
