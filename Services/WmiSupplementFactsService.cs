using System.Globalization;
using System.Management;
using System.Security;

namespace XinSpect;

internal sealed record WmiClassQueryResult<T>(
    IReadOnlyList<T>? Rows,
    FactAvailability Availability,
    string? Reason)
{
    public static WmiClassQueryResult<T> Success(IReadOnlyList<T> rows) =>
        new(rows, FactAvailability.Present, null);

    public static WmiClassQueryResult<T> Failure(FactAvailability availability, string reason) =>
        new(null, availability, reason);
}

internal sealed record WmiBusRow(uint? BusType);
internal sealed record WmiPortResourceRow(bool? IsAlias);
internal sealed record WmiIrqResourceRow(bool? IsHardware);
internal sealed record WmiProcessorCacheAssociation(string? CacheReference, string? ProcessorReference, uint? BusSpeedMhz);

internal sealed record WmiSupplementSnapshot(
    WmiClassQueryResult<WmiBusRow> Buses,
    WmiClassQueryResult<WmiPortResourceRow> PortResources,
    WmiClassQueryResult<WmiIrqResourceRow> IrqResources,
    WmiClassQueryResult<WmiProcessorCacheAssociation> ProcessorCacheAssociations);

/// <summary>U10-WM-0001/0008/0010/0012: 補讀 root\CIMV2 匯流排與資源關聯；只報 WMI 自述，不代替 PCI/ACPI/核心拓撲。</summary>
public static class WmiSupplementFactsService
{
    private const string Category = "匯流排與資源";
    private const int QueryTimeoutMs = WmiGuard.DefaultTimeoutMs;

    public const string OfficialCoverageKey = "wmi.official.coverage";

    // Microsoft Learn's Computer System Hardware Classes page, grouped into its 9 headings.
    internal static readonly IReadOnlyList<string> OfficialHardwareClasses =
    [
        "Win32_Fan", "Win32_HeatPipe", "Win32_Refrigeration", "Win32_TemperatureProbe",
        "Win32_Keyboard", "Win32_PointingDevice",
        "Win32_AutochkSetting", "Win32_CDROMDrive", "Win32_DiskDrive", "Win32_FloppyDrive",
        "Win32_PhysicalMedia", "Win32_TapeDrive",
        "Win32_1394Controller", "Win32_1394ControllerDevice", "Win32_AllocatedResource",
        "Win32_AssociatedProcessorMemory", "Win32_BaseBoard", "Win32_BIOS", "Win32_Bus",
        "Win32_CacheMemory", "Win32_ControllerHasHub", "Win32_DeviceBus", "Win32_DeviceMemoryAddress",
        "Win32_DeviceSettings", "Win32_DMAChannel", "Win32_FloppyController", "Win32_IDEController",
        "Win32_IDEControllerDevice", "Win32_InfraredDevice", "Win32_IRQResource", "Win32_MemoryArray",
        "Win32_MemoryArrayLocation", "Win32_MemoryDevice", "Win32_MemoryDeviceArray",
        "Win32_MemoryDeviceLocation", "Win32_MotherboardDevice", "Win32_OnBoardDevice", "Win32_ParallelPort",
        "Win32_PCMCIAController", "Win32_PhysicalMemory", "Win32_PhysicalMemoryArray",
        "Win32_PhysicalMemoryLocation", "Win32_PNPAllocatedResource", "Win32_PNPDevice", "Win32_PnPEntity",
        "Win32_PortConnector", "Win32_PortResource", "Win32_Processor", "Win32_SCSIController",
        "Win32_SCSIControllerDevice", "Win32_SerialPort", "Win32_SerialPortConfiguration",
        "Win32_SerialPortSetting", "Win32_SMBIOSMemory", "Win32_SoundDevice", "Win32_SystemBIOS",
        "Win32_SystemDriverPNPEntity", "Win32_SystemEnclosure", "Win32_SystemMemoryResource", "Win32_SystemSlot",
        "Win32_USBController", "Win32_USBControllerDevice", "Win32_USBHub",
        "Win32_NetworkAdapter", "Win32_NetworkAdapterConfiguration", "Win32_NetworkAdapterSetting",
        "Win32_Battery", "Win32_CurrentProbe", "Win32_PortableBattery", "Win32_PowerManagementEvent",
        "Win32_VoltageProbe",
        "Win32_DriverForDevice", "Win32_Printer", "Win32_PrinterConfiguration", "Win32_PrinterDriver",
        "Win32_PrinterDriverDll", "Win32_PrinterSetting", "Win32_PrinterController", "Win32_TCPIPPrinterPort",
        "Win32_PrintJob",
        "Win32_POTSModem", "Win32_POTSModemToSerialPort",
        "Win32_DesktopMonitor", "Win32_DisplayControllerConfiguration", "Win32_VideoController", "Win32_VideoSettings",
    ];

    internal static IReadOnlySet<string> DirectWmiQueryClasses { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Win32_AssociatedProcessorMemory", "Win32_BaseBoard", "Win32_Battery", "Win32_BIOS", "Win32_Bus",
        "Win32_CDROMDrive", "Win32_DesktopMonitor", "Win32_DiskDrive", "Win32_FloppyDrive", "Win32_IRQResource",
        "Win32_NetworkAdapter", "Win32_NetworkAdapterConfiguration", "Win32_PhysicalMemory", "Win32_PnPEntity",
        "Win32_PortResource", "Win32_Printer", "Win32_Processor", "Win32_SCSIController", "Win32_SoundDevice",
        "Win32_SystemEnclosure", "Win32_USBControllerDevice", "Win32_VideoController",
    };

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) => Collect(at, ReadSnapshot);

    internal static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Func<WmiSupplementSnapshot?> probe)
    {
        WmiSupplementSnapshot? snapshot;
        try { snapshot = probe(); }
        catch { snapshot = null; }

        if (snapshot is null)
        {
            const string reason = "WMI 補遺查詢失敗或逾時；讀不到不代表硬體不存在";
            return
            [
                Unavailable("wmi.bus", "WMI 匯流排", at, FactAvailability.ReadError, reason),
                Unavailable("wmi.portres", "WMI I/O 埠資源", at, FactAvailability.ReadError, reason),
                Unavailable("wmi.irqres", "WMI IRQ 資源列", at, FactAvailability.ReadError, reason),
                Unavailable("wmi.cpu.cache.assoc", "WMI 處理器與快取關聯", at, FactAvailability.ReadError, reason),
                OfficialCoverageFact(at),
            ];
        }

        return
        [
            BusFact(at, snapshot.Buses),
            PortResourceFact(at, snapshot.PortResources),
            IrqResourceFact(at, snapshot.IrqResources),
            ProcessorCacheFact(at, snapshot.ProcessorCacheAssociations),
            OfficialCoverageFact(at),
        ];
    }

    private static HardwareFact OfficialCoverageFact(DateTimeOffset at)
    {
        int total = OfficialHardwareClasses.Count;
        int queried = DirectWmiQueryClasses.Count;
        return new HardwareFact(OfficialCoverageKey, "系統與軟體", "Microsoft Learn 硬體類別查詢盤點",
            $"官方頁列 {total} 類；Services/ 有直接 WQL 查詢 {queried} 類，未直接查詢 {total - queried} 類。" +
            "這是查詢字面盤點，不是硬體支援率；未直接查詢不等於類別不存在或硬體不支援。",
            "類別", "Microsoft Learn Computer System Hardware Classes＋服務原始碼 WQL 盤點（測試逐類對帳）",
            FactTrustLevel.Derived, false, at, queried);
    }

    private static WmiSupplementSnapshot ReadSnapshot()
    {
        var buses = ReadClass("Win32_Bus", "SELECT BusType FROM Win32_Bus",
            row => new WmiBusRow(ToUInt32(row["BusType"])), out bool timedOut);

        var ports = timedOut
            ? Skipped<WmiPortResourceRow>()
            : ReadClass("Win32_PortResource", "SELECT Alias FROM Win32_PortResource",
                row => new WmiPortResourceRow(ToBoolean(row["Alias"])), out timedOut);

        var irqs = timedOut
            ? Skipped<WmiIrqResourceRow>()
            : ReadClass("Win32_IRQResource", "SELECT Hardware FROM Win32_IRQResource",
                row => new WmiIrqResourceRow(ToBoolean(row["Hardware"])), out timedOut);

        var associations = timedOut
            ? Skipped<WmiProcessorCacheAssociation>()
            : ReadClass("Win32_AssociatedProcessorMemory",
                "SELECT Antecedent, Dependent, BusSpeed FROM Win32_AssociatedProcessorMemory",
                row => MapProcessorCacheAssociation(row["Antecedent"]?.ToString(),
                    row["Dependent"]?.ToString(), ToUInt32(row["BusSpeed"])), out _);

        return new WmiSupplementSnapshot(buses, ports, irqs, associations);
    }

    private static WmiClassQueryResult<T> ReadClass<T>(string className, string query,
        Func<ManagementBaseObject, T> map, out bool timedOut)
    {
        var (result, didTimeOut) = WmiGuard.Probe(() => ExecuteQuery(className, query, map), QueryTimeoutMs);
        timedOut = didTimeOut;
        return result ?? WmiClassQueryResult<T>.Failure(FactAvailability.ReadError,
            didTimeOut ? $"{className} 查詢超過 {QueryTimeoutMs} ms；為限制啟動延遲，略過後續補遺查詢" : $"{className} 查詢失敗");
    }

    private static WmiClassQueryResult<T> ExecuteQuery<T>(string className, string query,
        Func<ManagementBaseObject, T> map)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            using var rows = searcher.Get();
            var values = new List<T>();
            foreach (ManagementBaseObject row in rows)
            {
                using (row) values.Add(map(row));
            }
            return WmiClassQueryResult<T>.Success(values);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidClass)
        {
            return WmiClassQueryResult<T>.Failure(FactAvailability.NotSupported, $"本機 WMI 未提供 {className} 類別");
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return WmiClassQueryResult<T>.Failure(FactAvailability.InsufficientPrivilege, $"存取 {className} 被拒");
        }
        catch (UnauthorizedAccessException)
        {
            return WmiClassQueryResult<T>.Failure(FactAvailability.InsufficientPrivilege, $"存取 {className} 被拒");
        }
        catch (SecurityException)
        {
            return WmiClassQueryResult<T>.Failure(FactAvailability.InsufficientPrivilege, $"存取 {className} 被拒");
        }
        catch
        {
            return WmiClassQueryResult<T>.Failure(FactAvailability.ReadError, $"{className} 查詢失敗");
        }
    }

    private static WmiClassQueryResult<T> Skipped<T>() =>
        WmiClassQueryResult<T>.Failure(FactAvailability.ReadError,
            "前一個 WMI 類別查詢逾時；為限制啟動延遲，略過後續補遺查詢");

    private static HardwareFact BusFact(DateTimeOffset at, WmiClassQueryResult<WmiBusRow> result)
    {
        if (result.Rows is null) return Unavailable("wmi.bus", "WMI 匯流排", at, result.Availability, result.Reason);
        var rows = result.Rows;
        var groups = rows.Where(x => x.BusType.HasValue).GroupBy(x => x.BusType!.Value)
            .OrderBy(x => x.Key).Select(x => $"碼 {x.Key.ToString(CultureInfo.InvariantCulture)} ×{x.Count()}").ToList();
        int missing = rows.Count(x => !x.BusType.HasValue);
        string detail = rows.Count == 0
            ? "WMI 查詢成功但回傳 0 筆；不等於底層沒有匯流排。"
            : $"類型碼分布：{(groups.Count == 0 ? "未回傳" : string.Join("、", groups))}" +
              (missing > 0 ? $"；BusType 缺值 {missing} 筆" : "");
        return Present("wmi.bus", "WMI 匯流排", at,
            $"Win32_Bus 回報 {rows.Count} 筆。{detail} 類型碼保持原值，未收錄碼不猜名稱。", rows.Count);
    }

    private static HardwareFact PortResourceFact(DateTimeOffset at, WmiClassQueryResult<WmiPortResourceRow> result)
    {
        if (result.Rows is null) return Unavailable("wmi.portres", "WMI I/O 埠資源", at, result.Availability, result.Reason);
        var rows = result.Rows;
        int aliases = rows.Count(x => x.IsAlias == true);
        int baseRows = rows.Count(x => x.IsAlias == false);
        int unknown = rows.Count - aliases - baseRows;
        string detail = rows.Count == 0
            ? "WMI 查詢成功但回傳 0 筆；不等於沒有 I/O 埠。"
            : $"Alias 標記：基底列 {baseRows}、別名列 {aliases}、未回報 {unknown}。";
        return Present("wmi.portres", "WMI I/O 埠資源", at,
            $"Win32_PortResource 回報 {rows.Count} 筆資源列。{detail} 不輸出地址；列數不等於去重後實體埠數。", rows.Count);
    }

    private static HardwareFact IrqResourceFact(DateTimeOffset at, WmiClassQueryResult<WmiIrqResourceRow> result)
    {
        if (result.Rows is null) return Unavailable("wmi.irqres", "WMI IRQ 資源列", at, result.Availability, result.Reason);
        var rows = result.Rows;
        int hardware = rows.Count(x => x.IsHardware == true);
        int software = rows.Count(x => x.IsHardware == false);
        int unknown = rows.Count - hardware - software;
        string detail = rows.Count == 0
            ? "WMI 查詢成功但回傳 0 筆；不等於沒有中斷資源。"
            : $"Hardware 標記：true {hardware}、false {software}、未回報 {unknown}。";
        return Present("wmi.irqres", "WMI IRQ 資源列", at,
            $"Win32_IRQResource 回報 {rows.Count} 筆資源列。{detail} 這是 WMI 資源列統計，不是唯一 IRQ 線或處理器向量數。", rows.Count);
    }

    private static HardwareFact ProcessorCacheFact(DateTimeOffset at,
        WmiClassQueryResult<WmiProcessorCacheAssociation> result)
    {
        if (result.Rows is null)
            return Unavailable("wmi.cpu.cache.assoc", "WMI 處理器與快取關聯", at, result.Availability, result.Reason);

        var rows = result.Rows;
        int complete = rows.Count(x => !string.IsNullOrWhiteSpace(x.CacheReference)
                                     && !string.IsNullOrWhiteSpace(x.ProcessorReference));
        int processors = rows.Where(x => !string.IsNullOrWhiteSpace(x.ProcessorReference))
            .Select(x => x.ProcessorReference).Distinct(StringComparer.Ordinal).Count();
        int caches = rows.Where(x => !string.IsNullOrWhiteSpace(x.CacheReference))
            .Select(x => x.CacheReference).Distinct(StringComparer.Ordinal).Count();
        var speeds = rows.Where(x => x.BusSpeedMhz.HasValue).GroupBy(x => x.BusSpeedMhz!.Value)
            .OrderBy(x => x.Key).Select(x => $"{x.Key.ToString(CultureInfo.InvariantCulture)} MHz ×{x.Count()}").ToList();
        int speedMissing = rows.Count - rows.Count(x => x.BusSpeedMhz.HasValue);
        string detail = rows.Count == 0
            ? "WMI 查詢成功但回傳 0 筆關聯；不等於處理器沒有快取。"
            : $"雙端點完整 {complete}/{rows.Count}；不同處理器參照 {processors}、快取參照 {caches}；" +
              $"BusSpeed 有值 {rows.Count - speedMissing}/{rows.Count}" +
              (speeds.Count > 0 ? $"（{string.Join("、", speeds)}）" : "");
        return Present("wmi.cpu.cache.assoc", "WMI 處理器與快取關聯",
            at, $"Win32_AssociatedProcessorMemory 回報 {rows.Count} 筆處理器↔快取關聯。{detail} " +
                "參照數不是快取層級／容量；BusSpeed 是該類別自述的處理器與記憶體間匯流排速度，不是 CPU 頻率。",
            rows.Count);
    }

    private static HardwareFact Present(string key, string name, DateTimeOffset at, string value, int count) =>
        new(key, Category, name, value, "筆", "WMI root\\CIMV2（Win32 provider，唯讀）",
            FactTrustLevel.Reported, false, at, count);

    private static HardwareFact Unavailable(string key, string name, DateTimeOffset at,
        FactAvailability availability, string? reason) =>
        new(key, Category, name, "", "", "WMI root\\CIMV2（Win32 provider，唯讀）",
            FactTrustLevel.Unknown, false, at, null, availability, reason);

    private static uint? ToUInt32(object? value)
    {
        if (value is null) return null;
        try { return Convert.ToUInt32(value, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private static bool? ToBoolean(object? value)
    {
        if (value is bool b) return b;
        if (value is null) return null;
        try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    internal static WmiProcessorCacheAssociation MapProcessorCacheAssociation(
        string? antecedentCacheReference, string? dependentProcessorReference, uint? busSpeedMhz) =>
        new(antecedentCacheReference, dependentProcessorReference, busSpeedMhz);
}
