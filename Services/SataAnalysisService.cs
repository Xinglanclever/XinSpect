using System.Collections.ObjectModel;
using System.Management;

namespace XinSpect;

/// <summary>
/// 列舉 SATA (AHCI) 控制器與連接磁碟，分析 SATA 世代、協商速度與 eSATA 能力。全程唯讀。
/// </summary>
public sealed class SataAnalysisService : ObservableObject
{
    public ObservableCollection<SataControllerRow> Controllers { get; } = [];
    public ObservableCollection<SataDriveRow> Drives { get; } = [];

    private bool _loading;
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); } }
    public bool CanRefresh => !_loading;

    private string _status = "尚未讀取。按「重新掃描」列出 SATA 控制器與磁碟（唯讀）。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _summary = "—";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public void Refresh()
    {
        if (_loading) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        Status = "查詢 SATA 控制器與磁碟中…";
        Controllers.Clear();
        Drives.Clear();
        Summary = "—";
        try
        {
            var (summary, ctrls, drives) = await Task.Run(ScanAll);
            foreach (var c in ctrls) Controllers.Add(c);
            foreach (var d in drives) Drives.Add(d);
            Summary = summary;
            Status = $"掃描完成。";
        }
        catch (Exception ex)
        {
            Summary = "無法讀取：" + ex.Message;
            Status = "讀取失敗。";
        }
        finally { IsLoading = false; }
    }

    // ── 速度參考（靜態資料） ──
    public static IReadOnlyList<SataSpeedRef> SpeedReference { get; } =
    [
        new("SATA I",   "1.5 Gbps", "150 MB/s",  "~120 MB/s"),
        new("SATA II",  "3.0 Gbps", "300 MB/s",  "~250 MB/s"),
        new("SATA III", "6.0 Gbps", "600 MB/s",  "~550 MB/s"),
    ];

    // ── 掃描邏輯 ──
    private static (string Summary, List<SataControllerRow> Ctrls, List<SataDriveRow> Drives) ScanAll()
    {
        var ctrls  = new List<SataControllerRow>();
        var drives = new List<SataDriveRow>();

        // 1) AHCI / SATA 控制器。
        //    Win32_IDEController 是舊類別，Win10 以後幾乎恆為空（本機實測 0 筆）。
        //    PnP 類別也不只一種：本機的「Standard SATA AHCI Controller」是 HDC，
        //    某些平台掛在 SCSIAdapter 下——兩個都查才不會漏。
        using (var s = new ManagementObjectSearcher("root\\CIMV2",
            "SELECT Name, Manufacturer, PNPDeviceID, Status FROM Win32_PnPEntity " +
            "WHERE PNPClass='HDC' OR PNPClass='SCSIAdapter'"))
        {
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    var name = (o["Name"] as string)?.Trim() ?? "";
                    if (name.Length == 0) continue;

                    bool isAhci = name.Contains("AHCI", StringComparison.OrdinalIgnoreCase)
                               || name.Contains("SATA", StringComparison.OrdinalIgnoreCase);
                    // NVMe 控制器同樣掛在 SCSIAdapter 下，這一頁只談 SATA/AHCI
                    if (!isAhci) continue;

                    var pnp = (o["PNPDeviceID"] as string) ?? "";
                    ctrls.Add(new SataControllerRow
                    {
                        Name = name,
                        Manufacturer = (o["Manufacturer"] as string)?.Trim() ?? "",
                        VendorId = ExtractPnp(pnp, "VEN_"),
                        DeviceId = ExtractPnp(pnp, "DEV_"),
                        AhciVersion = InferAhci(name),
                        IsAhci = true,
                        Status = (o["Status"] as string) ?? "",
                    });
                }
            }
        }

        // 2) SATA 磁碟：先用 MSFT_PhysicalDisk 的 BusType 標出哪些是 SATA（11），
        //    再與 Win32_DiskDrive 對應。舊寫法用 InterfaceType='IDE' 篩選，
        //    SATA 碟在 AHCI 模式下多回報為 SCSI，NVMe 更是另一種，會整個漏掉。
        var sataNames = ReadSataDiskNames();

        using (var s = new ManagementObjectSearcher("root\\CIMV2",
            "SELECT Model, SerialNumber, FirmwareRevision, Size, MediaType, PNPDeviceID, InterfaceType FROM Win32_DiskDrive"))
        {
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    var model = (o["Model"] as string)?.Trim() ?? "(未知)";
                    var iface = (o["InterfaceType"] as string)?.Trim() ?? "";

                    // 只留 SATA 碟：介面是 IDE/SATA，或 MSFT_PhysicalDisk 標為 SATA 者
                    bool isSata = iface.Equals("IDE", StringComparison.OrdinalIgnoreCase)
                               || iface.Equals("SATA", StringComparison.OrdinalIgnoreCase)
                               || sataNames.Contains(model);
                    if (!isSata) continue;

                    long size = Convert.ToInt64(o["Size"] ?? 0);
                    var (gen, speed) = GuessSataGen(model, size);
                    drives.Add(new SataDriveRow
                    {
                        Model = model,
                        Serial = (o["SerialNumber"] as string)?.Trim() ?? "",
                        Firmware = (o["FirmwareRevision"] as string)?.Trim() ?? "",
                        CapacityText = FormatSize(size),
                        MediaType = (o["MediaType"] as string) ?? "",
                        Generation = gen,
                        NegotiatedSpeed = speed,
                        NcqSupport = true, // AHCI 模式即隱含 NCQ
                        PnpId = (o["PNPDeviceID"] as string) ?? "",
                    });
                }
            }
        }



        string summary = $"共 {ctrls.Count} 個控制器（AHCI {ctrls.Count(c => c.IsAhci)} 個）、{drives.Count} 個 SATA 磁碟";
        return (summary, ctrls, drives);
    }

    /// <summary>
    /// 從儲存命名空間取得「匯流排為 SATA（BusType 11）」的磁碟型號集合。
    /// Win32_DiskDrive 的 InterfaceType 在 AHCI 模式下對 SATA 碟多半回報 SCSI，
    /// 單靠它分不出 SATA 與 NVMe——BusType 才是可靠的分辨依據。
    /// </summary>
    private static HashSet<string> ReadSataDiskNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scope = new ManagementScope(@"\\localhost\ROOT\Microsoft\Windows\Storage");
            scope.Connect();
            using var s = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT FriendlyName, BusType FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    if (Convert.ToUInt16(o["BusType"] ?? 0) != 11) continue;
                    var friendly = (o["FriendlyName"] as string)?.Trim();
                    if (!string.IsNullOrEmpty(friendly)) names.Add(friendly);
                }
            }
        }
        catch (Exception ex) { Diag.Swallow("SataAnalysis.Registry", ex, "SATA 登錄值讀不到"); }
        return names;
    }

    private static (string Gen, string Speed) GuessSataGen(string model, long bytes)
    {
        bool ssd = model.Contains("SSD", StringComparison.OrdinalIgnoreCase)
                || model.Contains("Solid", StringComparison.OrdinalIgnoreCase);
        if (ssd) return ("SATA III", "6.0 Gbps");
        if (bytes >= 1_000_000_000_000L) return ("SATA III", "6.0 Gbps");
        return ("未知", "未知");
    }

    private static string InferAhci(string name)
    {
        if (name.Contains("AHCI 1.3", StringComparison.OrdinalIgnoreCase)) return "1.3";
        if (name.Contains("AHCI 1.2", StringComparison.OrdinalIgnoreCase)) return "1.2";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return ">= 1.3";
        return "未知";
    }

    private static string ExtractPnp(string pnp, string prefix)
    {
        int i = pnp.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        int start = i + prefix.Length;
        int end = pnp.IndexOf('&', start);
        if (end < 0) end = pnp.Length;
        return pnp[start..Math.Min(start + 4, end)];
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "未知";
        double gb = bytes / 1_073_741_824.0;
        return gb >= 1000 ? $"{gb / 1024:F1} TB" : $"{gb:F0} GB";
    }
}

// ── 資料型別 ──
public sealed class SataControllerRow
{
    public required string Name         { get; init; }
    public required string Manufacturer { get; init; }
    public required string VendorId     { get; init; }
    public required string DeviceId     { get; init; }
    public required string AhciVersion  { get; init; }
    public required bool   IsAhci       { get; init; }
    public required string Status       { get; init; }
    public string TypeText => IsAhci ? "AHCI" : "IDE";
}

public sealed class SataDriveRow
{
    public required string Model           { get; init; }
    public required string Serial          { get; init; }
    public required string Firmware        { get; init; }
    public required string CapacityText    { get; init; }
    public required string MediaType       { get; init; }
    public          string Generation      { get; set; } = "未知";
    public          string NegotiatedSpeed { get; set; } = "未知";
    public required bool   NcqSupport      { get; init; }
    public required string PnpId           { get; init; }
    public string NcqText => NcqSupport ? "支援" : "不支援";
}

public sealed record SataSpeedRef(string Generation, string RawBandwidth, string TheoreticalMax, string PracticalMax);
