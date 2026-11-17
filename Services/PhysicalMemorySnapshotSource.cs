using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security;

namespace XinSpect;

// A raw row is valid only during its enumerator's current iteration; it is never published.
internal sealed record PhysicalMemoryRawRow(Func<string, object?> Read);

internal sealed record PhysicalMemorySnapshot(
    PlatformGovernanceFactsService.MixMatchState LegacySummary,
    IReadOnlyList<MemoryModuleInfo> Modules, bool Complete,
    FactAvailability Availability, string? Reason);

/// <summary>One existing physical-memory enumeration shared by nominal mixmatch and configured-rate evidence.</summary>
internal static class PhysicalMemorySnapshotSource
{
    internal const int MaxRows = 4096;
    internal const string Query = "SELECT Manufacturer, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Capacity FROM Win32_PhysicalMemory";
    internal const string Source = "Windows 設定速率（WMI）";
    private static int _inFlight;

    internal static PhysicalMemorySnapshot ReadForThisMachine()
        => ReadBounded(ReadNativeRows, WmiGuard.DefaultTimeoutMs);

    // Single-flight is retained until the source actually exits, including timeout cleanup.
    internal static PhysicalMemorySnapshot ReadBounded(Func<IEnumerable<PhysicalMemoryRawRow>> rows,
        int timeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return Failure(FactAvailability.ReadError, "上一批 WMI 實體記憶體查詢尚未完成；未重發查詢。");
        long started = Stopwatch.GetTimestamp();
        try
        {
            var result = WmiGuard.Probe(() =>
            {
                try { return ReadRows(rows); }
                finally { Volatile.Write(ref _inFlight, 0); }
            }, timeoutMilliseconds);
            bool expired = result.TimedOut || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= timeoutMilliseconds;
            return !expired && result.Value is not null ? result.Value : Failure(FactAvailability.ReadError,
                expired ? "WMI 實體記憶體查詢逾時；保留列不代表完整批次，來源仍自行完成與清理。"
                    : "WMI 實體記憶體查詢失敗；沒有完整設定速率批次。");
        }
        catch (Exception error)
        {
            // Probe only throws before its worker is scheduled; source exceptions are returned as failures.
            Volatile.Write(ref _inFlight, 0);
            return Failure(Availability(error), "WMI 實體記憶體查詢無法啟動（" + error.GetType().Name + "）。");
        }
    }

    internal static PhysicalMemorySnapshot ReadRows(Func<IEnumerable<PhysicalMemoryRawRow>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var manufacturers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var speeds = new HashSet<int>();
        var modules = new List<MemoryModuleInfo>();
        int count = 0;
        bool complete = true;
        FactAvailability state = FactAvailability.Present;
        string? reason = null;
        void Fail(Exception error)
        {
            complete = false;
            state = FactStateLattice.Weakest(state, Availability(error));
            reason = "WMI 實體記憶體批次不完整（" + error.GetType().Name + "）；未以部分列宣稱全機設定速率。";
        }
        try
        {
            foreach (var row in rows())
            {
                if (count == MaxRows) throw new InvalidDataException("row cap");
                // Preserve the old summary's update order, nominal field and partial counters.
                count++;
                if (row.Read("Manufacturer") is string m && !string.IsNullOrWhiteSpace(m)) manufacturers.Add(m);
                var nominal = row.Read("Speed");
                if (nominal is not null) speeds.Add(Convert.ToInt32(nominal, CultureInfo.InvariantCulture));
                try
                {
                    var configuredRaw = row.Read("ConfiguredClockSpeed");
                    var typeRaw = row.Read("SMBIOSMemoryType");
                    var capacityRaw = row.Read("Capacity");
                    modules.Add(new MemoryModuleInfo
                    {
                        ConfiguredSpeedMHz = configuredRaw is null ? 0 : Convert.ToInt32(configuredRaw, CultureInfo.InvariantCulture),
                        MemoryType = SystemInfoService.MemType(typeRaw is null ? 0 : Convert.ToInt32(typeRaw, CultureInfo.InvariantCulture)),
                        CapacityGB = capacityRaw is null ? 0 : Convert.ToDouble(capacityRaw, CultureInfo.InvariantCulture) / 1073741824.0,
                    });
                }
                catch (Exception error)
                {
                    // New field failures cannot stop or corrupt the pre-existing nominal summary.
                    Fail(error);
                }
            }
        }
        catch (Exception error) { Fail(error); }
        int spread = speeds.Count > 1 ? speeds.Max() - speeds.Min() : 0;
        return new(new(count, manufacturers.Count, 0, spread), Array.AsReadOnly(modules.ToArray()),
            complete, complete ? FactAvailability.Present : state, reason);
    }

    private static IEnumerable<PhysicalMemoryRawRow> ReadNativeRows()
    {
        using var searcher = new ManagementObjectSearcher(Query);
        searcher.Options.Timeout = TimeSpan.FromMilliseconds(WmiGuard.DefaultTimeoutMs);
        using var collection = searcher.Get();
        foreach (ManagementObject row in collection)
        {
            using (row)
                yield return new PhysicalMemoryRawRow(field => row[field]);
        }
    }

    internal static IReadOnlyList<HardwareFact> ProjectConfiguredRate(PhysicalMemorySnapshot snapshot, DateTimeOffset at)
    {
        if (!snapshot.Complete || snapshot.Availability != FactAvailability.Present)
            return [UnavailableRate(at, snapshot.Availability == FactAvailability.Present ? FactAvailability.Unknown : snapshot.Availability,
                snapshot.Reason ?? "未取得完整的 WMI 實體記憶體批次。")];
        var selected = HardwareReadingPriority.ResolveMemory(null, WmiMemoryTimings.Build(snapshot.Modules), null);
        var facts = EvidenceLabService.CollectMemoryTimingFacts(selected, at);
        if (facts.Count > 0) return facts;
        return [UnavailableRate(at, FactAvailability.Unknown,
            snapshot.Modules.Count == 0 ? "WMI 未提供實體記憶體列；未推定沒有記憶體。"
                : "模組設定速率缺值、非正值或不一致；未取最大值、第一列或標稱 Speed 填補全機設定速率。")];
    }

    internal static HardwareFact UnavailableRate(DateTimeOffset at, FactAvailability availability, string reason)
        => new("memory.rate", "記憶體", "設定資料速率（WMI 回報）", "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason);

    private static PhysicalMemorySnapshot Failure(FactAvailability availability, string reason)
        => new(new(0, 0, 0, 0), [], false, availability, reason);

    private static FactAvailability Availability(Exception error) => error is UnauthorizedAccessException or SecurityException
        || error is ManagementException { ErrorCode: ManagementStatus.AccessDenied }
            ? FactAvailability.InsufficientPrivilege : FactAvailability.ReadError;
}
