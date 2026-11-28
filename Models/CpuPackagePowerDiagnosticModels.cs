using System.Text.Json;
using System.Text.Json.Serialization;

namespace XinSpect;

public sealed record CpuPackagePowerDiagnosticSource(string BackendId, string BackendName,
    string TrustTier, string HostMode, string LeaseIdentity, string ApplicationVersion,
    string? TelemetryLibraryVersion = null, string? DeclaredModuleSha256 = null);

public sealed record CpuPackagePowerDiagnosticPlatform(string Vendor, int Family, int Model,
    int Stepping, bool Hypervisor, uint? CpuidPackageId, int WindowsPackageCount,
    ProcessorRef Processor, string TopologySource);

public sealed record CpuPackagePowerDiagnosticRead(uint Msr, ulong? Raw, long BeforeQpc,
    long AfterQpc, FactAvailability Availability, string Reason)
{
    public string AddressHex => $"0x{Msr:X}";
    public string? RawHex => Raw is ulong raw ? $"0x{raw:X16}" : null;
}

public sealed record CpuPackagePowerDiagnosticInterval(int FromSample, uint Modulo32Counts,
    double Seconds, double JoulesPerCount, double? UnverifiedWatts,
    double? OneCountResolutionWatts, bool WrapOrReset, string Limitation);

public sealed record CpuPackagePowerDiagnosticSample(int Index, DateTimeOffset CapturedAtUtc,
    CpuPackagePowerDiagnosticRead Units, CpuPackagePowerDiagnosticRead? Energy,
    CpuPackagePowerDiagnosticInterval? Interval, string State);

/// <summary>Diagnostic evidence only. UnverifiedWatts never becomes analytical/validated CPU power.</summary>
public sealed record CpuPackagePowerDiagnosticResult(DateTimeOffset StartedAtUtc, DateTimeOffset FinishedAtUtc,
    FactAvailability Availability, string Reason, bool Cancelled, bool PolicyStopped,
    long QpcFrequency, CpuPackagePowerDiagnosticPlatform? Platform,
    CpuPackagePowerDiagnosticSource? Source, IReadOnlyList<CpuPackagePowerDiagnosticSample> Samples)
{
    public int SchemaVersion => 1;
    public string Trust => "Derived / unverified; not a calibrated power measurement";
    public string Specification => CpuPackagePowerDiagnosticService.Specification;
    public string SourceUpdateTime => "Unknown; QPC brackets this application's synchronous reads, not the counter's internal update";
    public string Limitations => "僅 Intel family 6/model 0x55、裸機、Windows 單一封裝。只借用既有通道；不載入驅動、不施加負載。"
        + "RAPL 與 LHM 可能共用計數器，並非獨立驗證。計數解析度不是準確度；重設與回繞可能無法區分，"
        + "多次回繞無法由兩個端點排除。此結果不修改能量計信任，也不確認原 1.3 W 顯示的原因。";

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    });
}
