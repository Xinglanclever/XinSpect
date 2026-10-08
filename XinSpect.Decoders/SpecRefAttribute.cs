namespace XinSpect;

/// <summary>
/// 規格引用（V7 M1／WP44）：標注某個解碼方法／型別所依據的規格出處——文件、章節、暫存器、位元位置。
/// 「讓不猜變成可稽核」的型別承載：<b>沒有 SpecRef 的欄位＝未驗證</b>（V7 §12.11），
/// 覆蓋面由 Tests/SpecRefCoverageTests.cs 以反射機器檢查，解碼器新增方法而未附引用會直接紅燈。
/// 引用格式建議：「文件名稱, 位置（暫存器／位元／位址）；交叉核對來源」。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Struct,
    AllowMultiple = true)]
public sealed class SpecRefAttribute : Attribute
{
    public SpecRefAttribute(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new ArgumentException("規格引用不可空——空的引用等於沒有引用", nameof(reference));
        Reference = reference;
    }

    public string Reference { get; }
}

/// <summary>SpecRef 覆蓋面的單一齣處：要機器檢查哪些解碼器、讀出引用內容，都走這裡。</summary>
public static class SpecRefRegistry
{
    /// <summary>已納入機器檢查的解碼器（深層暫存器＋感測器＋環境解碼）。新解碼器在此註冊後即受覆蓋檢查約束。</summary>
    public static readonly Type[] CoveredDecoders =
    [
        typeof(SpiFlash),        // PCH SPI 快閃暫存器
        typeof(ChipsetSecurity), // BIOS_CNTL／SMRAMC／HFSTS1
        typeof(PlatformSecurity),// FEATURE_CONTROL／DEBUG_INTERFACE MSR
        typeof(PcieAer),         // PCIe AER 擴充能力
        typeof(AcpiTable),       // ACPI 表頭／MCFG／HEST／BERT
        typeof(Cmos),            // CMOS/RTC（MC146818 佈局）
        typeof(Tsod),            // TSE2004 記憶體溫度感測器
        typeof(SuperIo),         // Super I/O 晶片 ID
        typeof(PlatformTrustDecoder), // VBS/HVCI/CodeIntegrity 狀態解碼
        typeof(PciKnowledge),    // PCI 類別碼／廠商 ID 知識表（PCI-SIG 規格）
        typeof(PciBars),         // PCI BAR 資源解碼（PCI Local Bus Spec §6.2.5）
        typeof(SuperIoKnowledge),// Super I/O 晶片名稱對照（coreboot superiotool）
        typeof(WifiBssDecoder),  // WLAN_BSS_ENTRY／頻道換算（wlanapi.h＋IEEE 802.11）
        typeof(MonitorConnectionDecoder), // 螢幕連接介面碼（WMI VideoOutputTechnology）
        typeof(SuperIoHwmDecoder), // SuperIO HWM 感測器（ITE datasheet 公式）
        typeof(CpuTopologyDecoder), // CPUID 0x1F die 拓撲（Intel SDM）
        typeof(SlitDecoder),     // ACPI SLIT 節點距離矩陣
        typeof(PmuDecoder),      // PMU 能力探索（CPUID 0xA，唯讀）
        typeof(TrendSentinel),   // 事實時序的趨勢／變化點／相關性（Theil–Sen、CUSUM、Pearson）
        typeof(EntropyMap),      // 位元組序列的 Shannon 熵分析（內容類型判別，非判決）
        typeof(PcieLink),        // PCIe 鏈路速度／寬度／埠類別代碼（PCIe Base Spec）
        typeof(PcieNegotiationGap), // PCIe 鏈路落差判讀（能力 vs 現況，唯讀不寫暫存器）
        typeof(VirtualizationJudge), // 虛擬化平台三態判讀（元件／服務／虛擬層分離）
        typeof(NicLinkGap),      // 網卡落差（PCIe 供給 vs 線路速率）
        typeof(DisplayAdapterJudge), // 顯示轉接器真偽（實體 vs 軟體 vs 基本顯示驅動）
        typeof(DimmEccJudge),    // 記憶體 ECC／Registered／平台更正能力（三層分離）
        typeof(MemoryChannelJudge), // 記憶體通道配置（插槽命名推斷 vs 每通道模組數）
        typeof(MonitorJudge),    // 顯示器真偽（EDID 支撐 vs 軟體合成 vs 預設物件）
        typeof(StorageQdJudge),  // 儲存佇列深度掃描（IOPS／延遲隨 QD 曲線）
    ];

    /// <summary>列舉解碼器上缺 SpecRef 的公開靜態方法（宣告於本型別者）。</summary>
    public static IReadOnlyList<string> MethodsMissingRefs()
    {
        var missing = new List<string>();
        foreach (var type in CoveredDecoders)
        {
            foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (!m.GetCustomAttributes(typeof(SpecRefAttribute), inherit: false).Any())
                    missing.Add($"{type.Name}.{m.Name}");
            }
        }
        return missing;
    }

    /// <summary>全部引用條目（方法上的直接引用；允許多條）。用於覆蓋率報告與「引用非空」檢查。</summary>
    public static IReadOnlyList<(string Member, string Reference)> AllReferences()
    {
        var refs = new List<(string, string)>();
        foreach (var type in CoveredDecoders)
        {
            foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            {
                foreach (SpecRefAttribute a in m.GetCustomAttributes(typeof(SpecRefAttribute), inherit: false))
                    refs.Add(($"{type.Name}.{m.Name}", a.Reference));
            }
        }
        return refs;
    }
}
