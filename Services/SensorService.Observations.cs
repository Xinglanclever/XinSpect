using System.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace XinSpect;

public sealed partial class SensorService
{
    private readonly Dictionary<ISensor, IHardware> _sensorHardware = new();
    private readonly SensorStatisticsService _sensorStatistics = new();
    private SensorFrame _pendingSensorFrame = SensorFrame.Empty;
    private SensorFrame _currentSensorFrame = SensorFrame.Empty;
    private long _sensorSequence;
    private bool _sensorPollSucceeded;
    private string _sensorPollReason = "尚未輪詢";
    private DateTimeOffset _statisticsStartedAt = DateTimeOffset.UtcNow;

    public SensorFrame CurrentSensorFrame => Volatile.Read(ref _currentSensorFrame);
    public string StatisticsStatusText => $"統計自 {_statisticsStartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} 起；"
        + "平均為有效輪詢樣本的算術平均，非時間加權；未知／失敗／重複樣本不納入。";
    public string SensorPollStatusText => _sensorPollSucceeded
        ? $"已取得 {CurrentSensorFrame.Readings.Count} / {_sensorRows.Count} 個來源觀測"
            + (_sensorRows.Count > SensorStatisticsService.MaxSensors ? "（超過 16,384 項上限者未發布）" : "")
            + "；來源內部更新時間未知，未驗證功率不進統計。"
        : "本拍讀取失敗；保留已累積統計，但不把舊讀值當成新樣本。";

    private SensorRow CreateSensorRow(IHardware hardware, ISensor sensor, string group)
    {
        _sensorHardware[sensor] = hardware;
        string deviceId = ProviderIdentifier(() => hardware.Identifier?.ToString());
        string sensorId = ProviderIdentifier(() => sensor.Identifier?.ToString());
        return new SensorRow(group, sensor.Name, TypeZh(sensor.SensorType), UnitOf(sensor.SensorType),
            "lhm:" + sensorId, deviceId, hardware.Name);
    }

    private static string ProviderIdentifier(Func<string?> get)
    {
        try
        {
            string? id = get();
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }
        catch { /* Metadata failure is not a fabricated stable identity. */ }
        return "session/" + Guid.NewGuid().ToString("N");
    }

    // Called under _pollLock, after the provider completes or fails. It never invokes hardware.
    private void CaptureSensorPoll(bool succeeded, string? reason = null, long? startedTimestamp = null)
    {
        _sensorPollSucceeded = succeeded;
        _sensorPollReason = reason ?? "來源內部更新時間未知；本時間是本程式取得觀測的時間。";
        // Freshness conservatively starts before traversing any provider. A slow later device
        // must not renew an earlier device's cached observation. ReadAtUtc is capture completion,
        // not a fabricated provider update timestamp; SourceUpdatedAtUtc remains unknown.
        _pendingSensorFrame = new(++_sensorSequence, startedTimestamp ?? _timestamp(),
            DateTimeOffset.UtcNow, Array.Empty<SensorReading>());
        RebuildSensorObservations();
    }

    // The first Poll precedes BuildCache; retain the original capture time when binding its values.
    private void RebuildSensorObservations()
    {
        var frame = _pendingSensorFrame;
        var readings = new List<SensorReading>(Math.Min(_sensorRows.Count, SensorStatisticsService.MaxSensors));
        foreach (var (sensor, row) in _sensorRows.Take(SensorStatisticsService.MaxSensors))
        {
            double? value = null;
            var state = _sensorPollSucceeded ? FactAvailability.Present : FactAvailability.ReadError;
            string reason = _sensorPollReason;
            try
            {
                if (_sensorPollSucceeded) value = SensorSanity.Plausible(sensor.SensorType, sensor.Value);
                if (_sensorPollSucceeded && value is null)
                { state = FactAvailability.ReadError; reason = "來源未提供有效有限讀值；不以舊值或 0 補值。"; }
                if (_sensorHardware.TryGetValue(sensor, out var hw) && hw.HardwareType == HardwareType.Cpu)
                {
                    if (sensor.SensorType == SensorType.Power && value.HasValue)
                    {
                        // Global RAPL response validation has no source/socket binding and
                        // cannot establish that this LHM sensor actually updated this poll.
                        if (state == FactAvailability.Present) state = FactAvailability.Unknown;
                        reason += " LHM CPU 功率缺少當拍讀取成功與來源更新時間證據；全域 RAPL 響應驗證未綁定此感測器。"
                            + "保留上報值供診斷，不納入統計／CSV 數值／告警。";
                    }
                    if (sensor.SensorType == SensorType.Clock && row.DeviceId.StartsWith("/amdcpu/", StringComparison.Ordinal)
                        && sensor.Name is "Memory" or "Uncore" or "Fabric" && value.HasValue)
                    {
                        if (state == FactAvailability.Present) state = FactAvailability.Unknown;
                        reason += " LHM PM table 可能保留前值；來源未提供成功更新時間，不能確認是目前控制器時脈。";
                    }
                }
            }
            catch (Exception ex)
            { value = null; state = FactAvailability.ReadError; reason = "來源讀取失敗：" + ex.GetType().Name; }
            // Metadata diagnostics also survive an individual sensor.Value getter failure.
            if (row.Id.Contains("session/", StringComparison.Ordinal) || row.DeviceId.StartsWith("session/", StringComparison.Ordinal))
            {
                if (state == FactAvailability.Present) state = FactAvailability.Unknown;
                reason += " 來源識別碼不可讀；識別僅限本次工作階段。";
            }
            readings.Add(new(row.Id, row.DeviceId, row.DeviceName, row.Name, row.TypeText, row.Unit,
                value, state, FactTrustLevel.Reported,
                "LibreHardwareMonitor " + (typeof(Computer).Assembly.GetName().Version?.ToString() ?? "版本未知"),
                frame.ReadAtUtc, null, reason));
        }
        var duplicates = readings.GroupBy(r => r.Id, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        _pendingSensorFrame = frame with { Readings = Array.AsReadOnly(readings.Select(r => duplicates.Contains(r.Id)
            ? r with { Availability = r.Availability == FactAvailability.Present ? FactAvailability.Unknown : r.Availability,
                Reason = r.Reason + " 來源識別碼重複；不能確定裝置，不供數值消費。" } : r).ToArray()) };
    }

    internal void PublishSensorObservations()
    {
        SensorFrame frame;
        lock (_pollLock) frame = _pendingSensorFrame;
        bool changed = frame.Sequence != CurrentSensorFrame.Sequence;
        _sensorStatistics.Add(frame, _timestamp(), TimeSpan.FromSeconds(30));
        Volatile.Write(ref _currentSensorFrame, frame);
        if (changed || DetailedSensorsVisible)
        {
            // Rebuild keeps the same bounded row order. An ambiguous Id is never a
            // mapping key here: each row retains its own device identity and raw evidence.
            for (int i = 0; i < frame.Readings.Count && i < _sensorRows.Count; i++)
            {
                var row = _sensorRows[i].row;
                var reading = frame.Readings[i];
                _sensorStatistics.Values.TryGetValue(row.Id, out var statistics);
                row.ApplyObservation(reading, statistics);
            }
            OnPropertyChanged(nameof(CurrentSensorFrame));
            OnPropertyChanged(nameof(SensorPollStatusText));
        }
    }

    public void ResetSensorStatistics()
    {
        _sensorStatistics.Reset(CurrentSensorFrame.Sequence);
        _statisticsStartedAt = DateTimeOffset.UtcNow;
        foreach (var (_, row) in _sensorRows)
            if (row.Reading is { } reading) row.ApplyObservation(reading, null);
        OnPropertyChanged(nameof(StatisticsStatusText));
    }
}
