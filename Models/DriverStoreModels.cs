using System.Text.Json.Serialization;

namespace XinSpect;

public sealed record DriverStoreBinding(string PublishedName, string DeviceId, string DeviceName);

/// <summary>DISM package identity, separate from device bindings. All OS metadata is Reported.</summary>
public sealed record DriverStorePackage
{
    public string PublishedName { get; init; } = "";
    public string OriginalFileName { get; init; } = "";
    public string Provider { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string ClassGuid { get; init; } = "";
    public string CatalogFile { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? DriverDate { get; init; }
    public bool InBox { get; init; }
    public bool BootCritical { get; init; }
    public DriverStoreSignature Signature { get; init; }
    public FactAvailability Availability { get; init; } = FactAvailability.Present;
    public FactTrustLevel Trust => FactTrustLevel.Reported;
    public string Source => "Windows DISM API: DismGetDrivers(AllDrivers=true)";
    public FactAvailability BindingAvailability { get; init; } = FactAvailability.Unknown;
    public IReadOnlyList<DriverStoreBinding> Bindings { get; init; } = [];
    public string BindingSource => "WMI Win32_PnPSignedDriver.InfName / DeviceID / DeviceName";
    public string BindingReason { get; init; } = "尚未查詢裝置綁定";
    public bool CanExport => Availability == FactAvailability.Present && !InBox && DriverStoreService.IsPublishedOemInf(PublishedName);
    [JsonIgnore] public string OriginText => LanguageService.T(InBox ? "Windows 隨附" : "第三方套件");
    [JsonIgnore] public string DateText => DriverDate?.ToString("yyyy-MM-dd") ?? LanguageService.T("未知");
    [JsonIgnore] public string SignatureText => LanguageService.T(Signature switch
    {
        DriverStoreSignature.Signed => "DISM 回報已簽章",
        DriverStoreSignature.Unsigned => "DISM 回報未簽章",
        _ => "未知（DISM 未驗證）",
    });
    // Only catalogued application prose is localized; unknown provider diagnostics remain verbatim.
    [JsonIgnore] public string BindingReasonText => BindingReason.StartsWith("WMI 裝置綁定失敗：", StringComparison.Ordinal)
        ? LanguageService.T("WMI 裝置綁定失敗：") + UiTextTranslation.KnownDisplayText(
            BindingReason["WMI 裝置綁定失敗：".Length..], LanguageService.IsEnglish, LanguageService.IsSimplified)
        : UiTextTranslation.KnownDisplayText(BindingReason, LanguageService.IsEnglish, LanguageService.IsSimplified);
    [JsonIgnore] public string BindingText => BindingAvailability == FactAvailability.Present
        ? Bindings.Count > 0 ? string.Format(LanguageService.T("WMI 回報 {0} 個綁定"), Bindings.Count)
            : LanguageService.T("WMI 未回報綁定；不代表可刪除")
        : string.Format(LanguageService.T("綁定未知：{0} — {1}"), BindingAvailability, BindingReasonText);
}

[JsonConverter(typeof(JsonStringEnumConverter<DriverStoreSignature>))]
public enum DriverStoreSignature { Unknown = 0, Unsigned = 1, Signed = 2 }

public sealed record DriverStoreInventory
{
    public FactAvailability Availability { get; init; } = FactAvailability.Unknown;
    public string Reason { get; init; } = "尚未讀取";
    public DateTimeOffset ReadAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<DriverStorePackage> Packages { get; init; } = [];
    public FactAvailability BindingAvailability { get; init; } = FactAvailability.Unknown;
    public string BindingReason { get; init; } = "尚未讀取";
    public bool Cancelled { get; init; }
}

public sealed record DriverStoreFileHash(string RelativePath, long Length, string Sha256);
public sealed record DriverStoreExportEntry
{
    public required DriverStorePackage Package { get; init; }
    public string Directory { get; init; } = "";
    public string Status { get; set; } = "Pending";
    public string Detail { get; set; } = "";
    public int? ExitCode { get; set; }
    public IReadOnlyList<DriverStoreFileHash> Files { get; set; } = [];
}

public sealed record DriverStoreExportManifest
{
    public int SchemaVersion => 1;
    public string Operation => "ExportOnly";
    public string ExportSource => "Windows System32/pnputil.exe /export-driver <oem#.inf> <new-directory>";
    public string SignatureLimit => "DISM only verifies boot-critical driver signatures. Reported metadata and SHA-256 are not trust, completeness, or restorability certification.";
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string Status { get; set; } = "Running";
    public string Detail { get; set; } = "";
    public List<DriverStoreExportEntry> Packages { get; init; } = [];
}

public sealed record DriverStoreExportResult(FactAvailability Availability, string Status, string Detail,
    string? Directory = null, string? ManifestPath = null, DriverStoreExportManifest? Manifest = null);

public sealed record DriverStoreExportProgress(int Completed, int Total, string Message);
