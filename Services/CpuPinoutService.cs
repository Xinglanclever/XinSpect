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
        DetectAndLoad();
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

    static string? NormalizeSocketName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string u = raw.ToUpperInvariant().Replace(" ", "");

        // Intel LGA 系列
        if (u.Contains("LGA1700") || u.Contains("1700")) return "LGA 1700";
        if (u.Contains("LGA1851") || u.Contains("1851")) return "LGA 1851";
        if (u.Contains("LGA1200") || u.Contains("1200")) return "LGA 1200";
        if (u.Contains("LGA1151") || u.Contains("1151")) return "LGA 1151";
        if (u.Contains("LGA2066") || u.Contains("2066")) return "LGA 2066";
        if (u.Contains("LGA2011") || u.Contains("2011")) return "LGA 2011";
        if (u.Contains("LGA4677") || u.Contains("4677")) return "LGA 4677";
        if (u.Contains("LGA3647") || u.Contains("3647")) return "LGA 3647";

        // AMD AM 系列
        if (u.Contains("AM5"))  return "AM5";
        if (u.Contains("AM4"))  return "AM4";
        if (u.Contains("SP5"))  return "SP5";
        if (u.Contains("SP3") || u.Contains("TR4") || u.Contains("STRX4"))
            return "sTRX4 / TR4 / SP3";

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
            Desc:     "Intel Core X / 高階桌上型 (HEDT) 平台使用的腳座。",
            Gen:      "Skylake-X — Cascade Lake-X (2017–2019)",
            Facts:
            [
                "四通道 DDR4，最高支援 256 GB",
                "CPU 直出 PCIe 3.0 最多 48 lanes（含 DMI）",
                "搭配 X299 晶片組，支援 RAID 與 Thunderbolt",
                "處理器 TDP 自 140W 至 255W 不等",
                "接觸面尺寸 52.5 × 45.0 mm，需大型散熱方案",
            ]),

        "LGA 2011" => new(
            Name:     "LGA 2011 / 2011-v3",
            Pins:     2011,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "Intel Sandy Bridge-E 至 Broadwell-E HEDT 與 Xeon E5 系列使用的腳座。",
            Gen:      "Sandy Bridge-E — Broadwell-E / Xeon E5 (2011–2016)",
            Facts:
            [
                "v1 與 v3 電氣不相容，v3 搭配 X99 / C612 晶片組",
                "四通道 DDR3 (v1) 或 DDR4 (v3)",
                "CPU 直出 PCIe 3.0 最多 40 lanes",
                "ILM 為窄型與寬型兩種，對應不同散熱安裝方式",
            ]),

        "LGA 4677" => new(
            Name:     "LGA 4677",
            Pins:     4677,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "Intel Sapphire Rapids / Emerald Rapids Xeon Scalable 伺服器處理器使用的腳座。",
            Gen:      "Sapphire Rapids — Emerald Rapids Xeon (2023–)",
            Facts:
            [
                "八通道 DDR5，支援 HBM（部分 SKU）",
                "CPU 直出最多 80 lanes PCIe 5.0",
                "支援 CXL 1.1/2.0 記憶體擴展",
                "基板尺寸為消費級的數倍，散熱需求 350W+",
            ]),

        "LGA 3647" => new(
            Name:     "LGA 3647",
            Pins:     3647,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "Intel Skylake-SP / Cascade Lake-SP Xeon Scalable 伺服器處理器使用的腳座。",
            Gen:      "Skylake-SP — Cascade Lake-SP / Cooper Lake (2017–2020)",
            Facts:
            [
                "六通道 DDR4，最高支援 1.5 TB (per socket)",
                "CPU 直出最多 48 lanes PCIe 3.0",
                "窄型 (P) 與方型 (Square) 兩種 ILM",
                "TDP 從 70W 至 280W 不等",
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

        "sTRX4 / TR4 / SP3" => new(
            Name:     "sTRX4 / TR4 / SP3",
            Pins:     4094,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "AMD Threadripper 與 EPYC 系列高階處理器使用的大型腳座家族。",
            Gen:      "Zen — Zen 3 Threadripper / EPYC (2017–2022)",
            Facts:
            [
                "四通道 DDR4（Threadripper）/ 八通道 DDR4（EPYC）",
                "CPU 直出最多 128 lanes PCIe 4.0（EPYC 7003）",
                "基板尺寸遠大於消費級，需專用散熱方案",
                "TR4 → sTRX4 電氣不完全相容，需注意主機板支援列表",
                "SP3 用於伺服器 EPYC，腳位定義略有不同",
            ]),

        "SP5" => new(
            Name:     "SP5 (LGA 6096)",
            Pins:     6096,
            Mount:    "LGA（平面柵格陣列）",
            Desc:     "AMD EPYC 9004 (Genoa / Bergamo) 伺服器處理器使用的腳座。",
            Gen:      "Zen 4 EPYC 9004 — Zen 5 EPYC (2022–)",
            Facts:
            [
                "十二通道 DDR5，單插槽最高支援 6 TB",
                "CPU 直出最多 128 lanes PCIe 5.0 + CXL 2.0",
                "目前消費 / 伺服器級別中針腳數最多的 x86 腳座",
                "TDP 從 200W 至 400W 不等",
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
