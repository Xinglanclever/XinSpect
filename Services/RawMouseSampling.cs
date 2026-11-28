namespace XinSpect;

/// <summary>WM_INPUT 接收端的 QPC 到達間隔；不是 USB 匯流排或裝置內部輪詢量測。</summary>
public sealed record RawMouseDeviceSample(
    string DeviceId, string DisplayName, bool IdentityReadable, int Reports, int MovementReports,
    int Samples, int IdleGaps, int InvalidTimestamps, int ExcludedReports, int NoCoalesceReports,
    double MedianIntervalUs, double P95IntervalUs, double JitterMadUs, double ObservedHz,
    bool CapacityReached, bool Removed);

public sealed record RawMouseSnapshot(
    bool IsRunning, FactAvailability Availability, string Status, DateTimeOffset StartedAtUtc,
    DateTimeOffset MeasuredAtUtc, int WindowMs, IReadOnlyList<RawMouseDeviceSample> Devices,
    int IgnoredDevices, int ReadErrors);

/// <summary>純有界統計器；所有時間由呼叫者注入單調鐘，裝置間絕不合併。</summary>
internal sealed class RawMouseSampling
{
    internal const int MaximumDevices = 16;
    internal const int MaximumIntervalsPerDevice = 81_920; // 10 s @ 8000 Hz + 端點餘量
    internal const int MinimumIntervals = 20;
    internal const int MaximumDurationMs = 10_000;
    internal const double IdleGapUs = 200_000;
    private readonly long _frequency;
    private readonly long _started;
    private readonly DateTimeOffset _startedAt;
    private readonly Dictionary<nint, Device> _devices = new();
    private readonly HashSet<nint> _ignored = new();
    private int _errors;
    private long _ended;
    private string? _failure;
    private bool _running = true;

    internal RawMouseSampling(long frequency, long started, DateTimeOffset startedAt)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _frequency = frequency; _started = started; _startedAt = startedAt;
    }

    internal bool IsRunning => _running;
    internal bool IsExpired(long now) => ElapsedMs(now) >= MaximumDurationMs;
    internal bool ContainsDevice(nint handle) => _devices.ContainsKey(handle);

    internal void Add(nint handle, string id, string display, bool readable, long timestamp,
        int x, int y, ushort flags)
    {
        if (!_running || timestamp < _started) return;
        if (IsExpired(timestamp)) { Stop(timestamp); return; }
        if (!_devices.TryGetValue(handle, out var device))
        {
            if (_devices.Count >= MaximumDevices)
            {
                // 不保留無上限的被忽略 handle 集合。
                if (_ignored.Count < MaximumDevices) _ignored.Add(handle);
                return;
            }
            _devices.Add(handle, device = new Device(id, display, readable));
        }
        if (device.Removed) return; // 本次視窗不把重用的 handle 當成同一裝置
        device.Reports++;
        if ((flags & 8) != 0) device.NoCoalesceReports++;
        // 僅相對移動報告。按鍵／滾輪與絕對座標（例如數位板）不混成移動回報間隔。
        if ((flags & 1) != 0 || (x == 0 && y == 0)) { device.ExcludedReports++; return; }
        device.MovementReports++;
        if (device.LastTimestamp is long previous)
        {
            double us = (timestamp - previous) * (1_000_000.0 / _frequency);
            if (us <= 0) { device.InvalidTimestamps++; return; }
            if (us > IdleGapUs) device.IdleGaps++;
            else if (device.Intervals.Count < MaximumIntervalsPerDevice) device.Intervals.Add(us);
            else device.CapacityReached = true;
        }
        device.LastTimestamp = timestamp;
    }

    internal void Remove(nint handle)
    {
        if (_devices.TryGetValue(handle, out var device))
        { device.Removed = true; device.LastTimestamp = null; }
    }

    internal void ReadError() => _errors++;
    internal void Stop(long now, string? failure = null)
    {
        if (!_running) return;
        _ended = Math.Max(_started, now); _failure = failure; _running = false;
    }

    internal RawMouseSnapshot Snapshot(long now)
    {
        long end = _running ? now : _ended;
        int elapsed = (int)Math.Clamp(ElapsedMs(end), 0, MaximumDurationMs);
        var devices = _devices.Values.Select(d => d.Snapshot()).ToArray();
        var availability = _failure != null || _errors > 0 ? FactAvailability.ReadError
            : devices.Any(d => d.Samples >= MinimumIntervals && !d.Removed && !d.CapacityReached && d.InvalidTimestamps == 0)
                ? FactAvailability.Present : FactAvailability.Unknown;
        string status = _failure ?? (_errors > 0 ? $"Raw Input 讀取失敗 {_errors} 次；本窗不可作完整量測。"
            : devices.Length == 0 ? "尚無 Raw Input 滑鼠報告；請在視窗作用中移動滑鼠。無報告不證明沒有滑鼠。"
            : _running ? "取樣中；持續移動欲測滑鼠，其他裝置分開統計。" : "取樣已停止；數值只代表這段前景取樣期間。");
        return new(_running, availability, status, _startedAt, _startedAt.AddMilliseconds(elapsed),
            elapsed, devices, _ignored.Count, _errors);
    }

    private double ElapsedMs(long now) => (now - _started) * (1000.0 / _frequency);

    internal static (double Median, double P95, double Mad) Distribution(IReadOnlyList<double> intervals)
    {
        var sorted = intervals.Where(v => double.IsFinite(v) && v > 0).OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return (0, 0, 0);
        double median = Median(sorted);
        var deviations = sorted.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToArray();
        return (median, sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], Median(deviations));
    }

    private static double Median(double[] values) => values.Length % 2 == 1
        ? values[values.Length / 2] : values[values.Length / 2 - 1] / 2 + values[values.Length / 2] / 2;

    private sealed class Device(string id, string display, bool readable)
    {
        internal readonly List<double> Intervals = new(1024);
        internal long? LastTimestamp;
        internal int Reports, MovementReports, IdleGaps, InvalidTimestamps, ExcludedReports, NoCoalesceReports;
        internal bool CapacityReached, Removed;
        internal RawMouseDeviceSample Snapshot()
        {
            var (median, p95, mad) = Distribution(Intervals);
            return new(id, display, readable, Reports, MovementReports, Intervals.Count, IdleGaps,
                InvalidTimestamps, ExcludedReports, NoCoalesceReports, median, p95, mad,
                median > 0 ? 1_000_000 / median : 0, CapacityReached, Removed);
        }
    }
}
