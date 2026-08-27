namespace XinSpect;

// 由 CPU-Z 報告解析出的深度規格模型。皆為「讀取一次後整體指派」給檢視模型屬性，
// 故以一般可讀寫屬性呈現即可（指派新物件即觸發繫結更新），毋須逐欄通知。

/// <summary>處理器深度規格（CPU-Z，超出 WMI 的細節）。</summary>
public sealed class CpuDetail
{
    public bool Loaded { get; set; }

    public string Codename { get; set; } = "—";
    public string Specification { get; set; } = "—";
    public string Package { get; set; } = "—";
    public string Technology { get; set; } = "—";
    public string CpuId { get; set; } = "—";
    public string ExtCpuId { get; set; } = "—";
    public string Stepping { get; set; } = "—";
    public string Microcode { get; set; } = "—";
    public string CoreSpeed { get; set; } = "—";
    public string Multiplier { get; set; } = "—";
    public string StockFreq { get; set; } = "—";
    public string TdpLimit { get; set; } = "—";
    public string Tjmax { get; set; } = "—";
    public string CoreVoltage { get; set; } = "—";

    public string MaxNonTurbo { get; set; } = "—";
    public string MaxTurbo { get; set; } = "—";
    public string MaxEfficiency { get; set; } = "—";
    public string PowerMaxPl1 { get; set; } = "—";
    public string Pl1Window { get; set; } = "—";
    public string PowerMaxPl2 { get; set; } = "—";

    public string L1D { get; set; } = "—";
    public string L1I { get; set; } = "—";
    public string L2 { get; set; } = "—";
    public string L3 { get; set; } = "—";

    public string Instructions { get; set; } = "—";

    /// <summary>各核心數對應的 Turbo 倍頻（例："2 核　42x"）。</summary>
    public List<string> TurboRatios { get; } = new();
    public bool HasTurboRatios => TurboRatios.Count > 0;
}

/// <summary>主機板 / BIOS / 晶片組 / LPCIO 深度規格（CPU-Z + WMI 廠商）。</summary>
public sealed class MainboardDetail
{
    public bool Loaded { get; set; }

    public string Vendor { get; set; } = "—";   // 由 WMI 補入（CPU-Z 該欄常為代碼）
    public string Model { get; set; } = "—";

    public string Uefi { get; set; } = "—";
    public string BiosVendor { get; set; } = "—";
    public string BiosMsg { get; set; } = "—";
    public string BiosDate { get; set; } = "—";

    public string Northbridge { get; set; } = "—";
    public string Southbridge { get; set; } = "—";
    public string BusSpec { get; set; } = "—";
    public string GraphicInterface { get; set; } = "—";
    public string PcieLinkWidth { get; set; } = "—";
    public string PcieLinkSpeed { get; set; } = "—";
    public string MemoryType { get; set; } = "—";
    public string MemorySize { get; set; } = "—";
    public string Channels { get; set; } = "—";

    public string LpcioVendor { get; set; } = "—";
    public string LpcioModel { get; set; } = "—";

    public Brand Brand => Brands.Resolve(Vendor, Model);

    // 晶片組辨識（依南橋 / 北橋 / 型號代碼比對）與其功能簡述
    public string ChipsetName => ChipsetInfo.Resolve(Southbridge, Northbridge, Model).Name;
    public string ChipsetFeatures => ChipsetInfo.Resolve(Southbridge, Northbridge, Model).Features;
    public bool HasChipsetFeatures => ChipsetFeatures.Length > 0;
}

/// <summary>顯示卡深度規格（CPU-Z 報告，對應 GPU-Z 級別欄位）。</summary>
public sealed class GpuDetail
{
    public bool Primary { get; set; }
    public string Name { get; set; } = "—";
    public string BoardManufacturer { get; set; } = "—";
    public string BoardPartNumber { get; set; } = "—";
    public string Revision { get; set; } = "—";
    public string Codename { get; set; } = "—";
    public string CoreFamily { get; set; } = "—";
    public string Technology { get; set; } = "—";
    public string Cores { get; set; } = "—";
    public string RopUnits { get; set; } = "—";
    public string TmUnits { get; set; } = "—";
    public string MemoryType { get; set; } = "—";
    public string MemorySize { get; set; } = "—";
    public string MemoryBusWidth { get; set; } = "—";
    public string VendorId { get; set; } = "—";
    public string ModelId { get; set; } = "—";
    public string RevisionId { get; set; } = "—";
    public string BaseCoreClock { get; set; } = "—";
    public string BaseMemClock { get; set; } = "—";
    public string BoostCoreClock { get; set; } = "—";
    public string BoostMemClock { get; set; } = "—";
    public string PowerLimit { get; set; } = "—";
    public string ThermalLimit { get; set; } = "—";
    public string DriverVersion { get; set; } = "—";
    public string Wddm { get; set; } = "—";

    public Brand Brand => Brands.Resolve(Name, BoardManufacturer);
    public string Title => Primary ? $"{Name}（主要）" : Name;
}

/// <summary>NVML 深度資訊的單一欄位（標籤＋值），只在 NVML 回傳成功時建立（失敗即誠實省略）。</summary>
public sealed class GpuNvmlField
{
    public GpuNvmlField(string label, string value) { Label = label; Value = value; }
    public string Label { get; }
    public string Value { get; }
}

/// <summary>NVML 深度資訊的一個分組（例：識別、PCI Express、時脈、功耗與溫度）。</summary>
public sealed class GpuNvmlGroup
{
    public GpuNvmlGroup(string title, List<GpuNvmlField> fields) { Title = title; Fields = fields; }
    public string Title { get; }
    public List<GpuNvmlField> Fields { get; }
}

/// <summary>SPD 時序表的一列（JEDEC / XMP 皆用）。</summary>
public sealed class SpdTiming
{
    public string Label { get; set; } = "";   // 例："XMP #12"
    public string Values { get; set; } = "";  // 例："20.0-28-28-64-94-n.a @ 2000 MHz (1.400 Volts)"
}

/// <summary>單一 XMP / EXPO 效能設定檔。</summary>
public sealed class XmpProfile
{
    public string Name { get; set; } = "";          // 例："XMP-4000"
    public string Specification { get; set; } = "—";
    public string Voltage { get; set; } = "—";
    public string MaxCL { get; set; } = "—";
    public List<SpdTiming> Timings { get; } = new();
    public bool HasTimings => Timings.Count > 0;
}

/// <summary>單一記憶體模組的 SPD 深度資料（CPU-Z 每條 DIMM）。</summary>
public sealed class SpdModule
{
    public string Slot { get; set; } = "";           // 例："DIMM #1"
    public string MemoryType { get; set; } = "—";
    public string ModuleFormat { get; set; } = "—";
    public string Manufacturer { get; set; } = "—";
    public string Size { get; set; } = "—";
    public string MaxBandwidth { get; set; } = "—";
    public string MaxJedec { get; set; } = "—";
    public string PartNumber { get; set; } = "—";
    public string ManufacturingDate { get; set; } = "—";
    public string NominalVoltage { get; set; } = "—";
    public string Xmp { get; set; } = "—";

    public List<SpdTiming> Jedec { get; } = new();
    public List<XmpProfile> XmpProfiles { get; } = new();

    public Brand Brand => Brands.Resolve(Manufacturer, PartNumber);
    public bool HasJedec => Jedec.Count > 0;
    public bool HasXmp => XmpProfiles.Count > 0;
    public string XmpSummary => HasXmp ? string.Join(" ・ ", XmpProfiles.Select(p => p.Name)) : "無";
    public string Header => $"{Slot}　{Manufacturer}　{PartNumber}".Replace("  ", " ").Trim();
}

/// <summary>已安裝的實體網路卡（WMI Win32_NetworkAdapter，含未連線者）。</summary>
public sealed class NicInfo
{
    public string Name { get; set; } = "—";
    public string Manufacturer { get; set; } = "—";
    public string TypeText { get; set; } = "—";
    public string SpeedText { get; set; } = "—";
    public string Mac { get; set; } = "—";

    public Brand Brand => Brands.Resolve(Manufacturer, Name);
}

/// <summary>CPU-Z 報告的整體解析結果。</summary>
public sealed class CpuzReport
{
    public bool Ran { get; set; }
    public string Status { get; set; } = "尚未讀取";

    public MemoryTimings Timings { get; set; } = new();
    public CpuDetail Cpu { get; set; } = new();
    public MainboardDetail Board { get; set; } = new();
    public List<SpdModule> Spd { get; } = new();
    public List<GpuDetail> Gpus { get; } = new();
}
