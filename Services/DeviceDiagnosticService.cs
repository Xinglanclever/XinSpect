using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

/// <summary>
/// 唯讀列舉 present 與 non-present PnP 裝置，保留 Windows problem code、驅動證據與父子拓樸。
/// 所有判定皆由 <see cref="DeviceDiagnosticRules"/> 的純函式完成；服務不產生黑箱健康分。
/// </summary>
public sealed class DeviceDiagnosticService
{
    private readonly IDeviceDiagnosticDataSource _dataSource;

    public DeviceDiagnosticService() : this(new WindowsDeviceDiagnosticDataSource()) { }

    public DeviceDiagnosticService(IDeviceDiagnosticDataSource dataSource)
        => _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public DeviceDiagnosticReport Scan(DateTime? now = null)
        => BuildReport(_dataSource.EnumerateDevices(), now ?? DateTime.Now);

    public Task<DeviceDiagnosticReport> ScanAsync(DateTime? now = null, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Scan(now);
        }, cancellationToken);

    /// <summary>從來源資料正規化、分類，再以 Parent instance ID 建樹；不碰原生介面。</summary>
    public static DeviceDiagnosticReport BuildReport(IEnumerable<DeviceDiagnosticRawDevice> rawDevices, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(rawDevices);

        var nodes = rawDevices.Select(raw => new MutableNode(DeviceDiagnosticRules.Classify(raw, now))).ToList();
        var byId = nodes
            .Where(static n => n.Device.InstanceId != DeviceDiagnosticUnknown.Value)
            .GroupBy(static n => n.Device.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);

        var roots = new List<MutableNode>();
        foreach (var node in nodes)
        {
            string parent = node.Device.ParentInstanceId;
            if (parent != DeviceDiagnosticUnknown.Value
                && !parent.Equals(node.Device.InstanceId, StringComparison.OrdinalIgnoreCase)
                && byId.TryGetValue(parent, out var parentNode))
                parentNode.Children.Add(node);
            else
                roots.Add(node);
        }

        var frozenByNode = new Dictionary<MutableNode, DeviceDiagnosticDevice>();
        DeviceDiagnosticDevice Freeze(MutableNode node, HashSet<MutableNode> path)
        {
            if (frozenByNode.TryGetValue(node, out var frozen)) return frozen;
            if (!path.Add(node)) return node.Device;
            var children = node.Children
                .OrderBy(static n => n.Device.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
                .Select(n => Freeze(n, path))
                .ToArray();
            path.Remove(node);
            frozen = node.Device with { Children = children };
            frozenByNode[node] = frozen;
            return frozen;
        }

        // 先凍結 root，再凍結任何因異常循環而未能成為 root 的節點，確保扁平與樹狀結果共用完整子節點資料。
        var tree = roots
            .OrderBy(static n => n.Device.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .Select(n => Freeze(n, []))
            .ToArray();
        var flat = nodes.Select(n => Freeze(n, [])).ToArray();
        return new DeviceDiagnosticReport(flat, tree);
    }

    private sealed class MutableNode(DeviceDiagnosticDevice device)
    {
        public DeviceDiagnosticDevice Device { get; } = device;
        public List<MutableNode> Children { get; } = [];
    }
}

/// <summary>problem code 說明與 finding 規則；全部為確定性純函式。</summary>
public static class DeviceDiagnosticRules
{
    public const int OldCriticalDriverYears = DriverAuditDecoder.OldYears;

    private static readonly IReadOnlyDictionary<uint, string> ProblemDescriptions = new Dictionary<uint, string>
    {
        [0] = "Windows 未回報此裝置有問題。",
        [1] = "Windows 無法正確設定此裝置。",
        [3] = "此裝置的驅動程式可能損壞，或系統記憶體／其他資源不足。",
        [10] = "此裝置無法啟動。",
        [12] = "此裝置找不到足夠的可用資源。",
        [14] = "Windows 必須重新啟動電腦，才能讓此裝置正常運作。",
        [18] = "需要重新安裝此裝置的驅動程式。",
        [19] = "Windows 無法啟動此硬體裝置，因為其登錄設定不完整或已損壞。",
        [21] = "Windows 正在移除此裝置。",
        [22] = "此裝置已被停用。",
        [24] = "此裝置不存在、無法正常運作，或未安裝全部驅動程式。",
        [28] = "尚未安裝此裝置的驅動程式。",
        [29] = "裝置韌體沒有提供所需資源，因此此裝置已停用。",
        [31] = "此裝置無法正常運作，Windows 無法載入所需的驅動程式。",
        [32] = "此裝置的驅動程式或服務已被另一個驅動程式取代。",
        [33] = "Windows 無法判定此裝置需要哪些資源。",
        [34] = "Windows 無法判定此裝置的設定。",
        [35] = "電腦的系統韌體沒有足夠資訊可正確設定此裝置。",
        [36] = "此裝置要求 PCI 中斷，但已設定為 ISA 中斷，或反之。",
        [37] = "Windows 無法初始化此硬體的驅動程式。",
        [38] = "Windows 無法載入此硬體的驅動程式，因為先前的執行個體仍在記憶體中。",
        [39] = "Windows 無法載入此硬體的驅動程式；驅動程式可能損壞或遺失。",
        [40] = "Windows 無法存取登錄中的服務資訊。",
        [41] = "Windows 已載入驅動程式，但找不到硬體裝置。",
        [42] = "系統中已有重複的裝置在執行。",
        [43] = "Windows 已停止此裝置，因為裝置回報了問題。",
        [44] = "應用程式或服務已關閉此硬體裝置。",
        [45] = "此硬體裝置目前未連接到電腦。",
        [46] = "Windows 無法存取此硬體裝置，因為作業系統正在關機。",
        [47] = "此裝置已準備安全移除，但尚未從電腦移除。",
        [48] = "此裝置的軟體已被禁止啟動，因為已知它會造成 Windows 問題。",
        [49] = "Windows 無法啟動新的硬體裝置，因為系統登錄區太大。",
        [50] = "Windows 無法套用此裝置的全部內容。",
        [51] = "此裝置正在等待另一個裝置或裝置集合啟動。",
        [52] = "Windows 無法驗證此裝置所需驅動程式的數位簽章。",
        [53] = "此裝置已由 Windows 核心偵錯工具保留使用。",
        [54] = "此裝置失敗並正在重設。",
        [56] = "Windows 仍在設定此裝置的類別組態。",
    };

    public static string ExplainProblemCode(uint? code)
        => code is null ? DeviceDiagnosticUnknown.Value
         : ProblemDescriptions.TryGetValue(code.Value, out string? text) ? text
         : $"Windows 回報 problem code {code.Value}；此版本未收錄該碼的說明。";

    public static DeviceDiagnosticDevice Classify(DeviceDiagnosticRawDevice raw, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(raw);
        string cls = DeviceDiagnosticUnknown.Text(raw.DeviceClass);
        DateTime? date = raw.DriverDate?.Date;
        var findings = BuildFindings(raw, cls, date, now.Date);
        return new DeviceDiagnosticDevice
        {
            InstanceId = DeviceDiagnosticUnknown.Text(raw.InstanceId),
            FriendlyName = DeviceDiagnosticUnknown.Text(raw.FriendlyName),
            Description = DeviceDiagnosticUnknown.Text(raw.Description),
            DeviceClass = cls,
            ClassGuid = DeviceDiagnosticUnknown.Text(raw.ClassGuid),
            Manufacturer = DeviceDiagnosticUnknown.Text(raw.Manufacturer),
            Status = DeviceDiagnosticUnknown.Text(raw.Status),
            ProblemCode = raw.ProblemCode,
            ProblemCodeText = raw.ProblemCode?.ToString(CultureInfo.InvariantCulture) ?? DeviceDiagnosticUnknown.Value,
            ProblemExplanation = ExplainProblemCode(raw.ProblemCode),
            ParentInstanceId = DeviceDiagnosticUnknown.Text(raw.ParentInstanceId),
            Service = DeviceDiagnosticUnknown.Text(raw.Service),
            IsPresent = raw.IsPresent,
            DriverVersion = DeviceDiagnosticUnknown.Text(raw.DriverVersion),
            DriverDate = date,
            DriverProvider = DeviceDiagnosticUnknown.Text(raw.DriverProvider),
            InfName = DeviceDiagnosticUnknown.Text(raw.InfName),
            IsSigned = raw.IsSigned,
            Signer = DeviceDiagnosticUnknown.Text(raw.Signer),
            Findings = findings,
        };
    }

    public static IReadOnlyList<DeviceDiagnosticFinding> BuildFindings(
        DeviceDiagnosticRawDevice raw, string? normalizedClass, DateTime? driverDate, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var findings = new List<DeviceDiagnosticFinding>();

        // Ghost 只是當下不在場；仍保留歷史 problem code，但不把「不在場」本身當 finding。
        if (raw.ProblemCode is { } code && code != 0)
        {
            var kind = code switch
            {
                22 => DeviceDiagnosticFindingKind.Disabled,
                28 => DeviceDiagnosticFindingKind.MissingDriver,
                _ => DeviceDiagnosticFindingKind.WindowsProblemCode,
            };
            string summary = code switch
            {
                22 => "裝置已停用",
                28 => "缺少裝置驅動程式",
                _ => $"Windows problem code {code}",
            };
            findings.Add(new(kind, DeviceDiagnosticSeverity.Error, summary, ExplainProblemCode(code)));
        }

        if (raw.IsSigned == false)
            findings.Add(new(DeviceDiagnosticFindingKind.UnsignedDriver, DeviceDiagnosticSeverity.Error,
                "驅動程式未簽章", "驅動來源明確回報 IsSigned = false；這是簽章狀態，不推論來源或是否惡意。"));

        string cls = DeviceDiagnosticUnknown.Text(normalizedClass);
        if (raw.IsSigned != false && driverDate is { } date && DriverAuditDecoder.IsCritical(cls)
            && !DriverAuditDecoder.IsInboxPlaceholder(raw.DriverProvider, date)
            && (now.Date - date.Date).TotalDays >= OldCriticalDriverYears * 365.25)
        {
            findings.Add(new(DeviceDiagnosticFindingKind.OldCriticalDriver, DeviceDiagnosticSeverity.Warning,
                "關鍵類別驅動日期偏舊",
                $"{DriverAuditDecoder.ClassLabel(cls)}驅動日期為 {date:yyyy-MM-dd}，距掃描日已至少 {OldCriticalDriverYears} 年；舊不等於故障。"));
        }

        return findings;
    }
}

/// <summary>Windows 實作：SetupAPI 列舉所有已安裝裝置（含 ghost），WMI 補驅動欄位。</summary>
public sealed class WindowsDeviceDiagnosticDataSource : IDeviceDiagnosticDataSource
{
    public IReadOnlyList<DeviceDiagnosticRawDevice> EnumerateDevices()
    {
        var drivers = LoadDrivers();
        var devices = new List<DeviceDiagnosticRawDevice>();
        nint set = SetupDiGetClassDevs(0, null, 0, DigcfAllClasses);
        if (set == InvalidHandleValue)
            throw new InvalidOperationException($"SetupDiGetClassDevs 失敗，Win32 錯誤 {Marshal.GetLastWin32Error()}。");

        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems) break;
                    throw new InvalidOperationException($"SetupDiEnumDeviceInfo({index}) 失敗，Win32 錯誤 {error}。");
                }
                devices.Add(ReadDevice(set, ref info, drivers));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return devices;
    }

    private static DeviceDiagnosticRawDevice ReadDevice(
        nint set, ref SpDevInfoData info, IReadOnlyDictionary<string, DriverData> drivers)
    {
        string? instanceId = GetInstanceId(set, ref info);
        bool hasStatus = CM_Get_DevNode_Status(out uint status, out uint problem, info.DevInst, 0) == CrSuccess;
        bool? presentProperty = GetDevicePropertyBoolean(set, ref info, DeviceIsPresentKey);
        bool present = presentProperty ?? hasStatus;
        string? classGuid = GetStringProperty(set, ref info, SpdrpClassGuid)
                            ?? (info.ClassGuid == Guid.Empty ? null : info.ClassGuid.ToString("B"));
        string? parent = ParentInstanceId(info.DevInst);
        drivers.TryGetValue(instanceId ?? "", out DriverData? driver);

        return new DeviceDiagnosticRawDevice
        {
            InstanceId = instanceId,
            FriendlyName = GetStringProperty(set, ref info, SpdrpFriendlyName),
            Description = GetStringProperty(set, ref info, SpdrpDeviceDesc),
            DeviceClass = GetStringProperty(set, ref info, SpdrpClass),
            ClassGuid = classGuid,
            Manufacturer = GetStringProperty(set, ref info, SpdrpMfg),
            Status = hasStatus ? StatusText(status, problem, present) : present ? "Present" : "Not present",
            ProblemCode = hasStatus ? problem : null,
            ParentInstanceId = parent,
            Service = GetStringProperty(set, ref info, SpdrpService),
            IsPresent = present,
            DriverVersion = driver?.Version,
            DriverDate = driver?.Date,
            DriverProvider = driver?.Provider,
            InfName = driver?.Inf,
            IsSigned = driver?.Signed,
            Signer = driver?.Signer,
        };
    }

    private static string StatusText(uint status, uint problem, bool present)
    {
        if ((status & DnHasProblem) != 0 || problem != 0) return "Problem";
        if ((status & DnStarted) != 0) return "Started";
        return present ? "Present" : "Not present";
    }

    private sealed record DriverData(string? Version, DateTime? Date, string? Provider,
                                     string? Inf, bool? Signed, string? Signer);

    private static Dictionary<string, DriverData> LoadDrivers()
    {
        var result = new Dictionary<string, DriverData>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT DeviceID, DriverVersion, DriverDate, DriverProviderName, InfName, IsSigned, Signer "
                + "FROM Win32_PnPSignedDriver");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    string? id = WmiString(item, "DeviceID");
                    if (id is not { Length: > 0 }) continue;
                    result.TryAdd(id, new DriverData(
                        WmiString(item, "DriverVersion"),
                        DriverAuditDecoder.ParseCimDate(WmiString(item, "DriverDate")),
                        WmiString(item, "DriverProviderName"),
                        WmiString(item, "InfName"),
                        WmiBool(item, "IsSigned"),
                        WmiString(item, "Signer")));
                }
            }
        }
        catch (Exception ex) { Diag.Swallow("列舉 PnP 驅動詳細資料", ex, "裝置仍列出，驅動欄位顯示 Unknown"); }
        return result;
    }

    private static string? WmiString(ManagementObject item, string name)
    {
        try { return (item[name] as string)?.Trim() is { Length: > 0 } value ? value : null; }
        catch (ManagementException) { return null; }
    }

    private static bool? WmiBool(ManagementObject item, string name)
    {
        try { return item[name] is bool value ? value : null; }
        catch (ManagementException) { return null; }
    }

    private static string? ParentInstanceId(uint devInst)
    {
        if (CM_Get_Parent(out uint parent, devInst, 0) != CrSuccess) return null;
        int length = 0;
        if (CM_Get_Device_ID_Size(out length, parent, 0) != CrSuccess || length < 0) return null;
        var buffer = new StringBuilder(length + 1);
        return CM_Get_Device_ID(parent, buffer, buffer.Capacity, 0) == CrSuccess ? buffer.ToString() : null;
    }

    private static string? GetInstanceId(nint set, ref SpDevInfoData info)
    {
        SetupDiGetDeviceInstanceId(set, ref info, null, 0, out uint required);
        if (required == 0) return null;
        var buffer = new StringBuilder((int)required);
        return SetupDiGetDeviceInstanceId(set, ref info, buffer, required, out _) ? buffer.ToString() : null;
    }

    private static string? GetStringProperty(nint set, ref SpDevInfoData info, uint property)
    {
        SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, null, 0, out uint required);
        if (required < 2) return null;
        byte[] buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out uint type,
                                               buffer, required, out _)) return null;
        return type switch
        {
            RegSz or RegExpandSz => Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim() is { Length: > 0 } s ? s : null,
            RegMultiSz => string.Join("; ", Encoding.Unicode.GetString(buffer).TrimEnd('\0')
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)),
            _ => null,
        };
    }

    private static bool? GetDevicePropertyBoolean(nint set, ref SpDevInfoData info, DevPropKey key)
    {
        var buffer = new byte[4];
        return SetupDiGetDeviceProperty(set, ref info, ref key, out _, buffer, (uint)buffer.Length, out uint required, 0)
               && required >= 1 ? buffer[0] != 0 : null;
    }

    private static readonly DevPropKey DeviceIsPresentKey = new()
    {
        FmtId = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"),
        Pid = 5,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    private static readonly nint InvalidHandleValue = new(-1);
    private const uint DigcfAllClasses = 0x00000004;
    private const int ErrorNoMoreItems = 259;
    private const uint CrSuccess = 0;
    private const uint DnStarted = 0x00000008, DnHasProblem = 0x00000400;
    private const uint SpdrpDeviceDesc = 0x00000000, SpdrpService = 0x00000004,
                       SpdrpClass = 0x00000007, SpdrpClassGuid = 0x00000008,
                       SpdrpMfg = 0x0000000B, SpdrpFriendlyName = 0x0000000C;
    private const uint RegSz = 1, RegExpandSz = 2, RegMultiSz = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(nint classGuid, string? enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(nint deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(nint deviceInfoSet, ref SpDevInfoData deviceInfoData,
        StringBuilder? deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint deviceInfoSet, ref SpDevInfoData deviceInfoData,
        uint property, out uint propertyRegDataType, byte[]? propertyBuffer, uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(nint deviceInfoSet, ref SpDevInfoData deviceInfoData,
        ref DevPropKey propertyKey, out uint propertyType, byte[]? propertyBuffer, uint propertyBufferSize,
        out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_Size(out int idLength, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID(uint devInst, StringBuilder buffer, int bufferLength, uint flags);
}
