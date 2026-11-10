using System.Management;

namespace XinSpect;

/// <summary>
/// CPU 腳座 (Socket) 腳位參考資料庫。
/// 偵測目前處理器的腳座型號，並提供腳位分類與關鍵規格。
/// </summary>
public sealed class CpuPinoutService : ObservableObject
{
    // ── 可觀察屬性 ──────────────────────────────────────────

    private string _detectedSocket = "（偵測中…）";
    public string DetectedSocket { get => _detectedSocket; private set => SetProperty(ref _detectedSocket, value); }

    private string _socketName = "—";
    public string SocketName { get => _socketName; private set => SetProperty(ref _socketName, value); }

    private int _pinCount;
    public int PinCount { get => _pinCount; private set => SetProperty(ref _pinCount, value); }

    private string _mountType = "—";
    public string MountType { get => _mountType; private set => SetProperty(ref _mountType, value); }

    private string _description = "";
    public string Description { get => _description; private set => SetProperty(ref _description, value); }

    private string _generationNote = "";
    public string GenerationNote { get => _generationNote; private set => SetProperty(ref _generationNote, value); }

    private List<string> _keyFacts = [];
    public List<string> KeyFacts { get => _keyFacts; private set => SetProperty(ref _keyFacts, value); }

    private bool _isKnownSocket;
    public bool IsKnownSocket { get => _isKnownSocket; private set => SetProperty(ref _isKnownSocket, value); }

    // ── 建構 ────────────────────────────────────────────────

    public CpuPinoutService()
    {
        // WMI 查詢移到背景執行緒：本服務在 MainViewModel 建構時就 eager `= new()`，
        // 若在建構子同步查 WMI 會凍住整個 App 啟動。屬性全為純量／List（無 ObservableCollection），
        // 背景設值時 WPF 會自動把 PropertyChanged 封送回 UI 執行緒；DetectAndLoad 自帶 try/catch 不會逸出例外。
        _ = Task.Run(DetectAndLoad);
    }

    // ── 偵測 ────────────────────────────────────────────────

    void DetectAndLoad()
    {
        try
        {
            string raw = ReadWmiSocket();
            DetectedSocket = string.IsNullOrWhiteSpace(raw) ? "（WMI 未回傳）" : raw;
            ApplySocketData(NormalizeSocketName(raw));
        }
        catch
        {
            DetectedSocket = "（WMI 查詢失敗）";
            ApplySocketData(null);
        }
    }

    static string ReadWmiSocket()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT SocketDesignation FROM Win32_Processor");
        foreach (ManagementObject mo in searcher.Get())
        {
            var val = mo["SocketDesignation"]?.ToString();
            if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
        }
        return "";
    }

    // ── 名稱正規化 ──────────────────────────────────────────

    internal static string? NormalizeSocketName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string u = raw.ToUpperInvariant().Replace(" ", "");

        // Intel LGA 系列
        if (u.Contains("LGA1700") || u.Contains("1700")) return "LGA 1700";
        if (u.Contains("LGA1851") || u.Contains("1851")) return "LGA 1851";
        if (u.Contains("LGA1200") || u.Contains("1200")) return "LGA 1200";
        if (u.Contains("LGA1151") || u.Contains("1151")) return "LGA 1151";
        if (u.Contains("LGA2066") || u.Contains("2066")) return "LGA 2066";
        if (u.Contains("LGA2011-3") || u.Contains("LGA2011-V3") || u.Contains("LGA2011V3") || u.Contains("2011-3") || u.Contains("2011-V3") || u.Contains("2011V3") || u.Contains("SOCKETR3")) return "LGA 2011-v3";
        if (u.Contains("LGA2011") || u.Contains("2011") || u == "SOCKETR") return "LGA 2011";
        if (u.Contains("LGA1366") || u.Contains("1366")) return "LGA 1366";
        if (u.Contains("LGA4677") || u.Contains("4677")) return "LGA 4677";
        if (u.Contains("LGA3647") || u.Contains("3647")) return "LGA 3647";

        // AMD AM 系列
        if (u.Contains("AM5"))  return "AM5";
        if (u.Contains("AM4"))  return "AM4";
        if (u.Contains("SP5")) return "SP5";
        if (u.Contains("SP3")) return "SP3";
        if (u.Contains("TR4") || u.Contains("STRX4")) return "sTRX4 / TR4";

        return null;
    }

    // ── 腳座資料庫 ──────────────────────────────────────────

    void ApplySocketData(string? key)
    {
        if (key is null)
        {
            SocketName     = DetectedSocket;
            Description    = "此腳座不在內建資料庫中。";
            IsKnownSocket  = false;
            return;
        }

        var info = GetSocketInfo(key);
        SocketName     = info.Name;
        PinCount       = info.Pins;
        MountType      = info.Mount;
        Description    = info.Desc;
        GenerationNote = info.Gen;
        KeyFacts       = [.. info.Facts];
        IsKnownSocket  = true;
    }

    static SocketRecord GetSocketInfo(string key) => key switch
    {
        "LGA 1700" => new(
            Name:     "LGA 1700",
            Pins:     1700,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "Intel 第 12～14 代 Core（Alder Lake / Raptor Lake / Raptor Lake Refresh）桌上型處理器使用的腳座。",
            Gen:      "Alder Lake — Raptor Lake Refresh (2021–2024)",
            Facts:
            [
                "接觸面尺寸 45.0 × 37.5 mm，比 LGA 1200 長約 7.5 mm",
                "獨立負載板 (ILM) 改為四螺絲固定，不相容舊扣具",
                "支援 DDR4 與 DDR5 雙通道（視主機板而定）",
                "PCIe 5.0 x16 + PCIe 4.0 x4 由 CPU 直出",
                "供電針腳 (Vcore) 分佈於基板四周以降低迴路阻抗",
            ]),

        "LGA 1851" => new(
            Name:     "LGA 1851",
            Pins:     1851,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "Intel Arrow Lake 桌上型處理器使用的腳座，為 LGA 1700 的後繼者。",
            Gen:      "Arrow Lake (2024–)",
            Facts:
            [
                "針腳數由 1700 增加至 1851，接觸面積微幅擴大",
                "僅支援 DDR5，取消 DDR4 相容性",
                "PCIe 5.0 x16 + PCIe 4.0 通道配置",
                "散熱扣具孔位與 LGA 1700 共用，大部分塔扇可沿用",
                "分離式架構：CPU Tile + GPU Tile + SoC Tile + I/O Tile",
            ]),

        "LGA 1200" => new(
            Name:     "LGA 1200",
            Pins:     1200,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "Intel 第 10～11 代 Core（Comet Lake / Rocket Lake）桌上型處理器使用的腳座。",
            Gen:      "Comet Lake — Rocket Lake (2020–2021)",
            Facts:
            [
                "接觸面尺寸 37.5 × 37.5 mm，方形設計",
                "僅支援 DDR4 雙通道",
                "Rocket Lake 提供 PCIe 4.0 x20 由 CPU 直出",
                "散熱扣具與 LGA 115x 系列共用",
                "供電設計支援至 125W PBP / 最高 224W MTP",
            ]),

        "LGA 1151" => new(
            Name:     "LGA 1151",
            Pins:     1151,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "Intel 第 6～9 代 Core（Skylake 至 Coffee Lake Refresh）桌上型處理器使用的腳座。",
            Gen:      "Skylake — Coffee Lake Refresh (2015–2019)",
            Facts:
            [
                "v1 (100/200 系列) 與 v2 (300 系列) 電氣不相容，外觀相同",
                "僅支援 DDR4 雙通道（Skylake 初期有 DDR3L 版本）",
                "最高 PCIe 3.0 x16 由 CPU 直出",
                "六代至九代 Core 均使用此腳座，世代跨度最長之一",
            ]),

        "LGA 2066" => new(
            Name:     "LGA 2066",
            Pins:     2066,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "Intel Core X 高階桌上型與 Xeon W-2100／W-2200 工作站處理器使用的腳座；晶片組與 CPU 支援需按型號核對。",
            Gen:      "Kaby Lake-X／Skylake-X／Cascade Lake-X；Xeon W-2100／W-2200",
            Facts:
            [
                "記憶體通道與 PCIe lanes 依 CPU SKU；Kaby Lake-X 和 Core X 不能共用一組固定規格",
                "Core X 常見 X299；Xeon W-2100／W-2200 常見 C422，不能只按腳座推定晶片組",
                "ECC 與 Registered DIMM 支援依 Xeon W 型號、主機板與 BIOS",
                "接觸面尺寸 52.5 × 45.0 mm，需大型散熱方案",
            ]),

        "LGA 2011-v3" => new(
            Name:     "LGA 2011-v3",
            Pins:     2011,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "Intel Haswell-E／Broadwell-E 與 Xeon E5 v3／v4 使用的獨立電氣版本。",
            Gen:      "Haswell-E／Broadwell-E／Xeon E5 v3-v4",
            Facts:
            [
                "與 LGA 2011 v1 電氣不相容，不能只看相同針腳數判定相容",
                "桌面平台常見 X99；Xeon E5 平台常見 C612，仍須由主機板型號確認",
                "記憶體通道、ECC/RDIMM 與 PCIe 能力依 CPU SKU 和主機板",
            ]),

        "LGA 2011" => new(
            Name:     "LGA 2011（版本未明）",
            Pins:     2011,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "WMI 只回報 LGA 2011 時，無法單靠 SocketDesignation 分辨 v1 與 v3；須交叉核對 CPU 型號與晶片組。",
            Gen:      "v1／v3 未判定",
            Facts:
            [
                "LGA 2011 v1 與 v3 電氣不相容，不可只看相同針腳數判定相容",
                "v1 常見 X79／C600 系列並使用 DDR3；v3 常見 X99／C612 並使用 DDR4，需由 CPU／晶片組證據確認",
                "記憶體通道、ECC／RDIMM 與 PCIe 能力依處理器 SKU 和主機板",
                "ILM 為窄型與寬型兩種，對應不同散熱安裝方式",
            ]),

        "LGA 1366" => new(
            Name:     "LGA 1366",
            Pins:     1366,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "Intel Nehalem／Westmere 桌上型、工作站與伺服器處理器使用的腳座。",
            Gen:      "Nehalem／Westmere；Xeon 5500／5600 系列",
            Facts:
            [
                "X58 桌面平台與 5500／5520 系列伺服器晶片組平台是不同主機板設計",
                "CPU 型號決定記憶體控制器及 QPI 能力；主機板決定插槽與通道佈線",
                "不與後續 LGA 2011 系列相容",
            ]),

        "LGA 4677" => new(
            Name:     "LGA 4677",
            Pins:     4677,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "Intel Xeon Scalable 與 Xeon W-2400／W-3400 工作站處理器使用的腳座。",
            Gen:      "Xeon Scalable Sapphire Rapids／Emerald Rapids；Xeon W-2400／W-3400",
            Facts:
            [
                "記憶體通道、HBM、PCIe 與 CXL 能力依 Xeon 型號與平台配置",
                "伺服器常見 E/F/P 系列平台；Xeon W-2400／W-3400 使用 W790 工作站平台",
                "LGA 4677 腳座相同不代表不同 Xeon 系列或主機板可互換",
            ]),

        "LGA 3647" => new(
            Name:     "LGA 3647",
            Pins:     3647,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "Intel Xeon Scalable 與部分 Xeon W-3200 工作站處理器使用的腳座。",
            Gen:      "Skylake-SP／Cascade Lake-SP／Cooper Lake；Xeon W-3200",
            Facts:
            [
                "記憶體通道、容量及 PCIe 能力依處理器型號與平台配置",
                "C621 常見於 Xeon Scalable／部分 Xeon W 平台；須按主機板與 CPU SKU 核對",
                "窄型 (P) 與方型 (Square) 兩種 ILM",
            ]),

        "AM5" => new(
            Name:     "AM5 (LGA 1718)",
            Pins:     1718,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "AMD Ryzen 7000 系列以後桌上型處理器使用的腳座，為 AM4 之後首次改用 LGA 封裝。",
            Gen:      "Zen 4 — Zen 5+ (2022–)",
            Facts:
            [
                "AMD 首款消費級 LGA 腳座，針腳在主機板而非 CPU",
                "僅支援 DDR5 雙通道",
                "CPU 直出 PCIe 5.0 x16（顯示卡）+ PCIe 5.0 x4（M.2）",
                "整合 RDNA 2 內顯（所有 SKU）",
                "散熱扣具與 AM4 相容，無需更換",
                "接觸面尺寸 40.0 × 40.0 mm",
            ]),

        "AM4" => new(
            Name:     "AM4 (PGA 1331)",
            Pins:     1331,
            Mount:    "PGA（針腳柵格陣列，針腳在 CPU）",
            Desc:     "AMD Ryzen 1000～5000 系列與 Athlon 桌上型處理器使用的腳座，壽命最長的消費級平台之一。",
            Gen:      "Zen — Zen 3+ (2017–2022)",
            Facts:
            [
                "PGA 封裝：針腳在 CPU 上，主機板為孔洞",
                "支援 DDR4 雙通道",
                "CPU 直出 PCIe 4.0 x16（Zen 2 起）或 PCIe 3.0 x16（Zen / Zen+）",
                "同一腳座橫跨五個微架構世代（Zen 至 Zen 3+）",
                "散熱扣具沿用 AM3 / AM2 規格，安裝孔距不變",
                "拔取散熱器時需注意黏住 CPU 的情形（建議先暖機）",
            ]),

        "SP3" => new(
            Name:     "SP3",
            Pins:     4094,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "AMD EPYC 7000 系列伺服器處理器使用的 SP3 腳座。",
            Gen:      "EPYC Naples／Rome／Milan",
            Facts:
            [
                "EPYC 7000 系列的記憶體、I/O 與插槽能力依處理器 SKU、主機板及 BIOS",
                "SP3 與 TR4／sTRX4 腳座名稱相近但平台用途及電氣定義不同",
            ]),

        "SP5" => new(
            Name:     "SP5 (LGA 6096)",
            Pins:     6096,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "AMD EPYC 9000 系列伺服器處理器使用的 SP5 腳座。",
            Gen:      "EPYC Genoa／Bergamo 及後續相容系列",
            Facts:
            [
                "記憶體通道、容量、PCIe／CXL 與插槽能力依 EPYC SKU、主機板及 BIOS",
                "SP5 不與 SP3、SP6 或 Threadripper 腳座互換",
            ]),

        "sTRX4 / TR4" => new(
            Name:     "sTRX4 / TR4",
            Pins:     4094,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "AMD Threadripper HEDT 處理器使用的 TR4／sTRX4 腳座系列。",
            Gen:      "Threadripper Zen／Zen+／Zen 2",
            Facts:
            [
                "TR4、sTRX4、SP3 外觀與名稱相近但平台及電氣定義不同",
                "記憶體通道與 PCIe 能力依 Threadripper 型號及主機板",
                "TR4 與 sTRX4 也需依 CPU、主機板支援列表確認相容",
            ]),

        _ => new(
            Name: key, Pins: 0, Mount: "—", Desc: "此腳座不在內建資料庫中。",
            Gen: "—", Facts: []),
    };

    // ── 內部記錄型別 ────────────────────────────────────────

    sealed record SocketRecord(
        string Name, int Pins, string Mount, string Desc, string Gen,

        List<string> Facts);
}
