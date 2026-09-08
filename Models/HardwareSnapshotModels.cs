using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>硬體時間膠囊 JSON 格式的目前版本。</summary>
public static class HardwareSnapshotSchema
{
    public const int CurrentVersion = 1;
}

/// <summary>事實的可信程度；這是來源品質標示，不是保證。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FactTrustLevel>))]
public enum FactTrustLevel
{
    Unknown,
    Reported,
    Derived,
    Measured,
}

/// <summary>UI 可直接組成的硬體事實；NumericValue 只在可可靠解析時提供。</summary>
public sealed record HardwareFact(
    string Key,
    string Category,
    string Name,
    string Value,
    string Unit,
    string Source,
    FactTrustLevel Trust,
    bool Sensitive,
    DateTimeOffset MeasuredAtUtc,
    double? NumericValue = null);

/// <summary>匯出敏感值時的明確政策。預設一律遮蔽，Preserve 必須由呼叫端主動指定。</summary>
public enum SensitiveValuePolicy
{
    Redact,
    Preserve,
}

/// <summary>一筆可跨時間比較的硬體事實。</summary>
public sealed record HardwareSnapshotFact
{
    [JsonPropertyOrder(0)] public required string Key { get; init; }
    [JsonPropertyOrder(1)] public required string Category { get; init; }
    [JsonPropertyOrder(2)] public required string Name { get; init; }
    [JsonPropertyOrder(3)] public required string Value { get; init; }
    [JsonPropertyOrder(4)] public double? NumericValue { get; init; }
    [JsonPropertyOrder(5)] public string? Unit { get; init; }
    [JsonPropertyOrder(6)] public required string Source { get; init; }
    [JsonPropertyOrder(7)] public FactTrustLevel Trust { get; init; }
    [JsonPropertyOrder(8)] public bool Sensitive { get; init; }
    [JsonPropertyOrder(9)] public DateTimeOffset MeasuredAtUtc { get; init; }
}

/// <summary>
/// 與檔案同放的 SHA-256 完整性摘要。它不是數位簽章，也不能阻止有能力重算摘要的人改檔。
/// </summary>
public sealed record HardwareSnapshotIntegrity
{
    public const string Sha256Algorithm = "SHA-256";

    [JsonPropertyOrder(0)] public required string Algorithm { get; init; }
    [JsonPropertyOrder(1)] public required string Hash { get; init; }
}

/// <summary>一份版本化硬體時間膠囊；機器識別只接受匿名雜湊，不保存原始序號。</summary>
public sealed record HardwareSnapshot
{
    [JsonPropertyOrder(0)] public int SchemaVersion { get; init; }
    [JsonPropertyOrder(1)] public required string AppVersion { get; init; }
    [JsonPropertyOrder(2)] public required string AnonymousMachineId { get; init; }
    [JsonPropertyOrder(3)] public DateTimeOffset CapturedAtUtc { get; init; }
    [JsonPropertyOrder(4)] public bool SensitiveValuesPreserved { get; init; }
    [JsonPropertyOrder(5)] public required IReadOnlyList<HardwareSnapshotFact> Facts { get; init; }
    [JsonPropertyOrder(6)] public required HardwareSnapshotIntegrity Integrity { get; init; }
}

/// <summary>存檔選項。敏感值預設遮蔽；保留必須明確 opt-in。</summary>
public sealed record HardwareSnapshotSaveOptions
{
    public SensitiveValuePolicy SensitiveValues { get; init; } = SensitiveValuePolicy.Redact;
    public bool Overwrite { get; init; } = true;
}

public enum SnapshotChangeKind
{
    Added,
    Removed,
    Changed,
    Unchanged,
}

/// <summary>單一穩定 key 的前後差異。</summary>
public sealed record HardwareFactChange
{
    public required string Key { get; init; }
    public SnapshotChangeKind Kind { get; init; }
    public HardwareFact? Previous { get; init; }
    public HardwareFact? Current { get; init; }
    public double? NumericDelta { get; init; }
    public string? DeltaText => NumericDelta is { } d ? d.ToString("+0.################;-0.################;0", System.Globalization.CultureInfo.InvariantCulture) : null;
}

/// <summary>兩份時間膠囊的完整比較結果。</summary>
public sealed record HardwareSnapshotDiff
{
    public required string BeforeMachineId { get; init; }
    public required string AfterMachineId { get; init; }
    public DateTimeOffset BeforeCapturedAtUtc { get; init; }
    public DateTimeOffset AfterCapturedAtUtc { get; init; }
    public bool IsSameMachine => string.Equals(BeforeMachineId, AfterMachineId, StringComparison.Ordinal);
    public required IReadOnlyList<HardwareFactChange> Changes { get; init; }
    public IReadOnlyList<HardwareFactChange> Entries => Changes;
    public int Added => Changes.Count(x => x.Kind == SnapshotChangeKind.Added);
    public int Removed => Changes.Count(x => x.Kind == SnapshotChangeKind.Removed);
    public int Changed => Changes.Count(x => x.Kind == SnapshotChangeKind.Changed);
    public int Unchanged => Changes.Count(x => x.Kind == SnapshotChangeKind.Unchanged);
    public int AddedCount => Added;
    public int RemovedCount => Removed;
    public int ChangedCount => Changed;
    public int UnchangedCount => Unchanged;
}
