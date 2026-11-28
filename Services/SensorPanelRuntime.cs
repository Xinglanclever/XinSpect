using System.Diagnostics;

namespace XinSpect;

/// <summary>Pure observation consumer. It never starts collectors or stores observations on disk.</summary>
public sealed class SensorPanelRuntime
{
    public const int HistoryCapacity = 120;
    public static readonly TimeSpan FreshnessLimit = TimeSpan.FromSeconds(10);
    private sealed class History
    {
        public long LastSequence;
        public long LastCapturedTimestamp;
        public bool IsGap;
        public readonly Queue<SensorPanelPoint> Points = new();
    }
    private readonly Dictionary<(string Sensor, string Device, string Unit), History> _history = new();
    private long _lastSequence;
    private SensorFrame _current = SensorFrame.Empty;

    public IReadOnlyList<SensorPanelTileState> Update(SensorPanelLayout layout, SensorFrame frame, long nowTimestamp)
    {
        if (frame.Sequence > _lastSequence)
        {
            _current = frame;
            _lastSequence = frame.Sequence;
        }
        bool fresh = _current.IsFresh(nowTimestamp, FreshnessLimit);
        var groups = _current.Readings.GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var active = layout.Tiles.Select(t => (t.SensorId, t.DeviceId, t.Unit)).ToHashSet();
        foreach (var key in _history.Keys.Where(k => !active.Contains(k)).ToArray()) _history.Remove(key);
        var result = new List<SensorPanelTileState>(layout.Tiles.Count);
        foreach (var tile in layout.Tiles)
        {
            groups.TryGetValue(tile.SensorId, out var candidates);
            var reading = candidates is { Length: 1 } ? candidates[0] : null;
            bool matches = reading is not null && reading.DeviceId == tile.DeviceId && reading.Unit == tile.Unit;
            double? value = fresh && matches && reading!.IsUsable ? reading.Value : null;
            string status = !fresh ? "資料過期／尚無新採集"
                : candidates is { Length: > 1 } ? "識別碼重複，拒絕綁定"
                : reading is null ? "感測器已移除／本機缺少此項目"
                : !matches ? "裝置身份或單位已變更，請重新加入"
                : !reading.IsUsable ? reading.Availability + " · " + reading.Trust +
                    (string.IsNullOrWhiteSpace(reading.Reason) ? "" : " · " + reading.Reason)
                : "Present · " + reading.Trust;
            var key = (tile.SensorId, tile.DeviceId, tile.Unit);
            if (!_history.TryGetValue(key, out var history)) _history[key] = history = new History();
            // Repeated UI ticks cannot duplicate a sample. Expiry creates one explicit gap;
            // the next successful frame starts a new segment instead of extending the old line.
            if (_current.Sequence > history.LastSequence || (value is null && !history.IsGap))
            {
                if (!history.IsGap && history.LastCapturedTimestamp > 0
                    && _current.CapturedTimestamp > history.LastCapturedTimestamp
                    && (double)(_current.CapturedTimestamp - history.LastCapturedTimestamp) / Stopwatch.Frequency > FreshnessLimit.TotalSeconds)
                    history.Points.Enqueue(new SensorPanelPoint(_current.Sequence, _current.ReadAtUtc, null));
                history.Points.Enqueue(new SensorPanelPoint(_current.Sequence, _current.ReadAtUtc, value));
                history.LastSequence = _current.Sequence;
                history.LastCapturedTimestamp = _current.CapturedTimestamp;
                history.IsGap = value is null;
                while (history.Points.Count > HistoryCapacity) history.Points.Dequeue();
            }
            result.Add(new SensorPanelTileState(tile, reading, value, status, history.Points.ToArray()));
        }
        return result;
    }
}
