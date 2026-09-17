using System.Collections.ObjectModel;
using System.Management;

namespace XinSpect;

/// <summary>
/// 晶片組分析服務：偵測晶片組型號，提供北橋/南橋功能參考。
/// </summary>
public sealed class ChipsetAnalysisService : ObservableObject
{
    private string _chipsetName = "";
    public string ChipsetName { get => _chipsetName; private set => SetProperty(ref _chipsetName, value); }

    private string _chipsetVendor = "";
    public string ChipsetVendor { get => _chipsetVendor; private set => SetProperty(ref _chipsetVendor, value); }

    private string _hostBridgeName = "";
    public string HostBridgeName { get => _hostBridgeName; private set => SetProperty(ref _hostBridgeName, value); }

    private string _northbridgeInfo = "";
    public string NorthbridgeInfo { get => _northbridgeInfo; private set => SetProperty(ref _northbridgeInfo, value); }

    private string _southbridgeInfo = "";
    public string SouthbridgeInfo { get => _southbridgeInfo; private set => SetProperty(ref _southbridgeInfo, value); }

    private ObservableCollection<ChipsetFeatureEntry> _features = [];
    public ObservableCollection<ChipsetFeatureEntry> Features { get => _features; private set => SetProperty(ref _features, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    // ---- 晶片組資料庫 ----
    private static readonly Dictionary<string, ChipsetProfile> s_profiles = BuildProfileDatabase();

    public void Refresh()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            DetectChipset();
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
    //  偵測晶片組
    // ==================================================================

    private void DetectChipset()
    {
        // 1) 從 Win32_BaseBoard 取得主機板型號
        string boardModel = "";
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Product, Manufacturer FROM Win32_BaseBoard");
            foreach (var o in s.Get())
            {
                boardModel = $"{o["Manufacturer"]} {o["Product"]}".Trim();
                break;
            }
        }
        catch { /* 略過 */ }

        // 2) 從 PCI 列舉找 Host Bridge (class 0x0600)
        string hostBridge = "";
        string pchDevice = "";
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE 'PCI%'");
            foreach (var o in s.Get())
            {
                string? name = o["Name"]?.ToString();
                string? devId = o["DeviceID"]?.ToString();
                if (name is null) continue;

                // Host Bridge — 北橋/CPU 內建
                if (name.Contains("Host Bridge", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Root Complex", StringComparison.OrdinalIgnoreCase) ||
                    (name.Contains("PCI Express", StringComparison.OrdinalIgnoreCase) &&
                     name.Contains("Root Port", StringComparison.OrdinalIgnoreCase) && hostBridge == ""))
                {
                    if (hostBridge == "") hostBridge = name;
                }

                // ISA Bridge / LPC Controller / eSPI — 南橋/PCH
                if (name.Contains("ISA Bridge", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("LPC Controller", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("eSPI Controller", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("SMBus", StringComparison.OrdinalIgnoreCase))
                {
                    if (pchDevice == "") pchDevice = name;
                }
            }
        }
        catch { /* 略過 */ }

        HostBridgeName = hostBridge.Length > 0 ? hostBridge : "未偵測到";

        // 3) 嘗試從主機板型號或 PCI 名稱查表
        string detected = TryMatchChipset(boardModel, hostBridge, pchDevice);
        ChipsetName = detected;

        // 4) 查表填充詳細資訊
        if (s_profiles.TryGetValue(detected.ToUpperInvariant(), out var profile))
        {
            ChipsetVendor = profile.Vendor;
            NorthbridgeInfo = profile.NorthbridgeDescription;
            SouthbridgeInfo = profile.SouthbridgeDescription;
            Features = new(profile.Features);
            Summary = profile.OneLiner;
        }
        else
        {
            ChipsetVendor = hostBridge.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" :
                            hostBridge.Contains("AMD",   StringComparison.OrdinalIgnoreCase) ? "AMD" : "未知";
            NorthbridgeInfo = $"記憶體控制器、PCIe Root Complex（已整合至 CPU）\nHost Bridge: {hostBridge}";
            SouthbridgeInfo = $"PCH / 南橋: {(pchDevice.Length > 0 ? pchDevice : "未偵測")}";
            Features = [];
            Summary = $"晶片組型號「{detected}」不在內建資料庫中，僅顯示基本偵測結果。";
        }
    }

    private static string TryMatchChipset(string board, string hostBridge, string pch)
    {
        string upper = (board + " " + hostBridge + " " + pch).ToUpperInvariant();

        // 直接匹配晶片組名稱
        string[] known =
        [
            "Z890", "Z790", "B760", "H770", "Z690", "B660", "H670", "H610",
            "X299", "C621", "C622", "W680",
            "X870E", "X870", "X670E", "X670", "B650E", "B650", "A620",
            "X570", "B550", "A520", "X470", "B450",
            "X399", "TRX40", "TRX50", "WRX80",
        ];

        foreach (var name in known)
        {
            if (upper.Contains(name))
                return name;
        }

        // 這裡先前會在比對不到時，依 CPU 世代直接「回傳一個型號」（Alder/Raptor → Z790、
        // AM5 → X670、AM4 → B550）。那是捏造：同一個世代的晶片組可能是 H610、B660、Z690 任一種，
        // 而且呼叫端會拿這個型號去查表，填出一整份假的晶片組規格（幾個 USB、幾條 PCIe 全部是編的）。
        // 晶片組型號是主機板決定的，跟 CPU 世代沒有對應關係——查不到就回未知。
        return "未知";
    }

    // ==================================================================
    //  晶片組資料庫
    // ==================================================================

    private static Dictionary<string, ChipsetProfile> BuildProfileDatabase()
    {
        var db = new Dictionary<string, ChipsetProfile>(StringComparer.OrdinalIgnoreCase);

        // ============ Intel 700 系列 (LGA1700) ============
        db["Z790"] = new ChipsetProfile
        {
            Vendor = "Intel",
            OneLiner = "Intel Z790 — 第 12/13/14 代 Core 旗艦晶片組",
            NorthbridgeDescription = "已整合至 CPU：雙通道 DDR5/DDR4、CPU 直連 20 條 PCIe 5.0/4.0",
            SouthbridgeDescription = "PCH Z790：PCIe 4.0 x20 + PCIe 3.0 x8，最多 8 個 SATA III，5 個 USB 3.2 Gen2x2，10 個 USB 3.2 Gen2",
            Features =
            [
                new("記憶體", "DDR5-5600 / DDR4-3200（雙通道）"),
                new("CPU PCIe", "1x16 或 2x8 PCIe 5.0，4x PCIe 4.0"),
                new("PCH PCIe", "20x PCIe 4.0 + 8x PCIe 3.0"),
                new("USB", "5x USB 3.2 Gen2x2 (20Gbps) + 10x USB 3.2 Gen2"),
                new("SATA", "8x SATA III (6Gbps)"),
                new("DMI", "DMI 4.0 x8 (CPU ↔ PCH)"),
                new("超頻", "支援 CPU / 記憶體超頻"),
            ],
        };

        db["B760"] = new ChipsetProfile
        {
            Vendor = "Intel",
            OneLiner = "Intel B760 — 第 12/13/14 代 Core 主流晶片組",
            NorthbridgeDescription = "已整合至 CPU：雙通道 DDR5/DDR4、CPU 直連 20 條 PCIe 5.0/4.0",
            SouthbridgeDescription = "PCH B760：PCIe 4.0 x10 + PCIe 3.0 x4，4 個 SATA III，2 個 USB 3.2 Gen2x2",
            Features =
            [
                new("記憶體", "DDR5-4800 / DDR4-3200（雙通道，不支援記憶體超頻）"),
                new("CPU PCIe", "1x16 PCIe 5.0 + 4x PCIe 4.0"),
                new("PCH PCIe", "10x PCIe 4.0 + 4x PCIe 3.0"),
                new("USB", "2x USB 3.2 Gen2x2 + 4x USB 3.2 Gen2"),
                new("SATA", "4x SATA III"),
                new("DMI", "DMI 4.0 x4"),
                new("超頻", "CPU 超頻可，記憶體不可"),
            ],
        };

        db["H770"] = new ChipsetProfile
        {
            Vendor = "Intel",
            OneLiner = "Intel H770 — 第 12/13/14 代 Core 中階晶片組",
            NorthbridgeDescription = "已整合至 CPU：雙通道 DDR5/DDR4、CPU 直連 20 條 PCIe 5.0/4.0",
            SouthbridgeDescription = "PCH H770：PCIe 4.0 x16 + PCIe 3.0 x8，8 個 SATA III",
            Features =
            [
                new("記憶體", "DDR5-4800 / DDR4-3200（雙通道）"),
                new("CPU PCIe", "1x16 PCIe 5.0 + 4x PCIe 4.0"),
                new("PCH PCIe", "16x PCIe 4.0 + 8x PCIe 3.0"),
                new("USB", "2x USB 3.2 Gen2x2 + 8x USB 3.2 Gen2"),
                new("SATA", "8x SATA III"),
                new("DMI", "DMI 4.0 x8"),
                new("超頻", "不支援 CPU / 記憶體超頻"),
            ],
        };

        db["Z690"] = new ChipsetProfile
        {
            Vendor = "Intel",
            OneLiner = "Intel Z690 — 第 12 代 Core 旗艦晶片組",
            NorthbridgeDescription = "已整合至 CPU：雙通道 DDR5/DDR4、CPU 直連 20 條 PCIe 5.0/4.0",
            SouthbridgeDescription = "PCH Z690：PCIe 4.0 x12 + PCIe 3.0 x16，8 個 SATA III",
            Features =
            [
                new("記憶體", "DDR5-4800 / DDR4-3200"),
                new("CPU PCIe", "1x16 PCIe 5.0 + 4x PCIe 4.0"),
                new("PCH PCIe", "12x PCIe 4.0 + 16x PCIe 3.0"),
                new("USB", "4x USB 3.2 Gen2x2 + 10x USB 3.2 Gen2"),
                new("SATA", "8x SATA III"),
                new("DMI", "DMI 4.0 x8"),
                new("超頻", "支援 CPU / 記憶體超頻"),
            ],
        };

        db["B660"] = new ChipsetProfile
        {
            Vendor = "Intel",
            OneLiner = "Intel B660 — 第 12 代 Core 主流晶片組",
            NorthbridgeDescription = "已整合至 CPU：雙通道 DDR5/DDR4",
            SouthbridgeDescription = "PCH B660：PCIe 4.0 x6 + PCIe 3.0 x8，4 個 SATA III",
            Features =
            [
                new("記憶體", "DDR5 / DDR4（不支援超頻）"),
                new("PCH PCIe", "6x PCIe 4.0 + 8x PCIe 3.0"),
                new("SATA", "4x SATA III"),
                new("DMI", "DMI 4.0 x4"),
            ],
        };

        db["X299"] = new ChipsetProfile
        {
            Vendor = "Intel",
            OneLiner = "Intel X299 — Core X / HEDT 平台晶片組 (LGA2066)",
            NorthbridgeDescription = "已整合至 CPU：四通道 DDR4、最多 48 條 PCIe 3.0",
            SouthbridgeDescription = "PCH X299：24x PCIe 3.0，8 個 SATA III，多 USB 3.1",
            Features =
            [
                new("記憶體", "四通道 DDR4-2933"),
                new("CPU PCIe", "最多 48x PCIe 3.0"),
                new("PCH PCIe", "24x PCIe 3.0"),
                new("SATA", "8x SATA III"),
            ],
        };

        db["C621"] = new ChipsetProfile
        {
            Vendor = "Intel",
            OneLiner = "Intel C621 — Xeon Scalable 伺服器/工作站晶片組",
            NorthbridgeDescription = "已整合至 CPU：六通道 DDR4、最多 48 條 PCIe 3.0",
            SouthbridgeDescription = "PCH C621：20x PCIe 3.0，14 個 SATA III / 8 個 SAS",
            Features =
            [
                new("記憶體", "六通道 DDR4 ECC"),
                new("CPU PCIe", "最多 48x PCIe 3.0"),
                new("PCH PCIe", "20x PCIe 3.0"),
                new("SATA/SAS", "14x SATA III / 8x SAS 3.0"),
            ],
        };

        // ============ AMD 600 系列 (AM5) ============
        db["X670E"] = new ChipsetProfile
        {
            Vendor = "AMD",
            OneLiner = "AMD X670E — Ryzen 7000/9000 極致旗艦晶片組",
            NorthbridgeDescription = "已整合至 CPU：雙通道 DDR5、CPU 直連 28 條 PCIe 5.0/4.0",
            SouthbridgeDescription = "雙 Promontory 21 PCH：全部 PCIe 5.0 插槽 + PCIe 4.0 M.2，最多 12 個 USB 3.2",
            Features =
            [
                new("記憶體", "DDR5-5200（雙通道，支援 EXPO）"),
                new("CPU PCIe", "1x16 PCIe 5.0 + 4x PCIe 5.0 (M.2)"),
                new("PCH PCIe", "8x PCIe 4.0 + 4x PCIe 3.0 (雙 PCH)"),
                new("USB", "12x USB 3.2 Gen2 + 2x USB 3.2 Gen2x2"),
                new("SATA", "8x SATA III"),
                new("超頻", "支援 CPU / 記憶體超頻 + PBO"),
            ],
        };

        db["X670"] = new ChipsetProfile
        {
            Vendor = "AMD",
            OneLiner = "AMD X670 — Ryzen 7000/9000 高階晶片組",
            NorthbridgeDescription = "已整合至 CPU：雙通道 DDR5、CPU 直連 24+4 條 PCIe 5.0/4.0",
            SouthbridgeDescription = "雙 Promontory 21 PCH：PCIe 4.0 插槽 + M.2",
            Features =
            [
                new("記憶體", "DDR5-5200（雙通道）"),
                new("CPU PCIe", "1x16 PCIe 5.0 (GPU) + PCIe 4.0 (M.2)"),
                new("PCH PCIe", "PCIe 4.0 x8 (雙 PCH)"),
                new("USB", "12x USB 3.2 Gen2"),
                new("SATA", "8x SATA III"),
                new("超頻", "支援"),
            ],
        };

        db["B650E"] = new ChipsetProfile
        {
            Vendor = "AMD",
            OneLiner = "AMD B650E — Ryzen 7000/9000 主流旗艦晶片組",
            NorthbridgeDescription = "已整合至 CPU：雙通道 DDR5，CPU 直連 PCIe 5.0",
            SouthbridgeDescription = "單 Promontory 21 PCH：PCIe 4.0 x8，全部插槽支援 PCIe 5.0",
            Features =
            [
                new("記憶體", "DDR5（雙通道）"),
                new("CPU PCIe", "PCIe 5.0 x16 + PCIe 5.0 M.2"),
                new("PCH PCIe", "PCIe 4.0 x8"),
                new("USB", "6x USB 3.2 Gen2"),
                new("SATA", "4x SATA III"),
            ],
        };

        db["B650"] = new ChipsetProfile
        {
            Vendor = "AMD",
            OneLiner = "AMD B650 — Ryzen 7000/9000 主流晶片組",
            NorthbridgeDescription = "已整合至 CPU：雙通道 DDR5",
            SouthbridgeDescription = "單 Promontory 21 PCH：PCIe 4.0 x8",
            Features =
            [
                new("記憶體", "DDR5（雙通道）"),
                new("CPU PCIe", "PCIe 4.0 x16 + PCIe 4.0 M.2"),
                new("PCH PCIe", "PCIe 4.0 x8"),
                new("USB", "6x USB 3.2 Gen2"),
                new("SATA", "4x SATA III"),
            ],
        };

        // ============ AMD 500 系列 (AM4) ============
        db["X570"] = new ChipsetProfile
        {
            Vendor = "AMD",
            OneLiner = "AMD X570 — Ryzen 3000/5000 旗艦晶片組 (AM4)",
            NorthbridgeDescription = "已整合至 CPU：雙通道 DDR4、CPU 直連 24 條 PCIe 4.0",
            SouthbridgeDescription = "PCH X570：PCIe 4.0 x16，12 個 USB 3.2，8 個 SATA III（帶風扇）",
            Features =
            [
                new("記憶體", "DDR4-3200（雙通道）"),
                new("CPU PCIe", "1x16 PCIe 4.0 + 4x PCIe 4.0"),
                new("PCH PCIe", "16x PCIe 4.0"),
                new("USB", "8x USB 3.2 Gen2 + 4x USB 3.2 Gen1"),
                new("SATA", "8x SATA III + 2x NVMe"),
                new("特殊", "PCH 帶主動風扇散熱"),
            ],
        };

        db["B550"] = new ChipsetProfile
        {
            Vendor = "AMD",
            OneLiner = "AMD B550 — Ryzen 3000/5000 主流晶片組 (AM4)",
            NorthbridgeDescription = "已整合至 CPU：雙通道 DDR4、CPU 直連 24 條 PCIe 4.0",
            SouthbridgeDescription = "PCH B550：PCIe 3.0 x10，6 個 SATA III",
            Features =
            [
                new("記憶體", "DDR4-3200（雙通道）"),
                new("CPU PCIe", "1x16 PCIe 4.0 + 1x4 PCIe 4.0 (M.2)"),
                new("PCH PCIe", "10x PCIe 3.0"),
                new("USB", "2x USB 3.2 Gen2 + 6x USB 3.2 Gen1"),
                new("SATA", "6x SATA III"),
            ],
        };

        // ============ AMD HEDT ============
        db["X399"] = new ChipsetProfile
        {
            Vendor = "AMD",
            OneLiner = "AMD X399 — 第一代 / 第二代 Threadripper (TR4)",
            NorthbridgeDescription = "已整合至 CPU：四通道 DDR4、64 條 PCIe 3.0",
            SouthbridgeDescription = "PCH X399：PCIe 3.0 x8，SATA，USB",
            Features =
            [
                new("記憶體", "四通道 DDR4 ECC"),
                new("CPU PCIe", "64x PCIe 3.0"),
                new("SATA", "8x SATA III"),
            ],
        };

        db["TRX40"] = new ChipsetProfile
        {
            Vendor = "AMD",
            OneLiner = "AMD TRX40 — 第三代 Threadripper (sTRX4)",
            NorthbridgeDescription = "已整合至 CPU：四通道 DDR4、64 條 PCIe 4.0",
            SouthbridgeDescription = "PCH TRX40：PCIe 4.0 x16，8 個 SATA，多 USB 3.2",
            Features =
            [
                new("記憶體", "四通道 DDR4 ECC"),
                new("CPU PCIe", "64x PCIe 4.0 (含 8x 連 PCH)"),
                new("PCH PCIe", "16x PCIe 4.0"),
                new("SATA", "8x SATA III"),
            ],
        };

        return db;
    }
}

// ==================================================================
//  資料模型
// ==================================================================

public sealed class ChipsetProfile
{
    public string Vendor { get; init; } = "";
    public string OneLiner { get; init; } = "";
    public string NorthbridgeDescription { get; init; } = "";
    public string SouthbridgeDescription { get; init; } = "";
    public List<ChipsetFeatureEntry> Features { get; init; } = [];
}

public sealed record ChipsetFeatureEntry(string Category, string Description);
