namespace XinSpect;

/// <summary>溫度 / 負載的健康分級，對應 dataviz 的 status 配色（good/warning/serious/critical）。</summary>
public enum Severity { Neutral, Good, Warning, Serious, Critical }

/// <summary>系統 / 主機板 / BIOS 靜態資訊（開機時由 WMI 讀取一次）。</summary>
public sealed class SystemSummary
{
    public string OsName { get; set; } = "—";
    public string OsVersion { get; set; } = "—";
    public string OsArch { get; set; } = "—";
    public string HostName { get; set; } = "—";
    public string UserName { get; set; } = "—";
    public string InstallDate { get; set; } = "—";
    public string BootTime { get; set; } = "—";
    public string Uptime { get; set; } = "—";
    public string SystemManufacturer { get; set; } = "—";
    public string SystemModel { get; set; } = "—";
    public string BoardVendor { get; set; } = "—";
    public string BoardModel { get; set; } = "—";
    public string BoardVersion { get; set; } = "—";
    public string BoardSerial { get; set; } = "—";
    public string BiosVendor { get; set; } = "—";
    public string BiosVersion { get; set; } = "—";
    public string BiosDate { get; set; } = "—";
    public string SystemSku { get; set; } = "—";
    public string SystemUuid { get; set; } = "—";
    public string SystemType { get; set; } = "—";      // 例：x64-based PC
    public int LogicalProcessors { get; set; }         // ComputerSystem 邏輯處理器數

    public Brand BoardBrand => Brands.Resolve(BoardVendor, BoardModel);
}

/// <summary>處理器靜態規格（WMI）。即時數值放在 SensorService。</summary>
public sealed class CpuStatic
{
    public string Name { get; set; } = "—";
    public string Manufacturer { get; set; } = "—";
    public int Cores { get; set; }
    public int Threads { get; set; }
    public string Socket { get; set; } = "—";
    public double MaxClockMHz { get; set; }
    public string L2Cache { get; set; } = "—";
    public string L3Cache { get; set; } = "—";
    public string ProcessorId { get; set; } = "—";
    public string Description { get; set; } = "—";

    // ── 深度識別（由 WMI Win32_Processor 補入）──────────────────────────────
    public int EnabledCores { get; set; }             // 已啟用實體核心
    public string Family { get; set; } = "—";         // CPUID 家族（由 Description 解析）
    public string ModelNo { get; set; } = "—";        // CPUID 型號
    public string SteppingId { get; set; } = "—";     // CPUID 步進
    public string Revision { get; set; } = "—";       // 處理器修訂
    public int AddressWidth { get; set; }             // 位址寬度（位元）
    public int DataWidth { get; set; }                // 資料寬度（位元）
    public double ExtClockMHz { get; set; }           // 外頻 / 基準匯流排（MHz）
    public double CurrentVoltage { get; set; }        // WMI 回報電壓（V，粗略）
    public string Virtualization { get; set; } = "—"; // 韌體虛擬化 (VT-x/AMD-V) 開關
    public string Slat { get; set; } = "—";           // 二階位址轉譯 (EPT/NPT)
    public string Version { get; set; } = "—";        // 處理器版本字串

    public string AddressWidthText => AddressWidth > 0 ? $"{AddressWidth} 位元" : "—";
    public string DataWidthText => DataWidth > 0 ? $"{DataWidth} 位元" : "—";
    public string ExtClockText => ExtClockMHz > 0 ? $"{ExtClockMHz:0} MHz" : "—";
    public string CurrentVoltageText => CurrentVoltage > 0 ? $"{CurrentVoltage:0.###} V" : "—";
    public string EnabledCoresText => EnabledCores > 0 ? $"{EnabledCores} 核" : "—";

    public Brand Brand => Brands.Resolve(Manufacturer, Name, Description);
}

/// <summary>CPU 快取/拓撲的一列（由 Win32 GetLogicalProcessorInformationEx 匯總，全為真實系統資料）。</summary>
public sealed class CpuCacheRow
{
    public string Label { get; set; } = "—";    // 例：L1 資料快取
    public string Detail { get; set; } = "—";    // 例：8 × 32 KB ・ 8-way ・ 64 B 行 ・ 每核心獨立
}

/// <summary>CPU 拓撲與快取階層（Win32 原生查詢；不依賴 CPU-Z 報告，永遠可讀）。</summary>
public sealed class CpuTopology
{
    public bool Loaded { get; set; }
    public int PhysicalPackages { get; set; }
    public int PhysicalCores { get; set; }
    public int LogicalProcessors { get; set; }
    public int NumaNodes { get; set; }
    public int ProcessorGroups { get; set; }
    public bool Smt { get; set; }
    public List<CpuCacheRow> Caches { get; } = new();
    public List<string> Features { get; } = new();   // IsProcessorFeaturePresent 回報的平台能力

    public string PackagesText => PhysicalPackages > 0 ? $"{PhysicalPackages} 個" : "—";
    public string CoresText => PhysicalCores > 0 ? $"{PhysicalCores} 核" : "—";
    public string LogicalText => LogicalProcessors > 0 ? $"{LogicalProcessors} 執行緒" : "—";
    public string NumaText => NumaNodes > 0 ? $"{NumaNodes} 個節點" : "—";
    public string GroupsText => ProcessorGroups > 0 ? $"{ProcessorGroups} 組" : "—";
    public string SmtText => PhysicalCores > 0
        ? (Smt ? "啟用（同步多執行緒 / 超執行緒）" : "停用（每核心一執行緒）") : "—";
    public string FeaturesText => Features.Count > 0 ? string.Join("、", Features) : "—";
    public bool HasCaches => Caches.Count > 0;
}

/// <summary>單一記憶體模組（WMI Win32_PhysicalMemory）。</summary>
public sealed class MemoryModuleInfo
{
    public string Slot { get; set; } = "—";
    public string Manufacturer { get; set; } = "—";
    public string PartNumber { get; set; } = "—";
    public double CapacityGB { get; set; }
    public int ConfiguredSpeedMHz { get; set; }
    public int RatedSpeedMHz { get; set; }
    public string FormFactor { get; set; } = "—";
    public string MemoryType { get; set; } = "—";
    public string Voltage { get; set; } = "—";

    public string CapacityText => $"{CapacityGB:0.#} GB";
    public string SpeedText => ConfiguredSpeedMHz > 0
        ? $"{ConfiguredSpeedMHz} MHz" + (RatedSpeedMHz > ConfiguredSpeedMHz ? $"（額定 {RatedSpeedMHz}）" : "")
        : "—";

    public Brand Brand => Brands.Resolve(Manufacturer, PartNumber);
}

/// <summary>由 CPU-Z 報告解析出的即時記憶體時序。</summary>
public sealed class MemoryTimings : ObservableObject
{
    private string _status = "尚未讀取";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private bool _loaded;
    public bool Loaded { get => _loaded; set => SetProperty(ref _loaded, value); }

    public string MemoryTypeText { get; set; } = "—";
    public string ChannelsText { get; set; } = "—";
    public double DramFrequencyMHz { get; set; }
    public string DataRateText { get; set; } = "—";     // 例：DDR4-3600
    public string CL { get; set; } = "—";
    public string TRCD { get; set; } = "—";
    public string TRP { get; set; } = "—";
    public string TRAS { get; set; } = "—";
    public string TRFC { get; set; } = "—";
    public string CommandRate { get; set; } = "—";
    public string UncoreText { get; set; } = "—";

    // 次要時序與晶片組記憶體概況（CPU-Z Chipset 區塊）
    public string TCCD { get; set; } = "—";
    public string TCCDL { get; set; } = "—";
    public string TCCDWR { get; set; } = "—";
    public string TCCDWRL { get; set; } = "—";
    public string MemorySizeText { get; set; } = "—";
    public string HostBridge { get; set; } = "—";

    public string FrequencyText => DramFrequencyMHz > 0 ? $"{DramFrequencyMHz:0.#} MHz" : "—";
    /// <summary>時序摘要字串，例：16-22-22-38 2T。</summary>
    public string PrimaryTimingsText =>
        Loaded ? $"{CL}-{TRCD}-{TRP}-{TRAS}" + (CommandRate != "—" ? $"  {CommandRate}" : "") : "—";

    public void RaiseAll()
    {
        OnPropertyChanged(nameof(MemoryTypeText));
        OnPropertyChanged(nameof(ChannelsText));
        OnPropertyChanged(nameof(FrequencyText));
        OnPropertyChanged(nameof(DataRateText));
        OnPropertyChanged(nameof(CL));
        OnPropertyChanged(nameof(TRCD));
        OnPropertyChanged(nameof(TRP));
        OnPropertyChanged(nameof(TRAS));
        OnPropertyChanged(nameof(TRFC));
        OnPropertyChanged(nameof(CommandRate));
        OnPropertyChanged(nameof(UncoreText));
        OnPropertyChanged(nameof(TCCD));
        OnPropertyChanged(nameof(TCCDL));
        OnPropertyChanged(nameof(TCCDWR));
        OnPropertyChanged(nameof(TCCDWRL));
        OnPropertyChanged(nameof(MemorySizeText));
        OnPropertyChanged(nameof(HostBridge));
        OnPropertyChanged(nameof(PrimaryTimingsText));
    }
}

/// <summary>單一 CPU 核心的即時數值列。</summary>
public sealed class CoreRow : ObservableObject
{
    public CoreRow(string name) => Name = name;
    public string Name { get; }

    private double _clock;
    public double ClockMHz { get => _clock; set { if (SetProperty(ref _clock, value)) OnPropertyChanged(nameof(ClockText)); } }

    private double _load;
    public double LoadPercent { get => _load; set { if (SetProperty(ref _load, value)) OnPropertyChanged(nameof(LoadText)); } }

    private double? _temp;
    public double? TempC { get => _temp; set { if (SetProperty(ref _temp, value)) { OnPropertyChanged(nameof(TempText)); OnPropertyChanged(nameof(TempSeverity)); } } }

    public string ClockText => _clock > 0 ? $"{_clock:0} MHz" : "—";
    public string LoadText => $"{_load:0} %";
    public string TempText => _temp.HasValue ? $"{_temp:0} °C" : "—";
    public Severity TempSeverity => Health.Cpu(_temp);
}

/// <summary>顯示卡即時列。</summary>
public sealed class GpuRow : ObservableObject
{
    public GpuRow(string name) => Name = name;
    public string Name { get; }
    public string VendorText { get; set; } = "—";

    private double _coreClock; public double CoreClockMHz { get => _coreClock; set { if (SetProperty(ref _coreClock, value)) OnPropertyChanged(nameof(CoreClockText)); } }
    private double _memClock;  public double MemClockMHz  { get => _memClock;  set { if (SetProperty(ref _memClock, value)) OnPropertyChanged(nameof(MemClockText)); } }
    private double _load;      public double LoadPercent  { get => _load;      set { if (SetProperty(ref _load, value)) OnPropertyChanged(nameof(LoadText)); } }
    private double? _temp;     public double? TempC       { get => _temp;      set { if (SetProperty(ref _temp, value)) { OnPropertyChanged(nameof(TempText)); OnPropertyChanged(nameof(TempSeverity)); } } }
    private double _fan;       public double FanPercent   { get => _fan;       set { if (SetProperty(ref _fan, value)) OnPropertyChanged(nameof(FanText)); } }
    private double _vramUsed;  public double VramUsedMB   { get => _vramUsed;  set { if (SetProperty(ref _vramUsed, value)) OnPropertyChanged(nameof(VramText)); } }
    private double _vramTotal; public double VramTotalMB  { get => _vramTotal; set { if (SetProperty(ref _vramTotal, value)) OnPropertyChanged(nameof(VramText)); } }
    private double _power;     public double PowerW       { get => _power;     set { if (SetProperty(ref _power, value)) OnPropertyChanged(nameof(PowerText)); } }

    public string CoreClockText => _coreClock > 0 ? $"{_coreClock:0} MHz" : "—";
    public string MemClockText  => _memClock > 0 ? $"{_memClock:0} MHz" : "—";
    public string LoadText => $"{_load:0} %";
    public string TempText => _temp.HasValue ? $"{_temp:0} °C" : "—";
    public string FanText  => _fan > 0 ? $"{_fan:0} %" : "—";
    public string PowerText => _power > 0 ? $"{_power:0.#} W" : "—";
    public string VramText => _vramTotal > 0 ? $"{_vramUsed:0} / {_vramTotal:0} MB" : (_vramUsed > 0 ? $"{_vramUsed:0} MB" : "—");
    public Severity TempSeverity => Health.Gpu(_temp);
    public Brand Brand => Brands.Resolve(Name, VendorText);
}

/// <summary>儲存裝置即時列。</summary>
public sealed class StorageRow : ObservableObject
{
    public StorageRow(string name) => Name = name;
    public string Name { get; }

    private string _type = "—";
    public string TypeText { get => _type; set => SetProperty(ref _type, value); }

    private string _capacity = "—";
    public string CapacityText { get => _capacity; set => SetProperty(ref _capacity, value); }

    // HDD 由 S.M.A.R.T. 磁區狀態決定健康；SSD/NVMe 維持 Neutral（其健康由剩餘壽命於健康總評衍生）
    private Severity _health = Severity.Neutral;
    public Severity HealthSeverity { get => _health; set { if (SetProperty(ref _health, value)) OnPropertyChanged(nameof(LifeText)); } }
    public string HealthDetail { get; set; } = "";

    private double? _temp;   public double? TempC { get => _temp; set { if (SetProperty(ref _temp, value)) { OnPropertyChanged(nameof(TempText)); OnPropertyChanged(nameof(TempSeverity)); } } }
    private double? _life;   public double? RemainingLife { get => _life; set { if (SetProperty(ref _life, value)) OnPropertyChanged(nameof(LifeText)); } }
    private double? _used;   public double? UsedPercent { get => _used; set { if (SetProperty(ref _used, value)) OnPropertyChanged(nameof(UsedText)); } }

    private double _activity; public double ActivityPercent { get => _activity; set { if (SetProperty(ref _activity, value)) OnPropertyChanged(nameof(ActivityText)); } }
    /// <summary>此磁碟的活動時間（％）近 90 秒走勢，供「磁碟活動」曲線圖使用。</summary>
    public MetricHistory ActivityHist { get; } = new(90, "%", 100);
    public string ActivityText => $"{_activity:0} %";

    public string TempText => _temp.HasValue ? $"{_temp:0} °C" : "—";
    // SSD/NVMe 顯示剩餘壽命%；HDD 無壽命%，改以 S.M.A.R.T. 磁區狀態文字呈現
    public string LifeText => _life.HasValue ? $"{_life:0} %"
        : _health switch { Severity.Warning => "注意", Severity.Serious => "警示", Severity.Critical => "故障風險", Severity.Good => "良好", _ => "—" };
    public string UsedText => _used.HasValue ? $"{_used:0} %" : "—";
    public Severity TempSeverity => Health.Disk(_temp);
    public Brand Brand => Brands.Resolve(Name);

    // ── 深度靜態資訊（由 WMI Win32_DiskDrive 於 ApplyDiskInfo 併入，消除與獨立區塊的重複顯示）──
    public string Model { get; set; } = "—";
    public string SerialNumber { get; set; } = "—";
    public string Firmware { get; set; } = "—";
    public string InterfaceType { get; set; } = "—";
    public string BusText { get; set; } = "—";
    public string PartitionsText { get; set; } = "—";
    public string SectorText { get; set; } = "—";
    public string MediaType { get; set; } = "—";
    public string CountText { get; set; } = "—";
    /// <summary>顯示卡片時作為標題的裝置型號（無深度資料時退回感測器名稱）。</summary>
    public string DisplayModel => Model != "—" ? Model : Name;
    public string HealthText => string.IsNullOrEmpty(HealthDetail) ? LifeText : HealthDetail;
}

/// <summary>感測器總表中的一列（對應 LHM 的單一 ISensor）。</summary>
public sealed class SensorRow : ObservableObject
{
    public SensorRow(string group, string name, string typeText, string unit)
    {
        Group = group; Name = name; TypeText = typeText; Unit = unit;
    }
    public string Group { get; }
    public string Name { get; }
    public string TypeText { get; }
    public string Unit { get; }

    private string _value = "—";
    public string ValueText { get => _value; set => SetProperty(ref _value, value); }

    private string _min = "—";
    public string MinText { get => _min; set => SetProperty(ref _min, value); }

    private string _max = "—";
    public string MaxText { get => _max; set => SetProperty(ref _max, value); }
}

/// <summary>溫度分級門檻（集中管理，供 gauge 與資料列共用）。</summary>
public static class Health
{
    public static Severity Cpu(double? t) => Classify(t, 80, 90, 100);
    public static Severity Gpu(double? t) => Classify(t, 60, 75, 87);
    public static Severity Disk(double? t) => Classify(t, 45, 55, 65);

    public static Severity Load(double p)
        => p >= 95 ? Severity.Critical : p >= 80 ? Severity.Serious : p >= 50 ? Severity.Warning : Severity.Good;

    /// <summary>磁碟已用空間分級（較負載寬鬆：日常高占用屬正常）。</summary>
    public static Severity Space(double pct)
        => pct >= 95 ? Severity.Critical : pct >= 85 ? Severity.Serious : pct >= 70 ? Severity.Warning : Severity.Good;

    private static Severity Classify(double? v, double warn, double serious, double crit)
    {
        if (!v.HasValue) return Severity.Neutral;
        double t = v.Value;
        if (t >= crit) return Severity.Critical;
        if (t >= serious) return Severity.Serious;
        if (t >= warn) return Severity.Warning;
        return Severity.Good;
    }
}
