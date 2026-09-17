using System.Collections.ObjectModel;
using System.Management;

namespace XinSpect;

/// <summary>
/// PCIe 分析服務：延伸既有 PcieLinkService 資料，提供頻寬計算、通道分配圖與世代參考。
/// </summary>
public sealed class PcieAnalysisService : ObservableObject
{
    // ---- PCIe 世代參考 ----
    public static readonly IReadOnlyList<PcieGenReference> GenReferences =
    [
        new(1, "Gen1", 2.5,  8, 10, "8b/10b"),
        new(2, "Gen2", 5.0,  8, 10, "8b/10b"),
        new(3, "Gen3", 8.0,  128, 130, "128b/130b"),
        new(4, "Gen4", 16.0, 128, 130, "128b/130b"),
        new(5, "Gen5", 32.0, 128, 130, "128b/130b"),
        new(6, "Gen6", 64.0, 128, 130, "128b/130b (PAM4)"),
    ];

    private ObservableCollection<PcieSlotAnalysis> _slots = [];
    public ObservableCollection<PcieSlotAnalysis> Slots { get => _slots; private set => SetProperty(ref _slots, value); }

    private ObservableCollection<PcieLaneAllocation> _laneMap = [];
    public ObservableCollection<PcieLaneAllocation> LaneMap { get => _laneMap; private set => SetProperty(ref _laneMap, value); }

    private int _totalCpuLanes;
    public int TotalCpuLanes { get => _totalCpuLanes; private set => SetProperty(ref _totalCpuLanes, value); }

    private int _totalChipsetLanes;
    public int TotalChipsetLanes { get => _totalChipsetLanes; private set => SetProperty(ref _totalChipsetLanes, value); }

    private int _usedCpuLanes;
    public int UsedCpuLanes { get => _usedCpuLanes; private set => SetProperty(ref _usedCpuLanes, value); }

    private int _usedChipsetLanes;
    public int UsedChipsetLanes { get => _usedChipsetLanes; private set => SetProperty(ref _usedChipsetLanes, value); }

    private string _summary = string.Empty;
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    public void Refresh()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var devices = EnumeratePcieDevices();
            var slotList = new ObservableCollection<PcieSlotAnalysis>();
            var lanes = new ObservableCollection<PcieLaneAllocation>();

            int cpuLanesUsed = 0;
            int chipsetLanesUsed = 0;

            foreach (var dev in devices)
            {
                var slot = AnalyseDevice(dev);
                slotList.Add(slot);

                var alloc = new PcieLaneAllocation
                {
                    DeviceName = slot.DeviceName,
                    BusNumber = slot.BusNumber,
                    NegotiatedWidth = slot.NegotiatedWidth,
                    Source = ClassifyLaneSource(slot.BusNumber),
                };
                lanes.Add(alloc);

                if (alloc.Source == LaneSource.CPU)
                    cpuLanesUsed += slot.NegotiatedWidth;
                else
                    chipsetLanesUsed += slot.NegotiatedWidth;
            }

            Slots = slotList;
            LaneMap = lanes;
            UsedCpuLanes = cpuLanesUsed;
            UsedChipsetLanes = chipsetLanesUsed;

            // 常見平台總通道數估算
            EstimateTotalLanes();

            int splitCount = slotList.Count(s => s.IsSplitDetected);
            Summary = $"共 {slotList.Count} 個 PCIe 裝置，" +
                      $"CPU 通道 {UsedCpuLanes}/{TotalCpuLanes}，" +
                      $"PCH 通道 {UsedChipsetLanes}/{TotalChipsetLanes}" +
                      (splitCount > 0 ? $"，{splitCount} 個插槽偵測到拆分組態" : "");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"分析失敗：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ==================================================================
    //  內部實作
    // ==================================================================

    private static List<PciRawDevice> EnumeratePcieDevices()
    {
        var list = new List<PciRawDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE Service IS NOT NULL");
            foreach (var obj in searcher.Get())
            {
                string? devId = obj["DeviceID"]?.ToString();
                if (devId is null || !devId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                    continue;

                var raw = ParsePciDeviceId(devId);
                raw.Name = obj["Name"]?.ToString() ?? "未知裝置";
                raw.Status = obj["Status"]?.ToString() ?? "";
                list.Add(raw);
            }
        }
        catch { /* WMI 不可用時靠空清單 */ }

        // 試著從登錄檔讀 PCIe Link 資訊
        EnrichWithSetupApiData(list);
        return list;
    }

    private static PciRawDevice ParsePciDeviceId(string deviceId)
    {
        // PCI\VEN_XXXX&DEV_XXXX&SUBSYS_XXXXXXXX&REV_XX\BUS_XX&DEV_XX&FUNC_XX
        var raw = new PciRawDevice { DeviceId = deviceId };
        try
        {
            var parts = deviceId.Split('\\');
            if (parts.Length >= 2)
            {
                var idPart = parts[1];
                foreach (var seg in idPart.Split('&'))
                {
                    if (seg.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase))
                        raw.VendorId = Convert.ToUInt16(seg[4..], 16);
                    else if (seg.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase))
                        raw.DeviceIdHex = Convert.ToUInt16(seg[4..], 16);
                }
            }
            if (parts.Length >= 3)
            {
                foreach (var seg in parts[2].Split('&'))
                {
                    if (seg.StartsWith("BUS_", StringComparison.OrdinalIgnoreCase))
                        raw.Bus = Convert.ToByte(seg[4..], 16);
                    else if (seg.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase) && seg.Length <= 6)
                        raw.Device = Convert.ToByte(seg[4..], 16);
                    else if (seg.StartsWith("FUNC_", StringComparison.OrdinalIgnoreCase))
                        raw.Function = Convert.ToByte(seg[5..], 16);
                }
            }
        }
        catch { /* 解析失敗則留預設值 */ }
        return raw;
    }

    /// <summary>
    /// 嘗試用 SetupAPI / 登錄檔補充 PCIe Capability 資訊。
    /// 實際可讀取 HKLM\SYSTEM\CurrentControlSet\Enum\PCI\...\Device Parameters
    /// </summary>
    private static void EnrichWithSetupApiData(List<PciRawDevice> devices)
    {
        const string basePath = @"SYSTEM\CurrentControlSet\Enum\PCI";
        try
        {
            using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(basePath);
            if (baseKey is null) return;

            foreach (var dev in devices)
            {
                TryReadLinkInfo(baseKey, dev);
            }
        }
        catch { /* 登錄檔存取失敗則略過 */ }
    }

    private static void TryReadLinkInfo(Microsoft.Win32.RegistryKey baseKey, PciRawDevice dev)
    {
        // 遍歷登錄檔尋找對應的裝置
        foreach (var subName in baseKey.GetSubKeyNames())
        {
            if (!subName.Contains($"VEN_{dev.VendorId:X4}", StringComparison.OrdinalIgnoreCase) ||
                !subName.Contains($"DEV_{dev.DeviceIdHex:X4}", StringComparison.OrdinalIgnoreCase))
                continue;

            using var devKey = baseKey.OpenSubKey(subName);
            if (devKey is null) continue;

            foreach (var instName in devKey.GetSubKeyNames())
            {
                using var instKey = devKey.OpenSubKey(instName);
                var paramKey = instKey?.OpenSubKey("Device Parameters");
                if (paramKey is null) continue;

                // PCIe Link Speed / Width 可能存在這些值中
                if (paramKey.GetValue("LinkSpeed") is int speed)
                    dev.NegotiatedSpeedGTs = speed;
                if (paramKey.GetValue("LinkWidth") is int width)
                    dev.NegotiatedWidth = width;
                if (paramKey.GetValue("MaxLinkSpeed") is int maxSpeed)
                    dev.CapableSpeedGTs = maxSpeed;
                if (paramKey.GetValue("MaxLinkWidth") is int maxWidth)
                    dev.CapableWidth = maxWidth;
            }
        }
    }

    private static PcieSlotAnalysis AnalyseDevice(PciRawDevice raw)
    {
        int negGen = SpeedToGen(raw.NegotiatedSpeedGTs);
        int capGen = SpeedToGen(raw.CapableSpeedGTs);

        double negBw = CalculateBandwidthMBps(negGen, raw.NegotiatedWidth);
        double capBw = CalculateBandwidthMBps(capGen, raw.CapableWidth);

        bool isSplit = raw.CapableWidth > 0 && raw.NegotiatedWidth > 0 &&
                       raw.NegotiatedWidth < raw.CapableWidth;

        string splitDesc = isSplit
            ? $"x{raw.CapableWidth} → x{raw.NegotiatedWidth}"
            : "無";

        return new PcieSlotAnalysis
        {
            DeviceName = raw.Name,
            BusNumber = raw.Bus,
            DeviceNumber = raw.Device,
            FunctionNumber = raw.Function,
            VendorId = raw.VendorId,
            DeviceIdHex = raw.DeviceIdHex,
            NegotiatedGen = negGen,
            NegotiatedWidth = raw.NegotiatedWidth,
            NegotiatedBandwidthMBps = negBw,
            CapableGen = capGen,
            CapableWidth = raw.CapableWidth,
            CapableBandwidthMBps = capBw,
            IsSplitDetected = isSplit,
            SplitDescription = splitDesc,
            BandwidthUtilization = capBw > 0 ? negBw / capBw : 0,
        };
    }

    private static int SpeedToGen(int speedGTs) => speedGTs switch
    {
        >= 64 => 6,
        >= 32 => 5,
        >= 16 => 4,
        >= 8  => 3,
        >= 5  => 2,
        >= 2  => 1,
        _     => 0,
    };

    /// <summary>MB/s = GT/s * 通道數 * 編碼效率 / 8</summary>
    public static double CalculateBandwidthMBps(int gen, int width)
    {
        if (gen <= 0 || width <= 0) return 0;
        var r = GenReferences.FirstOrDefault(g => g.Gen == gen);
        if (r is null) return 0;
        double efficiency = (double)r.DataBits / r.TotalBits;
        return r.TransferRateGTs * width * efficiency * 1000.0 / 8.0; // GT/s → MB/s
    }

    /// <summary>
    /// 依 BUS 號判斷通道來源。
    /// 一般而言 Bus 0 上的裝置直接連 CPU Root Complex，
    /// Bus 號較高的通常經過 PCH。
    /// </summary>
    private static LaneSource ClassifyLaneSource(int bus)
        => bus <= 1 ? LaneSource.CPU : LaneSource.Chipset;

    private void EstimateTotalLanes()
    {
        // 依據 CPU 型號與平台常見通道數估算
        string cpuName = "";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var o in s.Get()) { cpuName = o["Name"]?.ToString() ?? ""; break; }
        }
        catch { /* 略過 */ }

        (TotalCpuLanes, TotalChipsetLanes) = EstimatePlatformLanes(cpuName);
    }

    internal static (int cpu, int chipset) EstimatePlatformLanes(string cpuName)
    {
        string upper = cpuName.ToUpperInvariant();

        // Intel 桌機平台
        if (upper.Contains("14900") || upper.Contains("14700") || upper.Contains("13900") || upper.Contains("13700"))
            return (20, 24); // LGA1700
        if (upper.Contains("12900") || upper.Contains("12700") || upper.Contains("12600"))
            return (20, 24); // LGA1700
        if (upper.Contains("10900") || upper.Contains("10700") || upper.Contains("10600"))
            return (16, 24); // LGA1200
        if (upper.Contains("XEON"))
            return (48, 24); // HEDT/Server 估算

        // AMD 桌機平台
        if (upper.Contains("9950X") || upper.Contains("9900X") || upper.Contains("9700X") || upper.Contains("9600X"))
            return (28, 12); // AM5
        if (upper.Contains("7950X") || upper.Contains("7900X") || upper.Contains("7700X") || upper.Contains("7600X"))
            return (28, 12); // AM5
        if (upper.Contains("5950X") || upper.Contains("5900X") || upper.Contains("5800X") || upper.Contains("5600X"))
            return (24, 16); // AM4
        if (upper.Contains("THREADRIPPER"))
            return (64, 16);

        // 預設估算
        return (16, 24);
    }
}

// ==================================================================
//  資料模型
// ==================================================================

public record PcieGenReference(int Gen, string Name, double TransferRateGTs,
                                int DataBits, int TotalBits, string Encoding);

public sealed class PciRawDevice
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public ushort VendorId { get; set; }
    public ushort DeviceIdHex { get; set; }
    public byte Bus { get; set; }
    public byte Device { get; set; }
    public byte Function { get; set; }
    public int NegotiatedSpeedGTs { get; set; }
    public int NegotiatedWidth { get; set; }
    public int CapableSpeedGTs { get; set; }
    public int CapableWidth { get; set; }
}

public sealed class PcieSlotAnalysis
{
    public string DeviceName { get; init; } = "";
    public int BusNumber { get; init; }
    public int DeviceNumber { get; init; }
    public int FunctionNumber { get; init; }
    public ushort VendorId { get; init; }
    public ushort DeviceIdHex { get; init; }

    public int NegotiatedGen { get; init; }
    public int NegotiatedWidth { get; init; }
    public double NegotiatedBandwidthMBps { get; init; }

    public int CapableGen { get; init; }
    public int CapableWidth { get; init; }
    public double CapableBandwidthMBps { get; init; }

    public bool IsSplitDetected { get; init; }
    public string SplitDescription { get; init; } = "";

    /// <summary>0–1，實際/最大頻寬比</summary>
    public double BandwidthUtilization { get; init; }

    // 顯示用屬性
    public string NegotiatedLabel => NegotiatedGen > 0
        ? $"Gen{NegotiatedGen} x{NegotiatedWidth}"
        : "未知";
    public string CapableLabel => CapableGen > 0
        ? $"Gen{CapableGen} x{CapableWidth}"
        : "未知";
    public string NegotiatedBwLabel => NegotiatedBandwidthMBps > 0
        ? $"{NegotiatedBandwidthMBps:N0} MB/s"
        : "—";
    public string CapableBwLabel => CapableBandwidthMBps > 0
        ? $"{CapableBandwidthMBps:N0} MB/s"
        : "—";
    public string LocationLabel => $"Bus {BusNumber:X2}h, Dev {DeviceNumber:X2}h, Func {FunctionNumber}";
}

public sealed class PcieLaneAllocation
{
    public string DeviceName { get; init; } = "";
    public int BusNumber { get; init; }
    public int NegotiatedWidth { get; init; }
    public LaneSource Source { get; init; }
    public string SourceLabel => Source == LaneSource.CPU ? "CPU" : "PCH 晶片組";
}

public enum LaneSource { CPU, Chipset }
