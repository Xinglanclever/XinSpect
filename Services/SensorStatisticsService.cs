namespace XinSpect;

public sealed record SensorStatistics(long Count, double Minimum, double Maximum, double Average,
    string Unit, string DeviceId);

/// <summary>Arithmetic mean of accepted poll observations, not a time-weighted hardware average.</summary>
public sealed class SensorStatisticsService
{
    private readonly Dictionary<string, SensorStatistics> _values = new(StringComparer.Ordinal);
    private long _lastSequence;
    public const int MaxSensors = 16384;
    public IReadOnlyDictionary<string, SensorStatistics> Values => _values;

    public bool Add(SensorFrame frame, long nowTimestamp, TimeSpan maxAge)
    {
        if (frame.Sequence <= _lastSequence) return false;
        _lastSequence = frame.Sequence;
        if (!frame.IsFresh(nowTimestamp, maxAge)) return false;
        var duplicates = frame.Readings.GroupBy(x => x.Id, StringComparer.Ordinal)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var reading in frame.Readings)
        {
            if (!reading.IsUsable || string.IsNullOrWhiteSpace(reading.Id) || duplicates.Contains(reading.Id)) continue;
            double value = reading.Value!.Value;
            if (!_values.TryGetValue(reading.Id, out var previous)
                || previous.Unit != reading.Unit || previous.DeviceId != reading.DeviceId)
            {
                if (_values.Count < MaxSensors || _values.ContainsKey(reading.Id))
                    _values[reading.Id] = new(1, value, value, value, reading.Unit, reading.DeviceId);
                continue;
            }
            if (previous.Count == long.MaxValue) continue;
            long count = previous.Count + 1;
            // Weighted terms avoid overflowing the subtraction for opposite-sign finite values.
            double average = previous.Average * ((count - 1d) / count) + value / count;
            if (!double.IsFinite(average)) continue;
            _values[reading.Id] = previous with { Count = count, Minimum = Math.Min(previous.Minimum, value),
                Maximum = Math.Max(previous.Maximum, value), Average = average };
        }
        return true;
    }

    public void Reset(long currentSequence)
    {
        _values.Clear();
        _lastSequence = Math.Max(0, currentSequence);
    }
}

public static class SensorValueText
{
    public static string Format(double? value, string unit)
        => value is double v && double.IsFinite(v)
            ? v.ToString(unit is "V" or "A" ? "0.000" : unit == "RPM" ? "0" : "0.##",
                System.Globalization.CultureInfo.CurrentCulture) + (unit.Length > 0 ? " " + unit : "")
            : "—";
}
