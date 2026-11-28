using System.Globalization;

namespace XinSpect;

/// <summary>The quantity and provenance of one controller observation; never an SPD capability.</summary>
public sealed record MemoryControllerReading(
    string Key, string Label, double? NumericValue, string Unit, string RawText,
    FactAvailability Availability, FactTrustLevel Trust, string Source, string RawField,
    string Scope, DateTimeOffset? CapturedAtUtc, DateTimeOffset? SourceUpdatedAtUtc, string Reason, bool IsExpired = false)
{
    public string ValueText => NumericValue is { } value && double.IsFinite(value)
        ? value.ToString("0.###", CultureInfo.CurrentCulture) + (Unit.Length > 0 ? " " + Unit : "")
        : string.IsNullOrWhiteSpace(RawText) ? "—" : RawText;
    public string StateText => IsExpired ? "擷取已過期" : Availability switch
    {
        FactAvailability.Present => "已取得",
        FactAvailability.Unknown => "尚未確認",
        FactAvailability.NotApplicable => "不適用",
        FactAvailability.NotSupported => "尚未支援",
        FactAvailability.InsufficientPrivilege => "權限不足",
        _ => "讀取失敗",
    };
    public string SourceText => Source + " · " + Trust + " · " + Scope;
    public string DetailText => RawField + "；" + Reason;
    /// <summary>Preserves the provider field and only localizes a fully catalogued explanatory projection.</summary>
    public string DisplayDetailText
    {
        get
        {
            bool hasField = !string.IsNullOrEmpty(RawField);
            bool hasReason = !string.IsNullOrEmpty(Reason);
            if (!hasField) return ProductTextProjection.LocalizeKnown(Reason ?? "");
            if (!hasReason) return RawField;
            return ProductTextProjection.LocalizeKnown(RawField + "；" + Reason);
        }
    }
    public string TimeText => "本程式擷取：" + (CapturedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "未知")
        + "；來源更新：" + (SourceUpdatedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "未知");
}

/// <summary>Immutable observations: quantities from unlike sources remain distinct.</summary>
public sealed class MemoryControllerSnapshot
{
    public static MemoryControllerSnapshot Empty { get; } = new();
    public IReadOnlyList<MemoryControllerReading> Clocks { get; init; } = Array.Empty<MemoryControllerReading>();
    public IReadOnlyList<MemoryControllerReading> ConfiguredRates { get; init; } = Array.Empty<MemoryControllerReading>();
    public IReadOnlyList<MemoryControllerReading> SubTimings { get; init; } = Array.Empty<MemoryControllerReading>();
    public long CapturedTimestamp { get; init; }
    public string CoverageText { get; init; } = "目前未取得控制器觀測。";
    public string TimingCoverageText => "目前副時序只採用已取得的 CPU-Z 報告欄位；未實作控制器暫存器副時序直讀。tRC／tRRD_S/L／tFAW／tWTR_S/L／tWR／tRTP／tCWL 尚未支援。SPD JEDEC／XMP／EXPO 表不作目前值。";
}

/// <summary>Copies a cached provider value, without performing any hardware operation.</summary>
internal sealed record MemoryControllerClockSample(string CpuId, string CpuName, string SensorId,
    string SensorName, double? ValueMHz, DateTimeOffset CapturedAtUtc);
