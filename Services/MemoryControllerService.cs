using System.Globalization;
using System.Diagnostics;

namespace XinSpect;

/// <summary>Pure memory-controller projection. No register, mailbox, driver, WMI or process calls.</summary>
/// <remarks>Provider mapping and its cache failure semantics are documented in
/// docs/MEMORY-CONTROLLER-COVERAGE-2026-11-27.md against the bundled 0.9.6 repository commit.</remarks>
public static class MemoryControllerService
{
    public const string LhmSource = "LibreHardwareMonitor 0.9.6 / RyzenSMU PM table";
    public static TimeSpan ApplicationCaptureMaxAge { get; } = TimeSpan.FromSeconds(30);
    public const string LhmCoverage = "MCLK／UCLK／FCLK 來自既有 LHM AMD CPU 的 Memory／Uncore／Fabric 感測器。上游只有特定 PM-table 佈局（001E0004／00240903／00380805／00540004）提供這三欄，不能按世代名稱推定支援。Intel／其他平台未提供這條路徑。本程式快照 30 秒到期；來源快取仍未確認新鮮度。未新增硬體存取或驅動。";
    private static readonly (string Key, string Label, string Sensor)[] ClockFields =
    [ ("mclk", "MCLK（記憶體時脈）", "Memory"),
      ("uclk", "UCLK（控制器時脈）", "Uncore"),
      ("fclk", "FCLK（Fabric 時脈）", "Fabric") ];

    internal static MemoryControllerSnapshot FromLhm(IEnumerable<MemoryControllerClockSample> input, long capturedTimestamp = 0)
    {
        ArgumentNullException.ThrowIfNull(input);
        // The sensor name alone is insufficient: exclude GPU memory and Intel uncore clocks.
        var samples = input.Where(s => ValidCpuId(s.CpuId)
            && ValidClockId(s.CpuId, s.SensorId)
            && ClockFields.Any(f => f.Sensor == s.SensorName)).ToArray();
        var readings = new List<MemoryControllerReading>();
        foreach (var cpu in samples.GroupBy(s => s.CpuId, StringComparer.Ordinal))
            foreach (var field in ClockFields)
            {
                var candidates = cpu.Where(s => s.SensorName == field.Sensor).ToArray();
                var sample = candidates.Length == 1 ? candidates[0] : null;
                bool valid = sample?.ValueMHz is { } value && double.IsFinite(value)
                    && value > 0 && value <= SensorSanity.MaxClockMHz;
                readings.Add(new(field.Key, field.Label, valid ? sample!.ValueMHz : null, "MHz", "",
                    candidates.Length == 0 ? FactAvailability.NotSupported
                        : valid ? FactAvailability.Unknown : FactAvailability.ReadError,
                    FactTrustLevel.Reported, LhmSource, sample?.SensorId ?? field.Sensor,
                    cpu.Key, sample?.CapturedAtUtc, null,
                    candidates.Length == 0 ? "此 CPU 的既有 provider 未提供此感測器。"
                    : candidates.Length > 1 ? "同一 CPU 有重複感測器，拒絕任選一筆。"
                    : valid ? "既有 provider 快取觀測；LHM 讀取 PM table 失敗時可能保留舊值，無逐值更新證據，故不標為已確認即時量測。"
                    : $"此輪感測值缺失、非有限數，或超出既有頻率範圍（0–{SensorSanity.MaxClockMHz} MHz，不含 0）；不沿用本程式上一輪數值。"));
            }
        return new() { Clocks = readings.AsReadOnly(), CapturedTimestamp = capturedTimestamp, CoverageText = LhmCoverage };
    }

    internal static bool ValidCpuId(string value)
        => value.StartsWith("/amdcpu/", StringComparison.Ordinal)
            && int.TryParse(value.AsSpan("/amdcpu/".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && index >= 0;

    private static bool ValidClockId(string cpu, string sensor)
    {
        string prefix = cpu + "/clock/";
        return sensor.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(sensor.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && index >= 0;
    }

    /// <summary>Called before priority resolution so WMI cannot erase the CPU-Z actual-frequency report.</summary>
    public static MemoryControllerSnapshot FromTimings(MemoryTimings? direct, MemoryTimings? wmi, MemoryTimings? report)
    {
        var clocks = new List<MemoryControllerReading>();
        var configured = new List<MemoryControllerReading>();
        // Frequency from WMI is a configured transfer rate projection, not an observed MCLK.
        foreach (var source in new[] { direct, wmi, report }.Where(t => t is not null).Distinct())
        {
            if (source!.IsJedecReference || source.RateIsJedecReference) continue;
            if (source.RateIsConfigured)
            {
                if (Known(source.DataRateText))
                    configured.Add(Reading("configured-rate", "Windows 設定資料速率", null, "MT/s",
                        source.DataRateText, source, rate: true, "Win32_PhysicalMemory.ConfiguredClockSpeed",
                        "Windows 回報設定值；不以此數字除以二填成目前 MCLK。"));
                continue;
            }
            if (double.IsFinite(source.DramFrequencyMHz) && source.DramFrequencyMHz > 0
                && source.DramFrequencyMHz <= SensorSanity.MaxClockMHz)
            {
                clocks.Add(Reading("mclk", "MCLK（記憶體時脈）", source.DramFrequencyMHz, "MHz", "",
                    source, rate: true, "Memory Frequency", "來源回報的目前 DRAM 時脈；來源更新時間未知。"));
            }
        }
        // Find configured rates independently of finding the first actual-frequency source.
        if (configured.Count == 0 && wmi is { RateIsConfigured: true } && Known(wmi.DataRateText))
            configured.Add(Reading("configured-rate", "Windows 設定資料速率", null, "MT/s", wmi.DataRateText,
                wmi, rate: true, "Win32_PhysicalMemory.ConfiguredClockSpeed", "Windows 設定資料速率，不是控制器即時時脈。"));
        if (configured.Count == 0)
            configured.Add(new("configured-rate", "Windows 設定資料速率", null, "MT/s", "",
                wmi?.RateFactAvailability is FactAvailability.ReadError or FactAvailability.InsufficientPrivilege
                    ? wmi.RateFactAvailability : FactAvailability.Unknown, FactTrustLevel.Reported,
                "Windows WMI", "Win32_PhysicalMemory.ConfiguredClockSpeed", "全機", null, null,
                "尚未取得一致且有效的 Windows 設定資料速率；不以 SPD 額定值補入。"));
        var timings = new List<MemoryControllerReading>();
        AddTiming("trfc", "tRFC", t => t.TRFC);
        AddTiming("tccd", "tCCD", t => t.TCCD);
        AddTiming("tccd-l", "tCCD_L", t => t.TCCDL);
        AddTiming("tccd-wr", "tCCD_WR", t => t.TCCDWR);
        AddTiming("tccd-wr-l", "tCCD_WR_L", t => t.TCCDWRL);
        void AddTiming(string key, string label, Func<MemoryTimings, string> get)
        {
            var source = new[] { direct, wmi, report }.FirstOrDefault(t => t is { Loaded: true, IsJedecReference: false } && Known(get(t)));
            if (source is null)
            {
                timings.Add(Missing(key, label, "未取得目前副時序來源；SPD 能力表不補為目前值。"));
                return;
            }
            string raw = get(source);
            bool valid = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double cycles)
                && double.IsFinite(cycles) && cycles > 0 && cycles <= ushort.MaxValue && cycles == Math.Truncate(cycles);
            var reading = Reading(key, label, valid ? cycles : null, "cycles", raw, source, rate: false, label,
                valid ? "目前時序來源回報值，單位為 DRAM clock cycles；沒有套用 SPD 表。"
                    : "原始回報不是可確認的正整數 clock cycles；保留原文但不確認為有效時序。");
            timings.Add(valid ? reading : reading with { Availability = FactAvailability.Unknown });
        }
        return new() { Clocks = clocks.AsReadOnly(), ConfiguredRates = configured.AsReadOnly(), SubTimings = timings.AsReadOnly(), CoverageText = LhmCoverage };
    }

    public static MemoryControllerSnapshot Merge(MemoryControllerSnapshot? live, MemoryControllerSnapshot? timings)
    {
        live ??= MemoryControllerSnapshot.Empty;
        timings ??= MemoryControllerSnapshot.Empty;
        var clocks = live.Clocks.ToList();
        foreach (var field in ClockFields)
        {
            // Unconfirmed live cache cannot displace a confirmed external current observation.
            var fallbacks = timings.Clocks.Where(r => r.Key == field.Key).ToArray();
            if (!clocks.Any(r => r.Key == field.Key))
            {
                if (fallbacks.Length == 0) clocks.Add(Missing(field.Key, field.Label, "既有 provider 未提供可用的目前值；未由設定速率或其他時脈推導。"));
                else clocks.AddRange(fallbacks);
            }
            else clocks.AddRange(fallbacks); // Observations remain visible; scopes are not silently merged.
        }
        return new() { Clocks = clocks.AsReadOnly(), ConfiguredRates = timings.ConfiguredRates, SubTimings = timings.SubTimings, CoverageText = LhmCoverage };
    }

    /// <summary>Expire application captures on the monotonic clock; hardware update time remains unknown.</summary>
    internal static MemoryControllerSnapshot WithFreshness(MemoryControllerSnapshot snapshot, long now, TimeSpan maxAge)
    {
        if (snapshot.CapturedTimestamp == 0) return snapshot;
        if (now >= snapshot.CapturedTimestamp && maxAge > TimeSpan.Zero
            && (double)(now - snapshot.CapturedTimestamp) / Stopwatch.Frequency <= maxAge.TotalSeconds) return snapshot;
        return new() { CapturedTimestamp = snapshot.CapturedTimestamp, CoverageText = snapshot.CoverageText,
            Clocks = Array.AsReadOnly(snapshot.Clocks.Select(r => r with { NumericValue = null, RawText = "", IsExpired = true,
                Availability = FactAvailability.Unknown, Reason = $"本程式最近一次成功輪詢已超過 {maxAge.TotalSeconds:0.#} 秒或單調鐘區間無效；清除數值。來源更新時間仍未知。" }).ToArray()) };
    }

    public static MemoryControllerSnapshot Invalidated(MemoryControllerSnapshot previous)
        => new()
        {
            Clocks = Array.AsReadOnly(previous.Clocks.Select(r => r with { NumericValue = null, RawText = "",
                Availability = FactAvailability.ReadError, Reason = "此輪硬體輪詢失敗或服務已關閉；不沿用先前值。" }).ToArray()),
            CoverageText = LhmCoverage,
        };

    internal static MemoryControllerSnapshot FailedCapture(DateTimeOffset at, string errorType)
        => new()
        {
            Clocks = Array.AsReadOnly(ClockFields.Select(field => new MemoryControllerReading(
                field.Key, field.Label, null, "MHz", "", FactAvailability.ReadError, FactTrustLevel.Reported,
                LhmSource, "IHardware / ISensor metadata 與快取值", "來源身分未能確認", at, null,
                "記憶體控制器觀測支線擷取失敗（" + errorType + "）；不是已確認無硬體。其他感測器仍依原輪詢結果發布。")).ToArray()),
            CoverageText = LhmCoverage,
        };

    private static MemoryControllerReading Reading(string key, string label, double? value, string unit, string text,
        MemoryTimings t, bool rate, string rawField, string reason)
        => new(key, label, value, unit, text, rate ? t.RateFactAvailability : t.TimingFactAvailability,
            (rate ? t.RateFactTrust : t.TimingFactTrust) ?? FactTrustLevel.Unknown,
            Source(rate ? t.RateFactSource : t.TimingFactSource, t.SourceText), rawField, "全機（來源未提供逐控制器範圍）",
            null, null, reason + " " + (rate ? t.RateFactReason : t.TimingFactReason));
    private static string Source(string? specific, string? general) => Known(specific) ? specific! : Known(general) ? general! : "來源未知";
    private static bool Known(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim() is not ("—" or "-" or "N/A");
    private static MemoryControllerReading Missing(string key, string label, string reason)
        => new(key, label, null, "", "", FactAvailability.NotSupported, FactTrustLevel.Unknown,
            "目前控制器來源", label, "未知", null, null, reason);
}
