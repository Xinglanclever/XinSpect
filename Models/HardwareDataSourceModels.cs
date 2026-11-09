using System.Text.Json.Serialization;

namespace XinSpect;

[JsonConverter(typeof(JsonStringEnumConverter<HardwareDataSourcePermission>))]
public enum HardwareDataSourcePermission
{
    StandardUser,
    Administrator,
    System,
}

[JsonConverter(typeof(JsonStringEnumConverter<HardwareDataSourceRunStatus>))]
public enum HardwareDataSourceRunStatus
{
    Completed,
    TimedOut,
    ReadError,
}

public sealed record HardwareSourceFactDescriptor(
    string Key,
    string Category,
    string Name,
    string Unit = "",
    bool Sensitive = false);

public sealed record HardwareDataSourceDescriptor(
    string SourceId,
    string Version,
    string DataDomain,
    IReadOnlyList<HardwareSourceFactDescriptor> SupportedFacts,
    HardwareDataSourcePermission RequiredPermission,
    TimeSpan Timeout,
    bool SupportsCancellation);

public interface IHardwareDataSource
{
    HardwareDataSourceDescriptor Descriptor { get; }

    Task<IReadOnlyList<HardwareFact>> CollectAsync(
        DateTimeOffset measuredAtUtc, CancellationToken cancellationToken);
}

public sealed record HardwareDataSourceRunResult(
    HardwareDataSourceDescriptor Source,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    HardwareDataSourceRunStatus Status,
    IReadOnlyList<HardwareFact> Facts,
    string? FailureCode);
