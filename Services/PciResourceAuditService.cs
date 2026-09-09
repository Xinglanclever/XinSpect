using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

/// <summary>
/// PCI/PCIe 資源與裝置狀態稽核。只讀取 present PCI devnodes、SetupAPI 屬性及
/// cfgmgr32 <c>ALLOC_LOG_CONF</c>；不讀 MMIO、不寫設定空間，也不推論 BER、AER 或鏈路訓練歷史。
/// </summary>
public sealed class PciResourceAuditService
{
    private readonly IPciResourceNative _native;

    public PciResourceAuditService() : this(new WindowsPciResourceNative()) { }

    public PciResourceAuditService(IPciResourceNative native)
        => _native = native ?? throw new ArgumentNullException(nameof(native));

    public PciResourceAuditReport Audit()
        => PciResourceAudit.Analyze(_native.EnumeratePresentPciDevices());
}

/// <summary>PCI hardware ID 解析、拓撲建立及 finding 判定；全為純 managed，供 CI 測試。</summary>
public static class PciResourceAudit
{
    public const string Unavailable = "unavailable";

    private const uint DnStarted = 0x00000008;
    private const uint DnHasProblem = 0x00000400;
    private const uint CmProbBootConfigConflict = 0x06;
    private const uint CmProbNormalConflict = 0x0C;
    private const uint CmProbCantShareIrq = 0x1E;

    /// <summary>依序嘗試 hardware IDs，僅接受完整的固定寬度十六進位欄位。</summary>
    public static PciHardwareIdentity? ParseHardwareIds(IEnumerable<string>? hardwareIds)
    {
        if (hardwareIds is null) return null;
        foreach (string? raw in hardwareIds)
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                !raw.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryHexField(raw, "VEN_", 4, out uint ven) ||
                !TryHexField(raw, "DEV_", 4, out uint dev)) continue;

            uint? subsys = TryHexField(raw, "SUBSYS_", 8, out uint sub) ? sub : null;
            byte? revision = TryHexField(raw, "REV_", 2, out uint rev) ? (byte)rev : null;
            return new PciHardwareIdentity((ushort)ven, (ushort)dev, subsys, revision, raw.Trim());
        }
        return null;
    }

    public static PciResourceAuditReport Analyze(IEnumerable<PciDeviceSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var devices = snapshots
            .Where(d => d is not null && !string.IsNullOrWhiteSpace(d.InstanceId))
            .GroupBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byId = devices.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);
        var children = devices.ToDictionary(d => d.InstanceId,
            _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            if (byId.ContainsKey(device.ParentInstanceId))
                children[device.ParentInstanceId].Add(device.InstanceId);
        }

        var overlaps = FindRangeConflicts(devices);
        var audits = new List<PciDeviceAudit>(devices.Count);
        foreach (var device in devices)
        {
            var findings = new List<string>();
            if (device.ProblemCode is uint problem && problem != 0)
                findings.Add($"Windows problem code {problem} ({ProblemName(problem)})." );
            else if (device.ProblemCode is null && device.DevNodeStatus is uint status && (status & DnHasProblem) != 0)
                findings.Add("Windows devnode status has DN_HAS_PROBLEM, but the problem code was unavailable.");

            if (device.DevNodeStatus is uint nodeStatus && (nodeStatus & DnStarted) == 0)
                findings.Add("Windows devnode status does not include DN_STARTED.");

            if (overlaps.TryGetValue(device.InstanceId, out var conflicts))
                findings.AddRange(conflicts.OrderBy(x => x, StringComparer.Ordinal));

            audits.Add(new PciDeviceAudit
            {
                Device = device,
                Identity = ParseHardwareIds(device.HardwareIds),
                ParentPciInstanceId = byId.ContainsKey(device.ParentInstanceId)
                    ? device.ParentInstanceId : null,
                ChildPciInstanceIds = children[device.InstanceId]
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                Findings = findings,
            });
        }

        var reportFindings = new List<string>();
        int problems = audits.Count(a => a.Device.ProblemCode is > 0);
        int conflictDevices = overlaps.Count;
        if (devices.Count == 0)
            reportFindings.Add("No present PCI devices were returned; this is unavailable data, not proof that PCI devices are absent.");
        else
            reportFindings.Add($"Enumerated {devices.Count} present PCI devices; {problems} have a Windows problem indication and {conflictDevices} participate in an overlapping memory/I/O allocation.");

        return new PciResourceAuditReport { Devices = audits, Findings = reportFindings };
    }

    private static Dictionary<string, List<string>> FindRangeConflicts(IReadOnlyList<PciDeviceSnapshot> devices)
    {
        var byId = devices.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var ranges = devices.SelectMany(d => d.Resources
                .Where(r => r.Kind is PciResourceKind.Memory or PciResourceKind.IoPort && r.Length > 0)
                .Select(r => (Device: d, Resource: r)))
            .OrderBy(x => x.Resource.Kind).ThenBy(x => x.Resource.Start).ToList();

        for (int i = 0; i < ranges.Count; i++)
        {
            for (int j = i + 1; j < ranges.Count; j++)
            {
                var a = ranges[i]; var b = ranges[j];
                if (a.Resource.Kind != b.Resource.Kind) break;
                if (b.Resource.Start > a.Resource.End) break;
                if (a.Device.InstanceId.Equals(b.Device.InstanceId, StringComparison.OrdinalIgnoreCase)) continue;

                ulong start = Math.Max(a.Resource.Start, b.Resource.Start);
                ulong end = Math.Min(a.Resource.End, b.Resource.End);
                if (end < start) continue;
                if (IsAncestorRelated(a.Device, b.Device) && IsContainment(a.Resource, b.Resource)) continue;
                Add(a.Device.InstanceId, ConflictText(a.Resource.Kind, b.Device.InstanceId, start, end));
                Add(b.Device.InstanceId, ConflictText(b.Resource.Kind, a.Device.InstanceId, start, end));
            }
        }
        return result;

        void Add(string id, string finding)
        {
            if (!result.TryGetValue(id, out var list)) result[id] = list = [];
            if (!list.Contains(finding, StringComparer.Ordinal)) list.Add(finding);
        }
        bool IsAncestorRelated(PciDeviceSnapshot a, PciDeviceSnapshot b)
            => IsAncestor(a.InstanceId, b) || IsAncestor(b.InstanceId, a);

        bool IsAncestor(string candidateId, PciDeviceSnapshot child)
        {
            string parent = child.ParentInstanceId;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (seen.Add(parent) && byId.TryGetValue(parent, out var node))
            {
                if (string.Equals(candidateId, parent, StringComparison.OrdinalIgnoreCase)) return true;
                parent = node.ParentInstanceId;
            }
            return false;
        }
    }

    private static bool IsContainment(PciAssignedResource a, PciAssignedResource b)
        => a.Kind == b.Kind && ((a.Start <= b.Start && a.End >= b.End) || (b.Start <= a.Start && b.End >= a.End));

    private static string ConflictText(PciResourceKind kind, string peer, ulong start, ulong end)
        => $"Overlapping Windows-configured {kind} range 0x{start:X}-0x{end:X} with {peer}; overlap is reported objectively and may be intentional sharing/bridge decoding.";

    private static string ProblemName(uint code) => code switch
    {
        CmProbBootConfigConflict => "CM_PROB_BOOT_CONFIG_CONFLICT",
        CmProbNormalConflict => "CM_PROB_NORMAL_CONFLICT",
        CmProbCantShareIrq => "CM_PROB_CANT_SHARE_IRQ",
        _ => "see Windows Device Manager problem codes",
    };

    private static bool TryHexField(string text, string marker, int digits, out uint value)
    {
        value = 0;
        int index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;
        int start = index + marker.Length;
        if (start + digits > text.Length) return false;
        ReadOnlySpan<char> span = text.AsSpan(start, digits);
        if (!uint.TryParse(span, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)) return false;
        int after = start + digits;
        return after == text.Length || text[after] is '&' or '\\' or '\0';
    }
}

internal sealed class WindowsPciResourceNative : IPciResourceNative
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint CrSuccess = 0;
    private const uint AllocLogConf = 2;
    private const uint ResTypeAll = 0;
    private const uint ResTypeMem = 1;
    private const uint ResTypeIo = 2;
    private const uint ResTypeDma = 3;
    private const uint ResTypeIrq = 4;
    private const uint ResTypeMemLarge = 7;

    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpHardwareId = 0x00000001;
    private const uint SpdrpService = 0x00000004;
    private const uint SpdrpClass = 0x00000007;
    private const uint SpdrpDriver = 0x00000009;

    public IReadOnlyList<PciDeviceSnapshot> EnumeratePresentPciDevices()
    {
        var rows = new List<PciDeviceSnapshot>();
        nint set = SetupDiGetClassDevs(nint.Zero, "PCI", 0, DigcfPresent | DigcfAllClasses);
        if (set == -1 || set == 0)
            throw new InvalidOperationException($"SetupDiGetClassDevs(PCI) failed: Win32 {Marshal.GetLastWin32Error()}.");
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new InvalidOperationException($"SetupDiEnumDeviceInfo failed: Win32 {error}.");
                }
                string instance = DeviceId(info.DevInst);
                if (!instance.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
                var (status, problem) = DevNodeStatus(info.DevInst);
                rows.Add(new PciDeviceSnapshot
                {
                    InstanceId = instance,
                    Description = StringProperty(set, ref info, SpdrpDeviceDesc),
                    DeviceClass = StringProperty(set, ref info, SpdrpClass),
                    HardwareIds = MultiStringProperty(set, ref info, SpdrpHardwareId),
                    ParentInstanceId = ParentId(info.DevInst),
                    DevNodeStatus = status,
                    ProblemCode = problem,
                    Service = StringProperty(set, ref info, SpdrpService),
                    Driver = StringProperty(set, ref info, SpdrpDriver),
                    InfName = DevicePropertyString(set, ref info, DriverInfPathKey),
                    Resources = AssignedResources(info.DevInst),
                });
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return rows;
    }

    private static (uint? Status, uint? Problem) DevNodeStatus(uint devInst)
        => CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == CrSuccess
            ? (status, problem) : (null, null);

    private static string ParentId(uint devInst)
        => CM_Get_Parent(out uint parent, devInst, 0) == CrSuccess
            ? DeviceId(parent) : PciResourceAudit.Unavailable;

    private static string DeviceId(uint devInst)
    {
        if (CM_Get_Device_ID_Size(out uint length, devInst, 0) != CrSuccess)
            return PciResourceAudit.Unavailable;
        var buffer = new StringBuilder(checked((int)length + 1));
        return CM_Get_Device_IDW(devInst, buffer, length + 1, 0) == CrSuccess
            ? Available(buffer.ToString()) : PciResourceAudit.Unavailable;
    }

    private static string StringProperty(nint set, ref SpDevInfoData info, uint property)
    {
        byte[]? data = RegistryProperty(set, ref info, property);
        if (data is null) return PciResourceAudit.Unavailable;
        return Available(Encoding.Unicode.GetString(data).TrimEnd('\0'));
    }

    private static IReadOnlyList<string> MultiStringProperty(nint set, ref SpDevInfoData info, uint property)
    {
        byte[]? data = RegistryProperty(set, ref info, property);
        if (data is null) return [];
        return Encoding.Unicode.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static byte[]? RegistryProperty(nint set, ref SpDevInfoData info, uint property)
    {
        SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, null, 0, out uint needed);
        if (needed == 0) return null;
        var data = new byte[needed];
        return SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, data, needed, out _)
            ? data : null;
    }

    private static string DevicePropertyString(nint set, ref SpDevInfoData info, DevPropKey key)
    {
        SetupDiGetDeviceProperty(set, ref info, ref key, out uint type, null, 0, out uint needed, 0);
        if (needed == 0) return PciResourceAudit.Unavailable;
        var data = new byte[needed];
        if (!SetupDiGetDeviceProperty(set, ref info, ref key, out type, data, needed, out _, 0))
            return PciResourceAudit.Unavailable;
        return Available(Encoding.Unicode.GetString(data).TrimEnd('\0'));
    }

    private static string Available(string? value)
        => string.IsNullOrWhiteSpace(value) ? PciResourceAudit.Unavailable : value.Trim();

    private static IReadOnlyList<PciAssignedResource> AssignedResources(uint devInst)
    {
        var resources = new List<PciAssignedResource>();
        if (CM_Get_First_Log_Conf(out nint logConf, devInst, AllocLogConf) != CrSuccess) return resources;
        try
        {
            nint cursor = logConf;
            while (CM_Get_Next_Res_Des(out nint descriptor, cursor, ResTypeAll,
                       out uint resourceId, 0) == CrSuccess)
            {
                if (cursor != logConf) CM_Free_Res_Des_Handle(cursor);
                cursor = descriptor;
                if (CM_Get_Res_Des_Data_Size(out uint size, descriptor, 0) != CrSuccess || size == 0) continue;
                var data = new byte[size];
                if (CM_Get_Res_Des_Data(descriptor, data, size, 0) != CrSuccess) continue;
                PciAssignedResource? resource = DecodeResource(resourceId, data);
                if (resource is not null) resources.Add(resource);
            }
            if (cursor != logConf) CM_Free_Res_Des_Handle(cursor);
        }
        finally { CM_Free_Log_Conf_Handle(logConf); }
        return resources.OrderBy(r => r.Kind).ThenBy(r => r.Start).ToArray();
    }

    internal static PciAssignedResource? DecodeResource(uint resourceId, ReadOnlySpan<byte> data)
    {
        const string source = "cfgmgr32 ALLOC_LOG_CONF (Windows configured resource)";
        return resourceId switch
        {
            ResTypeMem or ResTypeMemLarge when data.Length >= 32 =>
                Range(PciResourceKind.Memory, U64(data, 8), U64(data, 16), source, U32(data, 24)),
            ResTypeIo when data.Length >= 28 =>
                Range(PciResourceKind.IoPort, U64(data, 8), U64(data, 16), source, U32(data, 24)),
            ResTypeIrq when data.Length >= 24 =>
                Range(PciResourceKind.Irq, U32(data, 12), U32(data, 12), source, U16(data, 8)),
            ResTypeDma when data.Length >= 16 =>
                Range(PciResourceKind.Dma, U32(data, 12), U32(data, 12), source, U32(data, 8)),
            _ => null,
        };
    }

    private static PciAssignedResource? Range(PciResourceKind kind, ulong start, ulong end,
                                               string source, uint flags)
        => end >= start ? new PciAssignedResource(kind, start, end, source, flags) : null;

    private static ushort U16(ReadOnlySpan<byte> data, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static ulong U64(ReadOnlySpan<byte> data, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);

    private static readonly DevPropKey DriverInfPathKey = new()
    {
        FmtId = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"),
        Pid = 5,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(nint classGuid, string? enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint memberIndex, ref SpDevInfoData data);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint set, ref SpDevInfoData data, uint property,
        out uint propertyRegDataType, byte[]? buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(nint set, ref SpDevInfoData data, ref DevPropKey propertyKey,
        out uint propertyType, byte[]? buffer, uint bufferSize, out uint requiredSize, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_Size(out uint length, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint devInst, StringBuilder buffer, uint bufferLength, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_First_Log_Conf(out nint logConf, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Free_Log_Conf_Handle(nint logConf);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Next_Res_Des(out nint nextResDes, nint resDes, uint forResource,
        out uint resourceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Free_Res_Des_Handle(nint resDes);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Res_Des_Data_Size(out uint size, nint resDes, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Res_Des_Data(nint resDes, byte[] buffer, uint bufferLength, uint flags);
}
