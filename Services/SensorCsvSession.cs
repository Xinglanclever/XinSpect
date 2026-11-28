using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace XinSpect;

/// <summary>Pure, hardware-independent long-form CSV writer. One complete frame is flushed per sample.</summary>
internal sealed class SensorCsvSession : IDisposable
{
    internal const string Header = "FrameReadAtUtc,ElapsedSeconds,FrameSequence,SensorId,DeviceId,DeviceName,SensorName,SensorType,Unit,Value,Availability,ProviderAvailability,Trust,Source,ReadAtUtc,SourceUpdatedAtUtc,SourceTimeState,State,Reason";
    private readonly TextWriter _writer;
    private readonly SensorLogSensorDefinition[] _sensors;
    private readonly long _startedTimestamp;
    private long? _lastWriteTimestamp;
    private long _lastSequence;
    private bool _disposed;

    internal int FrameCount { get; private set; }
    internal long RecordCount { get; private set; }
    internal int SensorCount => _sensors.Length;

    internal SensorCsvSession(TextWriter writer, IEnumerable<SensorLogSensorDefinition> sensors, long startedTimestamp)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(sensors);
        _sensors = sensors.ToArray();
        if (_sensors.Length == 0) throw new ArgumentException("Select at least one sensor.", nameof(sensors));
        if (_sensors.Any(s => string.IsNullOrWhiteSpace(s.Id))
            || _sensors.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != _sensors.Length)
            throw new ArgumentException("Sensor identities must be nonempty and unique.", nameof(sensors));
        _writer = writer;
        _startedTimestamp = startedTimestamp;
        _writer.WriteLine(Header);
        _writer.Flush();
    }

    internal bool TryWriteFrame(SensorFrame frame, long nowTimestamp, TimeSpan interval, TimeSpan maxAge)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (interval <= TimeSpan.Zero || maxAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        // A wall-clock correction cannot make a sample due. Never relabel an old frame as a new observation.
        if (frame.Sequence <= 0 || frame.Sequence <= _lastSequence || nowTimestamp < _startedTimestamp) return false;
        if (_lastWriteTimestamp is long last
            && (nowTimestamp < last || Seconds(nowTimestamp, last) < interval.TotalSeconds)) return false;

        bool freshFrame = frame.IsFresh(nowTimestamp, maxAge);
        var observations = frame.Readings.GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var csv = new StringBuilder();
        foreach (var sensor in _sensors)
        {
            observations.TryGetValue(sensor.Id, out var matches);
            var reading = matches is { Length: 1 } ? matches[0] : null;
            string state, reason;
            FactAvailability availability;
            double? value = null;
            if (matches is { Length: > 1 })
            {
                state = "Ambiguous"; availability = FactAvailability.Unknown;
                reason = "Multiple observations have the same sensor identity; value withheld.";
            }
            else if (reading is null)
            {
                state = "Missing"; availability = FactAvailability.Unknown;
                reason = "Selected sensor is absent from this frame; no previous value was reused.";
            }
            else if (!freshFrame)
            {
                state = "Stale"; availability = FactAvailability.Unknown;
                reason = WithReason("Local observation frame is stale; value withheld.", reading.Reason);
            }
            else if (reading.DeviceId != sensor.DeviceId || reading.TypeText != sensor.TypeText || reading.Unit != sensor.Unit)
            {
                state = "SchemaMismatch"; availability = FactAvailability.Unknown;
                reason = WithReason("Sensor device, quantity or unit changed during the session; value withheld.", reading.Reason);
            }
            else if (reading.Availability != FactAvailability.Present)
            {
                state = "Unavailable"; availability = reading.Availability;
                reason = WithReason("Provider availability is " + reading.Availability + "; value withheld.", reading.Reason);
            }
            else if (reading.Trust is FactTrustLevel.Unknown or FactTrustLevel.Speculative
                || string.IsNullOrWhiteSpace(reading.Source) || reading.ReadAtUtc == DateTimeOffset.MinValue)
            {
                state = "Unverified"; availability = FactAvailability.Unknown;
                reason = WithReason("Observation is unverified or has no source/read time; value withheld.", reading.Reason);
            }
            else if (!reading.IsUsable)
            {
                state = "Invalid"; availability = FactAvailability.Unknown;
                reason = WithReason("Provider returned no finite numeric value; value withheld.", reading.Reason);
            }
            else
            {
                state = "Present"; availability = FactAvailability.Present;
                value = reading.Value; reason = reading.Reason;
            }

            string[] cells =
            [
                Iso(frame.ReadAtUtc), Seconds(nowTimestamp, _startedTimestamp).ToString("0.#########", CultureInfo.InvariantCulture),
                frame.Sequence.ToString(CultureInfo.InvariantCulture),
                Cell(sensor.Id), Cell(sensor.DeviceId), Cell(sensor.DeviceName), Cell(sensor.Name), Cell(sensor.TypeText), Cell(sensor.Unit),
                value?.ToString("R", CultureInfo.InvariantCulture) ?? "", availability.ToString(), reading?.Availability.ToString() ?? "",
                (reading?.Trust ?? FactTrustLevel.Unknown).ToString(), Cell(reading?.Source ?? ""),
                reading is null ? "" : Iso(reading.ReadAtUtc),
                reading?.SourceUpdatedAtUtc is DateTimeOffset updated ? Iso(updated) : "",
                reading?.SourceUpdatedAtUtc is DateTimeOffset known && known != DateTimeOffset.MinValue ? "Reported" : "Unknown",
                state, Cell(reason),
            ];
            csv.AppendJoin(',', cells).Append("\r\n");
        }
        _writer.Write(csv.ToString());
        _writer.Flush();
        _lastWriteTimestamp = nowTimestamp;
        _lastSequence = frame.Sequence;
        FrameCount++;
        RecordCount += _sensors.Length;
        return true;
    }

    private static string Iso(DateTimeOffset time)
        => time == DateTimeOffset.MinValue ? "" : time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static double Seconds(long later, long earlier) => ((double)later - earlier) / Stopwatch.Frequency;
    private static string WithReason(string explanation, string reason)
        => string.IsNullOrWhiteSpace(reason) ? explanation : explanation + " " + reason;

    /// <summary>RFC 4180 quoting plus spreadsheet formula protection, including leading whitespace/control bypasses.</summary>
    internal static string Cell(string value)
    {
        int start = 0;
        while (start < value.Length && (char.IsWhiteSpace(value[start]) || char.IsControl(value[start])
            || value[start] is '\uFEFF' or '\u200B')) start++;
        if (start < value.Length && "=+-@".Contains(value[start])
            || value.Length > 0 && char.IsControl(value[0])) value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? flushError = null;
        try { _writer.Flush(); } catch (Exception ex) { flushError = ex; }
        try { _writer.Dispose(); }
        catch (Exception ex)
        {
            if (flushError is not null) throw new AggregateException(flushError, ex);
            throw;
        }
        if (flushError is not null) throw flushError;
    }
}
