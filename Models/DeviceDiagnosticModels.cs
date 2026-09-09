namespace XinSpect;

/// <summary>裝置與驅動診斷中，任何來源沒有回報的文字欄位都使用此值。</summary>
public static class DeviceDiagnosticUnknown
{
    public const string Value = "Unknown";

    public static string Text(string? value)
        => string.IsNullOrWhiteSpace(value) ? Value : value.Trim();
}

/// <summary>原生／WMI 邊界回傳的 PnP 裝置原始資料；nullable 代表來源沒有回報。</summary>
public sealed record DeviceDiagnosticRawDevice
{
    public string? InstanceId { get; init; }
    public string? FriendlyName { get; init; }
    public string? Description { get; init; }
    public string? DeviceClass { get; init; }
    public string? ClassGuid { get; init; }
    public string? Manufacturer { get; init; }
    public string? Status { get; init; }
    public uint? ProblemCode { get; init; }
    public string? ParentInstanceId { get; init; }
    public string? Service { get; init; }
    public bool IsPresent { get; init; }
    public string? DriverVersion { get; init; }
    public DateTime? DriverDate { get; init; }
    public string? DriverProvider { get; init; }
    public string? InfName { get; init; }
    public bool? IsSigned { get; init; }
    public string? Signer { get; init; }
}

/// <summary>可展示、可測試的單一 PnP 裝置診斷結果。</summary>
public sealed record DeviceDiagnosticDevice
{
    public string InstanceId { get; init; } = DeviceDiagnosticUnknown.Value;
    public string FriendlyName { get; init; } = DeviceDiagnosticUnknown.Value;
    public string Description { get; init; } = DeviceDiagnosticUnknown.Value;
    public string DeviceClass { get; init; } = DeviceDiagnosticUnknown.Value;
    public string ClassGuid { get; init; } = DeviceDiagnosticUnknown.Value;
    public string Manufacturer { get; init; } = DeviceDiagnosticUnknown.Value;
    public string Status { get; init; } = DeviceDiagnosticUnknown.Value;
    public uint? ProblemCode { get; init; }
    public string ProblemCodeText { get; init; } = DeviceDiagnosticUnknown.Value;
    public string ProblemExplanation { get; init; } = DeviceDiagnosticUnknown.Value;
    public string ParentInstanceId { get; init; } = DeviceDiagnosticUnknown.Value;
    public string Service { get; init; } = DeviceDiagnosticUnknown.Value;
    public bool IsPresent { get; init; }
    public bool IsGhost => !IsPresent;
    public string PresenceText => IsPresent ? "目前在場" : "目前未連接/不在場";
    public string DriverVersion { get; init; } = DeviceDiagnosticUnknown.Value;
    public DateTime? DriverDate { get; init; }
    public string DriverDateText => DriverDate?.ToString("yyyy-MM-dd") ?? DeviceDiagnosticUnknown.Value;
    public string DriverProvider { get; init; } = DeviceDiagnosticUnknown.Value;
    public string InfName { get; init; } = DeviceDiagnosticUnknown.Value;
    public bool? IsSigned { get; init; }
    public string SignedText => IsSigned switch { true => "已簽章", false => "未簽章", _ => DeviceDiagnosticUnknown.Value };
    public string Signer { get; init; } = DeviceDiagnosticUnknown.Value;
    public IReadOnlyList<DeviceDiagnosticFinding> Findings { get; init; } = [];
    public IReadOnlyList<DeviceDiagnosticDevice> Children { get; init; } = [];
}

public enum DeviceDiagnosticFindingKind
{
    WindowsProblemCode,
    Disabled,
    MissingDriver,
    UnsignedDriver,
    OldCriticalDriver,
}

public enum DeviceDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>只描述資料能直接證明的事實，不是健康分數或故障機率。</summary>
public sealed record DeviceDiagnosticFinding(
    DeviceDiagnosticFindingKind Kind,
    DeviceDiagnosticSeverity Severity,
    string Summary,
    string Evidence);

/// <summary>一次裝置列舉的扁平清單與依 Parent instance ID 建出的樹。</summary>
public sealed record DeviceDiagnosticReport(
    IReadOnlyList<DeviceDiagnosticDevice> Devices,
    IReadOnlyList<DeviceDiagnosticDevice> Roots)
{
    public int PresentCount => Devices.Count(static d => d.IsPresent);
    public int GhostCount => Devices.Count(static d => d.IsGhost);
    public int FindingCount => Devices.Sum(static d => d.Findings.Count);
}

/// <summary>隔開 SetupAPI／ConfigManager／WMI，測試可注入固定資料。</summary>
public interface IDeviceDiagnosticDataSource
{
    IReadOnlyList<DeviceDiagnosticRawDevice> EnumerateDevices();
}
