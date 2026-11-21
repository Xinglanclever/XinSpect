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
        new(6, "Gen6", 64.0, 0, 0, "PAM4／FLIT／FEC（頻寬未校核）"),
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
        if (_isLoading) return;                 // 防止 Loaded 與「重新分析」重入
        IsLoading = true;
        ErrorMessage = null;
        // WMI 列舉 ＋ 上千次 PCI 設定空間掃描移到背景執行緒，避免進頁凍結；屬性回 UI 執行緒設定。
        _ = Task.Run(CollectAnalysis)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    ErrorMessage = $"分析失敗：{t.Exception!.GetBaseException().Message}";
                }
                else
                {
                    var a = t.Result;
                    Slots = new ObservableCollection<PcieSlotAnalysis>(a.Slots);
                    LaneMap = new ObservableCollection<PcieLaneAllocation>(a.Lanes);
                    UsedCpuLanes = a.CpuLanesUsed;
                    UsedChipsetLanes = a.ChipsetLanesUsed;
                    TotalCpuLanes = a.TotalCpu;
                    TotalChipsetLanes = a.TotalChipset;
                    Summary = $"共 {a.Slots.Count} 筆來源記錄（PnP 與設定空間未配對，可能為同一裝置）；鏈路按讀取位置保留，segment 與 CPU／PCH 來源未知，平台通道總數未知。";
                }
                IsLoading = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }
    // 背景執行緒：列舉裝置＋補鏈路＋算通道，全部回傳純資料；設定屬性留給 UI 執行緒。
    private static AnalysisResult CollectAnalysis()
    {
        var devices = EnumeratePcieDevices();
        List<PcieLinkRow> links = [];
        try { links = PcieLinkService.ScanAll().Rows; }
        catch (Exception ex) { Diag.Swallow("PcieAnalysis.LinkScan", ex, "鏈路讀取失敗，保留 PnP 清單與未知鏈路"); }
        return Analyse(devices, links);
    }

    private static AnalysisResult Analyse(IEnumerable<PciRawDevice> devices, IEnumerable<PcieLinkRow> links)
    {
        var slots = AnalyseSources(devices, links).ToList();
        var lanes = slots.Select(slot => new PcieLaneAllocation {
            DeviceName = slot.DeviceName, BusNumber = slot.BusNumber,
            NegotiatedWidth = slot.NegotiatedWidth, Source = LaneSource.Unknown }).ToList();
        return new(slots, lanes, 0, 0, 0, 0);
    }

    public bool PlatformLaneCountsKnown => false;
    public string CpuLaneCountLabel => "未知";
    public string ChipsetLaneCountLabel => "未知";

    private readonly record struct AnalysisResult(
        List<PcieSlotAnalysis> Slots, List<PcieLaneAllocation> Lanes,
        int CpuLanesUsed, int ChipsetLanesUsed, int TotalCpu, int TotalChipset);

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
        catch (Exception ex) { Diag.Swallow("PcieAnalysis.WMI", ex, "PCIe 裝置清單將為空"); }

        // PnP instance IDs do not supply verified segment/BDF identities. Do not join by model IDs.
        // PcieLinkService rows have BDF but no instance/segment identity for this mapping.
        return list;
    }

    internal static PciRawDevice ParsePciDeviceId(string deviceId)
    {
        var raw = new PciRawDevice { DeviceId = deviceId };
        var parts = deviceId.Split('\\');
        if (parts.Length < 2 || !parts[0].Equals("PCI", StringComparison.OrdinalIgnoreCase)) return raw;
        foreach (var segment in parts[1].Split('&'))
        {
            if (segment.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase) &&
                ushort.TryParse(segment.AsSpan(4), System.Globalization.NumberStyles.HexNumber, null, out ushort vendor)) raw.VendorId = vendor;
            if (segment.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase) &&
                ushort.TryParse(segment.AsSpan(4), System.Globalization.NumberStyles.HexNumber, null, out ushort device)) raw.DeviceIdHex = device;
        }
        // Instance suffix is opaque; BUS_/DEV_/FUNC_ text is not accepted as topology evidence.
        return raw;
    }

    internal static IReadOnlyList<PcieSlotAnalysis> AnalyseSources(IEnumerable<PciRawDevice> devices,
        IEnumerable<PcieLinkRow> links)
    {
        var result = devices.Where(d => !string.IsNullOrWhiteSpace(d.DeviceId))
            .GroupBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => { var raw = ParsePciDeviceId(g.Key); raw.Name = g.First().Name; return AnalyseDevice(raw); }).ToList();
        // The backend reads by BDF only (WinRing0Bridge.ReadPciConfig), without a segment argument.
        // Keep its own scoped records; never attach them to a PnP instance by VEN/DEV or bus alone.
        foreach (var group in links.GroupBy(r => r.Location, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() != 1 || !TryReadLocation(group.Key, out byte bus, out byte device, out byte function)) continue;
            var row = group.Single();
            result.Add(AnalyseDevice(new PciRawDevice {
                DeviceId = "PCI-config-BDF-only:" + group.Key,
                Name = $"PCI {row.Ven:X4}:{row.Dev:X4}（設定空間記錄，未配對 PnP）",
                VendorId = row.Ven, DeviceIdHex = row.Dev, Bus = bus, Device = device, Function = function,
                RawLocation = row.Location,
                LinkSource = "PcieLinkService.ScanAll / WinRing0 ReadPciConfig(BDF); segment unavailable",
                NegotiatedSpeedCode = row.CurSpeed, NegotiatedWidth = row.CurWidth,
                CapableSpeedCode = row.MaxSpeed, CapableWidth = row.MaxWidth }));
        }
        return result;
    }

    private static bool TryReadLocation(string text, out byte bus, out byte device, out byte function)
    {
        bus = device = function = 0;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\A([0-9A-Fa-f]{2}):([0-9A-Fa-f]{2})\.([0-7])\z");
        return match.Success && byte.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out bus)
            && byte.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.HexNumber, null, out device)
            && device <= 31 && byte.TryParse(match.Groups[3].Value, out function);
    }

    internal static PcieSlotAnalysis AnalyseDevice(PciRawDevice raw)
    {
        int negGen = ValidGeneration(raw.NegotiatedSpeedCode);
        int capGen = ValidGeneration(raw.CapableSpeedCode);

        double negBw = CalculateBandwidthMBps(negGen, raw.NegotiatedWidth);
        double capBw = CalculateBandwidthMBps(capGen, raw.CapableWidth);

        bool isSplit = false; // A negotiated-width gap does not prove lane bifurcation.

        string splitDesc = isSplit
            ? $"x{raw.CapableWidth} → x{raw.NegotiatedWidth}"
            : "未驗證";

        return new PcieSlotAnalysis
        {
            DeviceName = raw.Name,
            RawInstanceId = raw.DeviceId,
            Address = raw.Address,
            RawLocation = raw.RawLocation,
            LinkSource = raw.LinkSource,
            BusNumber = raw.Address?.Bus ?? raw.Bus,
            DeviceNumber = raw.Address?.Device ?? raw.Device,
            FunctionNumber = raw.Address?.Function ?? raw.Function,
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

    // Link Capabilities/Status bits 3:0 are speed codes (1..6), not rates in GT/s.
    private static int ValidGeneration(int speedCode) => speedCode is >= 1 and <= 6 ? speedCode : 0;

    /// <summary>MB/s = GT/s * 通道數 * 編碼效率 / 8</summary>
    public static double CalculateBandwidthMBps(int gen, int width)
    {
        if (gen <= 0 || width <= 0) return 0;
        var r = GenReferences.FirstOrDefault(g => g.Gen == gen);
        if (r is null || !r.BandwidthCalculationSupported) return 0;
        double efficiency = (double)r.DataBits / r.TotalBits;
        return r.TransferRateGTs * width * efficiency * 1000.0 / 8.0; // GT/s → MB/s
    }

    // CPU model names and bus numbers do not establish lane budgets or physical routing.
    internal static (int cpu, int chipset) EstimatePlatformLanes(string cpuName) => (0, 0);

}

// ==================================================================
//  資料模型
// ==================================================================

public record PcieGenReference(int Gen, string Name, double TransferRateGTs,
                                int DataBits, int TotalBits, string Encoding)
{
    public bool BandwidthCalculationSupported => Gen is >= 1 and <= 5 && DataBits > 0 && TotalBits > 0;
    public double TheoreticalSingleLaneMBps => PcieAnalysisService.CalculateBandwidthMBps(Gen, 1);
    public string TheoreticalSingleLaneLabel => BandwidthCalculationSupported
        ? $"{TheoreticalSingleLaneMBps:N2}" : "未校核";
}

public readonly record struct PcieAddress(ushort Segment, byte Bus, byte Device, byte Function);

public sealed class PciRawDevice
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string RawLocation { get; set; } = "";
    public PcieAddress? Address { get; set; }
    public string LinkSource { get; set; } = "";
    public ushort VendorId { get; set; }
    public ushort DeviceIdHex { get; set; }
    public byte Bus { get; set; }
    public byte Device { get; set; }
    public byte Function { get; set; }
    public int NegotiatedSpeedCode { get; set; }
    public int NegotiatedWidth { get; set; }
    public int CapableSpeedCode { get; set; }
    public int CapableWidth { get; set; }
}

public sealed class PcieSlotAnalysis
{
    public string RawInstanceId { get; init; } = "";
    public string RawLocation { get; init; } = "";
    public PcieAddress? Address { get; init; }
    public string LinkSource { get; init; } = "";
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
        : NegotiatedGen >= 6 ? "未知（未校核）" : "未知";
    public string CapableBwLabel => CapableBandwidthMBps > 0
        ? $"{CapableBandwidthMBps:N0} MB/s"
        : CapableGen >= 6 ? "未知（未校核）" : "未知";
    public string LocationLabel => Address is { } address
        ? $"Segment {address.Segment:X4}, Bus {address.Bus:X2}h, Dev {address.Device:X2}h, Func {address.Function}"
        : RawLocation.Length > 0 ? $"{RawLocation}（segment 未知）" : "未知（缺少可驗證 PCI 位置）";
}

public sealed class PcieLaneAllocation
{
    public string DeviceName { get; init; } = "";
    public int BusNumber { get; init; }
    public int NegotiatedWidth { get; init; }
    public LaneSource Source { get; init; }
    public string SourceLabel => Source switch { LaneSource.CPU => "CPU", LaneSource.Chipset => "PCH 晶片組", _ => "未知（未驗證拓撲）" };
}

public enum LaneSource { CPU, Chipset, Unknown }
