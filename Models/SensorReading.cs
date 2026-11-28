using System.Diagnostics;

namespace XinSpect;

/// <summary>A provider observation. Id is provider/device/sensor identity, never a translated label.</summary>
public sealed record SensorReading(
    string Id, string DeviceId, string DeviceName, string Name, string TypeText, string Unit,
    double? Value, FactAvailability Availability, FactTrustLevel Trust, string Source,
    DateTimeOffset ReadAtUtc, DateTimeOffset? SourceUpdatedAtUtc, string Reason)
{
    // Unknown/speculative/invalid observations remain displayable evidence, but cannot enter
    // statistics, alerts or numeric charts. Reported does not mean independently calibrated.
    public bool IsUsable => Availability == FactAvailability.Present
        && Trust is not (FactTrustLevel.Unknown or FactTrustLevel.Speculative)
        && Value is double v && double.IsFinite(v);

    public string Label => DeviceName + " / " + Name;
}

/// <summary>Immutable whole-poll observation; no UI text parsing or old-value substitution.</summary>
public sealed record SensorFrame(long Sequence, long CapturedTimestamp, DateTimeOffset ReadAtUtc,
    IReadOnlyList<SensorReading> Readings)
{
    public static SensorFrame Empty { get; } = new(0, 0, DateTimeOffset.MinValue, Array.Empty<SensorReading>());

    public bool IsFresh(long nowTimestamp, TimeSpan maxAge)
        => Sequence > 0 && CapturedTimestamp > 0 && nowTimestamp >= CapturedTimestamp
            && maxAge > TimeSpan.Zero
            && (double)(nowTimestamp - CapturedTimestamp) / Stopwatch.Frequency <= maxAge.TotalSeconds;
}
