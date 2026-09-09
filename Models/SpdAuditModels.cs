namespace XinSpect;

/// <summary>SPD 稽核的四種結論；「可疑」不是重刷定論。</summary>
public enum SpdAuditVerdict
{
    Consistent,
    Conflict,
    MissingData,
    Suspicious,
}

public enum SpdAuditSourceKind
{
    NativeSpd,
    CpuZ,
    Smbios,
    CurrentTimings,
}

public enum SpdAuditField
{
    SourceAvailability,
    Checksum,
    JedecVendor,
    PartNumber,
    SerialNumber,
    Capacity,
    Speed,
    ManufactureDate,
    CurrentTimings,
}

/// <summary>
/// 跨來源比較用的正規化 adapter 輸入。既有 CPU-Z 模型沒有序號；呼叫端若另有可信值，
/// 可用 record 的 <c>with</c> 補入，而不必改動既有模型。
/// </summary>
public sealed record SpdAuditSourceInput(
    SpdAuditSourceKind Kind,
    string Source,
    string? Locator = null,
    string? Vendor = null,
    string? PartNumber = null,
    string? SerialNumber = null,
    int? CapacityMiB = null,
    int? SpeedMTs = null,
    int? ManufactureYear = null,
    int? ManufactureWeek = null,
    bool? BaseCrcValid = null,
    bool? ModuleCrcValid = null,
    bool? VendorParityValid = null,
    string? ChecksumText = null);

/// <summary>單一 finding 內的一筆可追溯證據。</summary>
public sealed record SpdAuditEvidence(SpdAuditSourceKind Kind, string Source, string Value);

/// <summary>欄位級稽核 finding；每一筆都帶來源及其實際值。</summary>
public sealed record SpdAuditFinding(
    SpdAuditField Field,
    SpdAuditVerdict Verdict,
    string Summary,
    IReadOnlyList<SpdAuditEvidence> Evidence);

/// <summary>每個原生 SPD 插槽的完整證據與保守結論。</summary>
public sealed record SpdSlotAudit(
    string Slot,
    SpdAuditVerdict Verdict,
    string Summary,
    IReadOnlyList<SpdAuditSourceInput> Sources,
    IReadOnlyList<SpdAuditFinding> Findings);

/// <summary>目前時序是全機資料，會附在每個插槽作背景證據，不假裝是單條 DIMM 的專屬值。</summary>
public sealed record CurrentMemoryTimingEvidence(
    string Source,
    string? MemoryType,
    int? DataRateMTs,
    string? PrimaryTimings);

/// <summary>純稽核入口；NativeSpd 順序就是輸出插槽順序。</summary>
public sealed record SpdAuditInput(
    IReadOnlyList<SpdAuditSourceInput> NativeSpd,
    IReadOnlyList<SpdAuditSourceInput> CpuZ,
    IReadOnlyList<SpdAuditSourceInput> Smbios,
    CurrentMemoryTimingEvidence? CurrentTimings = null);
