namespace XinSpect;

/// <summary>Windows 已配置給 PCI 裝置的資源類型。</summary>
public enum PciResourceKind
{
    Memory,
    IoPort,
    Irq,
    Dma,
}

/// <summary>
/// cfgmgr32 的 <c>ALLOC_LOG_CONF</c> 中一筆已配置資源。
/// 記憶體項目是 Windows 配置資源（可能包含 BAR 或橋接視窗），不是 VRAM 容量。
/// </summary>
public sealed record PciAssignedResource(
    PciResourceKind Kind,
    ulong Start,
    ulong End,
    string Source,
    uint Flags = 0)
{
    public ulong Length => End >= Start ? End - Start + 1 : 0;
    public string Label => Kind switch
    {
        PciResourceKind.Memory => "Windows 配置資源（記憶體範圍；不是 VRAM）",
        PciResourceKind.IoPort => "Windows 配置資源（I/O 連接埠）",
        PciResourceKind.Irq => "Windows 配置資源（IRQ）",
        PciResourceKind.Dma => "Windows 配置資源（DMA 通道）",
        _ => "Windows 配置資源",
    };
}

/// <summary>從 PCI hardware ID 可靠解出的識別欄位；缺少的欄位維持 null。</summary>
public sealed record PciHardwareIdentity(
    ushort VendorId,
    ushort DeviceId,
    uint? SubsystemId,
    byte? RevisionId,
    string HardwareId)
{
    public ushort? SubsystemVendorId => SubsystemId is uint value ? (ushort)(value & 0xFFFF) : null;
    public ushort? SubsystemDeviceId => SubsystemId is uint value ? (ushort)(value >> 16) : null;
}

/// <summary>SetupAPI/cfgmgr32 回報的一個 present PCI 裝置快照。</summary>
public sealed class PciDeviceSnapshot
{
    public required string InstanceId { get; init; }
    public string Description { get; init; } = PciResourceAudit.Unavailable;
    public string DeviceClass { get; init; } = PciResourceAudit.Unavailable;
    public IReadOnlyList<string> HardwareIds { get; init; } = [];
    public string ParentInstanceId { get; init; } = PciResourceAudit.Unavailable;
    public uint? DevNodeStatus { get; init; }
    public uint? ProblemCode { get; init; }
    public string Service { get; init; } = PciResourceAudit.Unavailable;
    public string Driver { get; init; } = PciResourceAudit.Unavailable;
    public string InfName { get; init; } = PciResourceAudit.Unavailable;
    public IReadOnlyList<PciAssignedResource> Resources { get; init; } = [];
}

/// <summary>稽核後的裝置；保留原始狀態碼，並附上可重現的拓撲與客觀 findings。</summary>
public sealed class PciDeviceAudit
{
    public required PciDeviceSnapshot Device { get; init; }
    public PciHardwareIdentity? Identity { get; init; }
    public string? ParentPciInstanceId { get; init; }
    public IReadOnlyList<string> ChildPciInstanceIds { get; init; } = [];
    public IReadOnlyList<string> Findings { get; init; } = [];

    public string VendorId => Identity is null ? PciResourceAudit.Unavailable : $"{Identity.VendorId:X4}";
    public string DeviceId => Identity is null ? PciResourceAudit.Unavailable : $"{Identity.DeviceId:X4}";
    public string SubsystemId => Identity?.SubsystemId is uint value ? $"{value:X8}" : PciResourceAudit.Unavailable;
    public string RevisionId => Identity?.RevisionId is byte value ? $"{value:X2}" : PciResourceAudit.Unavailable;
}

/// <summary>一次唯讀 PCI 資源與裝置狀態稽核的結果。</summary>
public sealed class PciResourceAuditReport
{
    public IReadOnlyList<PciDeviceAudit> Devices { get; init; } = [];
    public IReadOnlyList<string> Findings { get; init; } = [];
}

/// <summary>
/// 原生邊界。實作只能列舉 present PCI 裝置並讀 SetupAPI/cfgmgr32 資料；
/// 測試可注入純 managed fake，避免 CI 呼叫 Windows 原生 API。
/// </summary>
public interface IPciResourceNative
{
    IReadOnlyList<PciDeviceSnapshot> EnumeratePresentPciDevices();
}
