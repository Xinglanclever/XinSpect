namespace XinSpect;

/// <summary>
/// 晶片組型號辨識與功能簡述。依已知代碼對主機板的南橋 / 北橋 / 型號字串做子字串比對，
/// 命中後給出繁體中文的平台功能概述。用於「主機板」分頁顯示「為何晶片組及其功能」。
/// </summary>
public static class ChipsetInfo
{
    public readonly record struct Entry(string Name, string Features);

    // 代碼須「由長到短」排列，避免短碼誤命中（例：X99 命中 X299、B60 命中 B660）。
    private static readonly (string Code, string Name, string Features)[] _table =
    {
        // ── Intel HEDT / 工作站 ──
        ("X299", "Intel X299", "Basin Falls 高階桌面 / 工作站平台，搭配 LGA2066 Core X 系列（Skylake-X / Cascade Lake-X）。四通道 DDR4、CPU 直連最多 44 條 PCIe 3.0；PCH 另提供最多 24 條 PCIe 3.0、8 個 SATA 6Gb/s 與原生 USB 3.1 Gen2，支援 CPU 與記憶體超頻。"),
        ("C621", "Intel C621", "Lewisburg 伺服器晶片組，搭配 LGA3647 Xeon Scalable。支援六通道 DDR4 ECC RDIMM、大量 PCIe 3.0 通道與整合式 QAT / 網路加速。"),
        ("X99",  "Intel X99",  "Haswell-E / Broadwell-E（LGA2011-3）HEDT 平台。四通道 DDR4、CPU 直連 40 條 PCIe 3.0，支援超頻。"),
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
            if (hay.Contains(code, StringComparison.Ordinal))
                return new Entry(name, features);

        // 未命中：沿用報告中的南橋字串（若有），無功能描述
        string fallback = !string.IsNullOrWhiteSpace(southbridge) && southbridge != "—"
            ? southbridge!
            : (!string.IsNullOrWhiteSpace(northbridge) && northbridge != "—" ? northbridge! : "—");
        return new Entry(fallback, "");
    }
}
