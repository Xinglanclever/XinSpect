using System.Text.Json.Serialization;

namespace XinSpect;

public enum DeepBenchTestStatus { Implemented, Integrated, NotSupported, Deferred }
public enum DeepBenchDomain { Cpu, Topology, Memory, Gpu, Storage, Gauntlet, UserExperience, Confidence }
public enum DeepBenchFailureKind { None, NotRun, Unsupported, DriverRejected, InsufficientPermission, Unstable, Cancelled, PlatformError }
public enum DeepBenchConfidence { Insufficient, High, Medium, Low }
public enum DeepBenchRunProfile { Quick, Full }

public sealed record DeepBenchMetricPoint(
    double Value,
    IReadOnlyDictionary<string, string> Axes,
    IReadOnlyList<double> Samples)
{
    [JsonIgnore]
    public DeepBenchMeasurementSummary Statistics => DeepBenchMeasurementStatistics.FromSamples(Samples);
}

public sealed record DeepBenchMetric(
    string Id,
    string Title,
    string Unit,
    bool HigherIsBetter,
    string Configuration,
    IReadOnlyList<double> Samples,
    IReadOnlyList<DeepBenchMetricPoint> Points)
{
    [JsonIgnore]
    public DeepBenchMeasurementSummary Statistics => DeepBenchMeasurementStatistics.FromSamples(Samples);
}

public sealed record DeepBenchTestResult(
    string TestId,
    Guid SessionId,
    DeepBenchRunProfile Profile,
    DateTime StartedUtc,
    DateTime EndedUtc,
    string Configuration,
    IReadOnlyList<DeepBenchMetric> Metrics,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<string> Limitations,
    DeepBenchFailureKind FailureKind,
    string? Error)
{
    [JsonIgnore]
    public bool HasSuccessfulMeasurement => FailureKind == DeepBenchFailureKind.None && Metrics.Count > 0;
}
