namespace XinSpect;

/// <summary>
/// 晶片組型號辨識與功能簡述。依已知代碼對主機板的南橋 / 北橋 / 型號字串做完整代碼比對，
/// 命中後給出繁體中文的平台功能概述。用於「主機板」分頁顯示「為何晶片組及其功能」。
/// </summary>
public static class ChipsetInfo
{
    public readonly record struct Entry(string Name, string Features);

    // 代碼須「由長到短」排列，避免短碼誤命中（例：X99 命中 X299、B60 命中 B660）。
    private static readonly (string Code, string Name, string Features)[] _table =
    {
        // ── Intel HEDT / 工作站 ──
        ("C422", "Intel C422", "LGA2066 Xeon W-2100／W-2200 工作站平台。CPU SKU 決定記憶體通道、ECC 與 PCIe 能力；主機板、BIOS 及處理器共同決定實際配置。"),
        ("X299", "Intel X299", "Basin Falls 高階桌面平台，搭配 LGA2066 Core X。Kaby Lake-X 與 Skylake-X／Cascade Lake-X 的記憶體通道及 PCIe 能力不同，須按 CPU SKU 判讀；PCH 資源也受主機板配置影響。"),
        ("C612", "Intel C612", "Grantley 工作站／伺服器晶片組，常見於 LGA2011-v3 Xeon E5 v3／v4。CPU SKU、主機板與 BIOS 決定插槽數、記憶體通道、ECC／RDIMM 與 PCIe 配置。"),
        ("X99",  "Intel X99",  "LGA2011-v3 Haswell-E／Broadwell-E 高階桌面平台。記憶體與 PCIe 能力依處理器 SKU；X99 主機板不能據此推定 Xeon E5 伺服器功能。"),
        ("X79", "Intel X79", "LGA2011 Sandy Bridge-E／Ivy Bridge-E 高階桌面平台；同代 Xeon E5 使用 C600 系列晶片組。CPU、主機板與 BIOS 決定記憶體及 PCIe 配置。"),
        ("X58", "Intel X58 Express", "LGA1366 Nehalem／Westmere 高階桌面與工作站平台；X58 北橋搭配 ICH10 系列南橋。實際 CPU、記憶體插槽與擴充配置由主機板決定。"),
        // ── Intel Xeon E5／Scalable ──
        ("C608", "Intel C608", "Intel C600 系列伺服器／工作站 PCH。與 C602／C604／C606 同屬一代平台；CPU SKU、主機板與 BIOS 決定記憶體、插槽及 I/O 能力。"),
        ("C606", "Intel C606", "Intel C600 系列伺服器／工作站 PCH。Xeon E5 能力依處理器與主機板配置，不由晶片組名稱單獨決定。"),
        ("C604", "Intel C604", "Intel C600 系列伺服器／工作站 PCH。Xeon E5 能力依處理器與主機板配置，不由晶片組名稱單獨決定。"),
        ("C602", "Intel C602", "Intel C600 系列伺服器／工作站 PCH，常見於 LGA2011 Xeon E5 v1／v2。CPU SKU、主機板與 BIOS 決定記憶體、插槽及 I/O 能力。"),
        ("C600", "Intel C600 系列", "韌體或裝置名稱只回報 C600 系列，無法據此分辨 C602／C604／C606／C608；不推定單一型號。"),
        ("C621A", "Intel C621A", "伺服器 PCH；不同插槽世代與 Xeon Scalable CPU 不可只憑相似型號互推。記憶體與 PCIe 能力須依 CPU SKU、主機板及 BIOS 核對。"),
        ("C621", "Intel C621", "Lewisburg 伺服器／工作站 PCH，常見於 LGA3647 Xeon Scalable 與部分 Xeon W 平台。CPU SKU、主機板及 BIOS 決定記憶體、插槽及 I/O 配置。"),
        ("C622", "Intel C622", "Lewisburg 伺服器／工作站 PCH。CPU SKU、主機板與 BIOS 決定記憶體、插槽及 I/O 配置。"),
        ("C624", "Intel C624", "Lewisburg 伺服器／工作站 PCH。CPU SKU、主機板與 BIOS 決定記憶體、插槽及 I/O 配置。"),
        ("C625", "Intel C625", "Lewisburg 伺服器／工作站 PCH。CPU SKU、主機板與 BIOS 決定記憶體、插槽及 I/O 配置。"),
        ("W790", "Intel W790", "Xeon W-2400／W-3400 工作站晶片組；不同 Xeon W SKU 的記憶體通道、ECC 與 PCIe 資源不同，須按處理器型號判讀。"),
        // ── AMD EPYC／Threadripper 工作站 ──
        ("WRX90", "AMD WRX90", "Threadripper PRO 工作站平台；處理器 SKU 決定記憶體與 PCIe 上限，實際插槽配置受主機板限制。"),
        ("WRX80", "AMD WRX80", "Threadripper PRO 工作站平台；處理器 SKU 決定記憶體與 PCIe 上限，實際插槽配置受主機板限制。"),
        ("SP5", "AMD SP5／EPYC", "EPYC SP5 平台；I/O 與記憶體控制器整合於處理器，通道、插槽與可用功能依 EPYC SKU、主機板及 BIOS。"),
        ("SP3", "AMD SP3／EPYC", "EPYC SP3 平台；I/O 與記憶體控制器整合於處理器，通道、插槽與可用功能依 EPYC SKU、主機板及 BIOS。"),
        ("EPYC", "AMD EPYC SoC 平台", "EPYC 將記憶體控制器與主要 I/O 整合於處理器；插槽、通道、NUMA 與 I/O 能力須依 EPYC SKU、主機板及 BIOS 核對，不能當作單一晶片組規格。"),
        // ── Intel 主流（LGA1700，12/13/14 代 Core）──
        ("Z790", "Intel Z790", "LGA1700 旗艦晶片組，支援第 12/13/14 代 Core 之 CPU 與記憶體超頻。雙通道 DDR5 / DDR4、CPU PCIe 5.0、PCH 最多 20 條 PCIe 4.0、USB 3.2 Gen2x2（20Gb/s）。"),
        ("Z690", "Intel Z690", "LGA1700 超頻晶片組，支援第 12/13 代 Core。雙通道 DDR5 / DDR4、PCIe 5.0、DMI 4.0 x8、USB 3.2 Gen2x2。"),
        ("B760", "Intel B760", "LGA1700 主流晶片組，支援記憶體超頻（CPU 不可超頻）。雙通道 DDR5 / DDR4、PCIe 5.0（CPU）、USB 3.2 Gen2x2。"),
        ("H770", "Intel H770", "LGA1700 中階晶片組，較 B760 提供更多 PCIe / USB 通道，支援記憶體超頻。"),
        ("B660", "Intel B660", "LGA1700 主流晶片組，支援記憶體超頻。雙通道 DDR5 / DDR4、PCIe 4.0。"),
        ("H610", "Intel H610", "LGA1700 入門晶片組，功能精簡，不支援超頻，僅 DDR5 / DDR4 單一時脈。"),
        // ── Intel 主流（LGA1200，10/11 代 Core）──
        ("Z590", "Intel Z590", "LGA1200 超頻晶片組，支援第 10/11 代 Core。雙通道 DDR4、第 11 代提供 PCIe 4.0、USB 3.2 Gen2x2。"),
        ("B560", "Intel B560", "LGA1200 主流晶片組，開放記憶體超頻。雙通道 DDR4、PCIe 4.0（11 代）。"),
        ("Z490", "Intel Z490", "LGA1200 超頻晶片組，支援第 10 代 Core（相容 11 代）。雙通道 DDR4。"),
        // ── AMD AM5（Ryzen 7000 以後）──
        ("X670E","AMD X670E", "AM5 旗艦晶片組（雙 PCH），全面 PCIe 5.0（顯示卡 + 儲存）。雙通道 DDR5、支援 CPU / 記憶體超頻與 EXPO。"),
        ("X670", "AMD X670",  "AM5 高階晶片組（雙 PCH），PCIe 5.0 儲存、顯示卡多為 5.0。雙通道 DDR5、支援超頻與 EXPO。"),
        ("B650E","AMD B650E", "AM5 中高階晶片組，顯示卡與儲存皆 PCIe 5.0。雙通道 DDR5、支援超頻與 EXPO。"),
        ("B650", "AMD B650",  "AM5 主流晶片組，PCIe 5.0 儲存。雙通道 DDR5、支援超頻與 EXPO。"),
        // ── AMD AM4（Ryzen 1000～5000）──
        ("X570", "AMD X570",  "AM4 旗艦晶片組，首發 PCIe 4.0（顯示卡 + 儲存）。雙通道 DDR4、支援超頻，PCH 常需主動散熱。"),
        ("B550", "AMD B550",  "AM4 主流晶片組，顯示卡 / 主 M.2 為 PCIe 4.0。雙通道 DDR4、支援 CPU 超頻。"),
        ("A520", "AMD A520",  "AM4 入門晶片組，PCIe 3.0，支援記憶體超頻但不支援 CPU 超頻。"),
        ("X470", "AMD X470",  "AM4 上一代高階晶片組，PCIe 3.0，支援超頻與 StoreMI。"),
        ("B450", "AMD B450",  "AM4 主流晶片組，PCIe 3.0，支援 CPU 超頻，相容性廣。"),
        // ── AMD sTRX4 / TR ──
        ("TRX40","AMD TRX40", "Threadripper（sTRX4）HEDT 平台，四通道 DDR4、大量 PCIe 4.0 通道，支援超頻。"),
    };

    /// <summary>依南橋 / 北橋 / 型號字串辨識晶片組；未命中時 Name 取南橋原字串、Features 留空。</summary>
    public static Entry Resolve(string? southbridge, string? northbridge, string? model)
    {
        string hay = $"{southbridge} {northbridge} {model}".ToUpperInvariant();
        foreach (var (code, name, features) in _table)
            if (ContainsCode(hay, code))
                return new Entry(name, features);

        // 未命中：沿用報告中的南橋字串（若有），無功能描述
        string fallback = !string.IsNullOrWhiteSpace(southbridge) && southbridge != "—"
            ? southbridge!
            : (!string.IsNullOrWhiteSpace(northbridge) && northbridge != "—" ? northbridge! : "—");
        return new Entry(fallback, "");
    }

    private static bool ContainsCode(string text, string code)
    {
        int start = 0;
        while ((start = text.IndexOf(code, start, StringComparison.Ordinal)) >= 0)
        {
            int end = start + code.Length;
            bool leftBoundary = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
            bool rightBoundary = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (leftBoundary && rightBoundary) return true;
            start = end;
        }

        return false;
    }
}
