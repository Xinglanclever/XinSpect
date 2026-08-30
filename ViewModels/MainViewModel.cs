using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace XinSpect;

/// <summary>
/// 主檢視模型：整個應用程式的資料繫結門面。
/// </summary>
/// <remarks>
/// 2.0 把原先塞在本類別裡的三大段行為拆了出去，只留下繫結表面與少量門面方法：
/// <list type="bullet">
/// <item><see cref="StartupSequence"/>：開機分階段偵測與載入（WMI／感測器／磁碟／色域／超頻引擎／深度規格）。</item>
/// <item><see cref="MetricsPump"/>：每秒的感測輪詢、走勢推入、記錄與警示。</item>
/// <item><see cref="AiSnapshotBuilder"/>：交給 AI 的硬體＋即時數據文字快照。</item>
/// </list>
/// 各屬性 setter 放寬為 internal 即為此拆分服務；對外（XAML 繫結）的路徑與名稱完全未變。
/// </remarks>
public sealed class MainViewModel : ObservableObject
{
    private MetricsPump? _pump;

    private SensorService? _live;
    public SensorService? Live { get => _live; internal set => SetProperty(ref _live, value); }

    private NetworkService? _net;
    public NetworkService? Net { get => _net; internal set => SetProperty(ref _net, value); }

    private SystemSummary _system = new();
    public SystemSummary System { get => _system; internal set => SetProperty(ref _system, value); }

    private CpuStatic _cpu = new();
    public CpuStatic Cpu { get => _cpu; internal set => SetProperty(ref _cpu, value); }

    private CpuTopology _cpuTopology = new();
    public CpuTopology CpuTopology { get => _cpuTopology; internal set => SetProperty(ref _cpuTopology, value); }

    public ObservableCollection<MemoryModuleInfo> Modules { get; } = new();

    /// <summary>磁碟區容量（甜甜圈圖 / 健康總評用）。開機即建立，之後每數秒更新已用量。</summary>
    public VolumeService Volumes { get; } = new();

    /// <summary>健康總評：每秒由即時感測值 + 磁碟容量彙整為狀態燈與綜合分數。</summary>
    public HealthReport Health { get; } = new();

    private UpgradeReport _upgrade = new();
    /// <summary>
    /// 升級建議：由本機讀值推出的瓶頸判定與依優先度排序的建議清單。
    /// </summary>
    /// <remarks>
    /// 不隨每秒心跳重算——它的輸入包含歷史統計，秒級更新只會讓文字跳動而不會更準；
    /// 改由健康總評頁進入時與使用者按「重新分析」時各算一次（<see cref="RefreshUpgrade"/>）。
    /// 初始值是一份空分析：在硬體偵測完成前它會誠實說「資料不足」，而不是先給一個猜出來的結論。
    /// </remarks>
    public UpgradeReport Upgrade { get => _upgrade; private set => SetProperty(ref _upgrade, value); }

    /// <summary>依當下讀值重算升級建議。失敗時保留上一份結果，不清成空白。</summary>
    public void RefreshUpgrade()
    {
        try { Upgrade = UpgradeAdvisor.Analyze(UpgradeFactsCollector.Collect(this)); }
        catch { /* 取值失敗就沿用上一份分析 */ }
    }

    /// <summary>
    /// 跑分紀錄簿：本機歷次成績的落地紀錄，是曦覽唯一承認的跑分基準。
    /// </summary>
    /// <remarks>
    /// <see cref="Bench"/>、<see cref="Chess"/>、<see cref="SuperPi"/> 共用同一實例（於建構式指派，
    /// 初始式先於建構式執行故必定已就緒），如此才不會出現多份物件同時讀寫同一個檔案。
    /// </remarks>
    public BenchLog Benchmarks { get; } = new();

    /// <summary>綜合效能測試（單/多執行緒運算 + 記憶體頻寬）。使用者手動觸發。</summary>
    public BenchService Bench { get; }

    /// <summary>烤機（穩定度壓力測試）：全執行緒滿載，觀察高負載下的溫度 / 頻率 / 降頻。</summary>
    public StressTestService Stress { get; } = new();
    /// <summary>快取 / 記憶體延遲測試（指標追逐法，推估 L1/L2/L3/RAM 延遲）。</summary>
    public CacheBenchService Cache { get; } = new();

    /// <summary>核心到核心延遲矩陣：原子交換往返延遲的 N×N 熱圖（CPU 分頁卡片，使用者手動觸發）。</summary>
    public CoreLatencyService CoreLatency { get; } = new();

    /// <summary>CPUID 晶片直讀：快取幾何、指令集位元、標稱頻率與拓樸（CPU 分頁卡片，建構即讀取、零特權）。</summary>
    public CpuIdService CpuId { get; } = new();

    /// <summary>記憶體延遲曲線：半倍頻步進的指標追逐，邊界由曲線推導並與 CPUID 宣稱並列。</summary>
    public LatencyCurveService LatencyCurve { get; } = new();

    /// <summary>SMBIOS 原始表全解：插槽使用狀態、記憶體條序號／型號／Rank 等 WMI 未轉譯的欄位。</summary>
    public SmbiosService Smbios { get; } = new();

    /// <summary>WHEA 硬體錯誤紀錄：事件檢視器 Microsoft-Windows-WHEA-Logger 的近 30 天彙整（零特權）。</summary>
    public WheaErrorService Whea { get; } = new();

    /// <summary>可靠性歷史：非預期關機、藍屏、應用程式當機與開機耗時的近 30 天時間軸。</summary>
    public ReliabilityHistoryService Reliability { get; } = new();

    /// <summary>S.M.A.R.T. 原始資料直讀：NVMe log page 0x02／SATA SMART READ DATA（儲存分頁卡片）。</summary>
    public StorageSmartService DiskSmart { get; } = new();

    /// <summary>幀時間擷取：ETW 訂閱 DXGI Present（實用工具子頁，零注入）。</summary>
    public FrameTimeService FrameTime { get; } = new();

    /// <summary>DPC／ISR 延遲排行：ETW 核心追蹤（實用工具子頁）。</summary>
    public DpcLatencyService DpcLatency { get; } = new();

    /// <summary>SLC 快取耗盡曲線：持續寫入與斷崖偵測（儲存分頁卡片）。</summary>
    public SlcCacheBenchService SlcCache { get; } = new();

    /// <summary>黏滯節流位元：封裝溫度牆／功耗牆的自開機以來紀錄（CPU 分頁卡片，唯讀 MSR）。</summary>
    public ThermalStickyService ThermalSticky { get; } = new();

    /// <summary>機器檢查銀行（MCA）：逐核逐銀行的已修正／不可修正錯誤計數（健康分頁卡片，唯讀 MSR）。</summary>
    public McaService Mca { get; } = new();

    /// <summary>安全緩解狀態：ARCH_CAPABILITIES 免疫位元＋SPEC_CTRL 目前啟用（CPU 分頁卡片，唯讀 MSR）。</summary>
    public CpuSecurityService CpuSecurity { get; } = new();

    /// <summary>逐核時間歸因：閒置／使用者／核心／DPC／中斷（CPU 分頁卡片，NtQuerySystemInformation，零特權）。</summary>
    public CoreTimeService CoreTime { get; } = new();

    /// <summary>電源政策實況：核心停放／ASPM／USB 選擇性暫停與逐核頻率上限（健康分頁卡片，零特權唯讀）。</summary>
    public PowerPolicyService PowerPolicy { get; } = new();


    /// <summary>記憶體真實面貌：認可尖峰 vs 實體（記憶體分頁卡片，零特權）。</summary>
    public MemoryTruthService MemoryTruth { get; } = new();

    /// <summary>Intel RDT 監測：逐核心 L3 占用與記憶體頻寬（CPU 分頁卡片，需 MSR 寫入）。</summary>
    public RdtService Rdt { get; } = new();

    /// <summary>Top-down Level 1：逐實體核心的管線四桶歸因（效能分頁卡片，需 PMU 編程）。</summary>
    public TopDownService TopDown { get; } = new();

    /// <summary>頻率真相：倍頻表、實測 BCLK、逐核有效時脈（CPU 分頁卡片，MSR 唯讀）。</summary>
    public FrequencyTruthService FreqTruth { get; } = new();

    /// <summary>計時器地基：QPC 來源、解析度、Invariant TSC（健康分頁卡片，零特權）。</summary>
    public TimerFoundationService TimerFoundation { get; } = new();

    /// <summary>平台可信度：hypervisor／VBS／HVCI 是否介入，決定所有 MSR 卡片的可信度（健康分頁卡片，零特權）。</summary>
    public PlatformTrustService PlatformTrust { get; } = new();

    /// <summary>韌體與開機信任鏈：Secure Boot 四態、Hypervisor、微碼（主機板分頁卡片）。</summary>
    public FirmwareService Firmware { get; } = new();

    /// <summary>BIOS／Intel ME 韌體與微碼：唯讀直讀（含 MKHI 直詢 ME 韌體版本）＋官方刷寫路徑導向，本身不寫韌體。</summary>
    public BiosMeService BiosMe { get; } = new();

    /// <summary>記憶體圖樣檢測（寫入／回讀比對，抓卡死位元、鄰位干擾與位址解碼錯誤）。</summary>
    public MemoryTestService MemTest { get; } = new();

    /// <summary>SuperPI 圓周率運算（Chudnovsky 級數＋二分裂解，耗時即分數）。</summary>
    public SuperPiService SuperPi { get; }

    /// <summary>磁碟讀寫效能測試（循序 / 隨機 4K，MB/s 與 IOPS）。</summary>
    public DiskBenchService DiskBench { get; } = new();

    /// <summary>系統工具箱（內建 Windows 工具啟動 + 第三方工具官方導向）。</summary>
    public ToolboxService Toolbox { get; } = new();

    /// <summary>效能天梯榜：CPU／顯示卡跑分排行（離線內嵌，資料來源 topcpu.net），自動標示本機硬體。</summary>
    public RankingService Ranking { get; } = new();

    /// <summary>一鍵裝機：依分類批次安裝常用軟體（透過 Windows 內建 winget）。</summary>
    public WingetService Winget { get; } = new();

    // ── 螢幕色域（EDID 解析，開機背景讀取一次）──────────────────────────────
    private IReadOnlyList<MonitorGamutInfo> _monitors = new List<MonitorGamutInfo>();
    public IReadOnlyList<MonitorGamutInfo> Monitors { get => _monitors; internal set { if (SetProperty(ref _monitors, value)) { OnPropertyChanged(nameof(HasMonitors)); OnPropertyChanged(nameof(HasNoMonitors)); } } }
    public bool HasMonitors => _monitors.Count > 0;
    public bool HasNoMonitors => _monitors.Count == 0;

    private MemoryTimings _timings = new() { Status = "讀取中…" };
    public MemoryTimings Timings { get => _timings; internal set => SetProperty(ref _timings, value); }

    // 由 CPU-Z 報告解析出的深度規格（讀取一次後整體指派，指派即觸發繫結更新）
    private CpuDetail _cpuDetail = new();
    public CpuDetail CpuDetail { get => _cpuDetail; internal set => SetProperty(ref _cpuDetail, value); }

    private MainboardDetail _mainboard = new();
    public MainboardDetail Mainboard { get => _mainboard; internal set => SetProperty(ref _mainboard, value); }

    private IReadOnlyList<SpdModule> _spdModules = new List<SpdModule>();
    public IReadOnlyList<SpdModule> SpdModules { get => _spdModules; internal set { if (SetProperty(ref _spdModules, value)) OnPropertyChanged(nameof(HasSpdModules)); } }
    public bool HasSpdModules => _spdModules.Count > 0;

    private IReadOnlyList<GpuDetail> _gpuDetails = new List<GpuDetail>();
    public IReadOnlyList<GpuDetail> GpuDetails { get => _gpuDetails; internal set { if (SetProperty(ref _gpuDetails, value)) OnPropertyChanged(nameof(HasGpuDetails)); } }
    public bool HasGpuDetails => _gpuDetails.Count > 0;

    /// <summary>原生象棋節點吞吐跑分（perft，無執行緒上限）＋ 原版 Fritz 對照啟動。</summary>
    public ChessBenchService Chess { get; }

    /// <summary>Windows 體驗指數（WinSAT）：讀取快取分數，可手動重新評分。</summary>
    public WinsatService Winsat { get; } = new();
    /// <summary>超頻模組（測試版）：透過 Intel XTU 引擎對硬體進行真實寫入，並即時監測電壓/溫度/頻率。</summary>
    public OverclockService Overclock { get; } = new();

    /// <summary>顯示卡超頻模組（測試版）：NVML（功耗上限／風扇）＋ NVAPI（核心/顯示記憶體頻率偏移、溫度上限）真實寫入與即時遙測。</summary>
    public GpuOcService GpuOc { get; } = new();

    /// <summary>內建終端：常駐 cmd／PowerShell 行程，重導向 I/O 執行真實指令（不模擬輸出）。</summary>
    public TerminalService Terminal { get; } = new();

    /// <summary>集中式使用者設定（更新間隔／開機自啟／預設紀年／記錄與警示閾值），JSON 持久化。</summary>
    public SettingsService Settings { get; } = new();

    /// <summary>感測器記錄：依設定間隔把即時感測值持續寫入 CSV。</summary>
    public SensorLogService SensorLog { get; } = new();

    /// <summary>溫度／負載警示：超過使用者閾值時記錄事件並發系統匣通知。</summary>
    public AlertService Alerts { get; } = new();

    /// <summary>歷史倉：秒級近況常駐記憶體、分鐘級極值落地磁碟，供歷史回放頁查詢數週。</summary>
    public HistoryStore History { get; } = new();

    /// <summary>事件時間軸：警示／降頻／磁碟壽命／藍屏彙整成單一時序並持久化。</summary>
    public EventsService Events { get; } = new();

    /// <summary>風扇曲線：每顆可控風扇一條溫度→轉速曲線，由每秒脈動負責真實寫入硬體。</summary>
    public FanCurveService FanCurves { get; } = new();

    /// <summary>場景設定檔：一鍵把風扇曲線、Windows 電源計劃與顯示卡功耗／溫度上限調成同一取向。</summary>
    public ProfileService Profiles { get; } = new();

    /// <summary>歷史回放：時間窗操作（區間、縮放、平移、跟隨）與每項指標的統計。</summary>
    public HistoryViewModel HistoryView { get; }

    /// <summary>環境自檢：偵測各功能所需執行階段／驅動／服務是否就緒，缺少者附官方取得連結。</summary>
    public EnvCheckService EnvCheck { get; } = new();

    /// <summary>AI 評價：把真實硬體規格與即時感測數據交給使用者自選的 AI 模型（本機免費 Ollama 或 OpenAI 相容 API）評價。</summary>
    public AiService Ai { get; }

    /// <summary>總覽儀表板版面：使用者自選要顯示哪些磁貼、以什麼順序排（持久化於設定）。</summary>
    public DashboardLayout Dashboard { get; }

    public MainViewModel()
    {
        // 三個跑分服務共用同一本紀錄簿：分數的唯一誠實基準是本機自己的歷次成績
        Bench = new BenchService(Benchmarks);
        Chess = new ChessBenchService(Benchmarks);
        SuperPi = new SuperPiService(Benchmarks);

        Ai = new AiService(Settings) { SnapshotProvider = BuildAiSnapshot };
        // 診斷代理的本機工具箱：全部唯讀，讀的就是畫面上這同一份即時物件。
        Ai.Tools = AiToolboxBuilder.Build(this);

        // 主動診斷：警示觸發時自動請 AI 分析一次。預設關閉，只有使用者在設定頁明示開啟才會送出請求；
        // 同一項目的觸發間隔由 AlertService 自行節流。
        Alerts.Diagnose = (label, message) =>
        {
            if (!Settings.AiProactive) return;
            _ = Ai.ProactiveAsync(label, message);
        };
        HistoryView = new HistoryViewModel(History, Events, Settings);
        FanCurves.Events = Events;

        // 總覽磁貼版面：樣板要靠磁貼身上的 Vm 才接得回既有繫結，故必須在建構式（而非初始式）建立
        Dashboard = new DashboardLayout(this, Settings);

        // 場景設定檔要真正動到硬體，把三個執行者交給它（缺少者由它自行如實略過）
        Profiles.Events = Events;
        Profiles.Fans = FanCurves;
        Profiles.Gpu = GpuOc;

        // 歷史倉的開關與保留天數跟隨設定；歷史倉關檔或補上新分鐘時通知回放頁重畫
        History.Enabled = Settings.HistoryEnabled;
        History.RetentionDays = Settings.HistoryRetentionDays;
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsService.HistoryEnabled)) History.Enabled = Settings.HistoryEnabled;
            else if (e.PropertyName == nameof(SettingsService.HistoryRetentionDays))
                History.RetentionDays = Settings.HistoryRetentionDays;
        };
    }

    // 即時走勢緩衝（供 HistoryGraph 繪製）
    public MetricHistory CpuLoadHist { get; } = new(90, "%", 100);
    public MetricHistory CpuTempHist { get; } = new(90, "°C", 100);
    public MetricHistory MemHist { get; } = new(90, "%", 100);
    public MetricHistory GpuHist { get; } = new(90, "%", 100);
    public MetricHistory GpuTempHist { get; } = new(90, "°C", 100);
    public MetricHistory GpuVramHist { get; } = new(90, "MB", null);
    public MetricHistory CpuClockHist { get; } = new(90, "MHz", null);

    /// <summary>各邏輯處理器即時使用率（仿工作管理員的格狀曲線；獨立於 LHM，直接取系統計時）。</summary>
    public CpuCoreUsageService CoreLoads { get; } = new(90);

    private string _statusText = "初始化中…";
    /// <summary>底部狀態列文字。外殼（頁面載入失敗等）與開機序列亦會寫入，故 setter 為公開。</summary>
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private double _startupSeconds;
    /// <summary>
    /// 本次啟動的實測耗時（秒）：由 <see cref="StartupSequence"/> 以計時器量得，0 表示尚未量到。
    /// 狀態列只報這個真實數字，不寫死「約 N 秒」之類的估計值。
    /// </summary>
    public double StartupSeconds
    {
        get => _startupSeconds;
        internal set { if (SetProperty(ref _startupSeconds, value)) OnPropertyChanged(nameof(StartupText)); }
    }

    /// <summary>啟動耗時的顯示文字；還沒量到時為空字串。</summary>
    public string StartupText => _startupSeconds > 0 ? $"啟動耗時 {_startupSeconds:0.0} 秒" : "";

    private string _clock = "";
    public string Clock { get => _clock; internal set => SetProperty(ref _clock, value); }
    // 紀年切換：預設西元；變更時立即重新格式化時鐘，不必等下一拍
    private EraMode _era = EraMode.Gregorian;
    /// <summary>目前紀年（供 <see cref="MetricsPump"/> 每拍格式化時鐘）。</summary>
    internal EraMode Era => _era;
    public IReadOnlyList<string> EraNames => EraCalendar.Names;
    public int EraIndex
    {
        get => (int)_era;
        set
        {
            var mode = (EraMode)value;
            if (_era == mode) return;
            _era = mode;
            OnPropertyChanged();
            Settings.DefaultEra = value;   // 保存為下次啟動的預設紀年
            UpdateClock();
        }
    }

    /// <summary>依目前紀年重新格式化時鐘文字（切換紀年時立即套用，其後由每秒脈動維持）。</summary>
    internal void UpdateClock() => Clock = EraCalendar.Format(DateTime.Now, _era);

    /// <summary>套用已存設定中的預設紀年（開機序列最前呼叫）。</summary>
    internal void ApplySavedEra()
    {
        if (Settings.DefaultEra < 0 || Settings.DefaultEra >= EraCalendar.Names.Length) return;
        _era = (EraMode)Settings.DefaultEra;
        OnPropertyChanged(nameof(EraIndex));
    }

    public string AppTitle => "曦覽 XinSpect";
    public string AppSubtitle => "硬體資訊總覽";

    // ── 音效卡 / 網路卡偵測（WMI，開機背景讀取一次）────────────────────────────
    private IReadOnlyList<string> _soundDevices = new List<string>();
    public IReadOnlyList<string> SoundDevices { get => _soundDevices; internal set { if (SetProperty(ref _soundDevices, value)) OnPropertyChanged(nameof(HasSoundDevices)); } }
    public bool HasSoundDevices => _soundDevices.Count > 0;

    private IReadOnlyList<NicInfo> _installedNics = new List<NicInfo>();
    public IReadOnlyList<NicInfo> InstalledNics { get => _installedNics; internal set { if (SetProperty(ref _installedNics, value)) OnPropertyChanged(nameof(HasInstalledNics)); } }
    public bool HasInstalledNics => _installedNics.Count > 0;

    // ── 實體磁碟深度靜態資訊（WMI Win32_DiskDrive，開機背景讀取一次）──────────
    private IReadOnlyList<PhysicalDiskInfo> _physicalDisks = new List<PhysicalDiskInfo>();
    public IReadOnlyList<PhysicalDiskInfo> PhysicalDisks { get => _physicalDisks; internal set { if (SetProperty(ref _physicalDisks, value)) OnPropertyChanged(nameof(HasPhysicalDisks)); } }
    public bool HasPhysicalDisks => _physicalDisks.Count > 0;
    // ── CUDA 版本（無 NVIDIA CUDA 驅動時顯示 ****）───────────────────────────
    private string _cudaVersion = "****";
    public string CudaVersion { get => _cudaVersion; internal set => SetProperty(ref _cudaVersion, value); }

    // ── 磁碟活動走勢的「全部顯示 / 單獨顯示（可切換硬碟）」切換 ──────────────
    // 用獨立的 CollectionView 過濾（不動到裝置清單所用的預設檢視）。
    private ICollectionView? _diskActivityView;
    public ICollectionView? DiskActivityView { get => _diskActivityView; internal set => SetProperty(ref _diskActivityView, value); }

    private IReadOnlyList<string> _diskViewChoices = new List<string>();
    public IReadOnlyList<string> DiskViewChoices { get => _diskViewChoices; internal set => SetProperty(ref _diskViewChoices, value); }

    private int _diskViewIndex;
    public int DiskViewIndex
    {
        get => _diskViewIndex;
        set { if (SetProperty(ref _diskViewIndex, value)) DiskActivityView?.Refresh(); }
    }

    // ===== 生命週期 =====

    /// <summary>開機：跑分階段偵測序列，並啟動每秒脈動。</summary>
    public void Initialize()
    {
        _pump = new MetricsPump(this);

        // 事件時間軸：記一筆本次啟動，並開始鏡射警示服務的新增事件
        try { Events.NoteAppStart(); Events.AttachAlerts(Alerts); } catch { /* 事件為附加功能 */ }

        // 射後不理：序列內部逐步以 try/catch 降級，不會逸出例外
        _ = StartupSequence.RunAsync(this, _pump);
    }

    /// <summary>所有功能一鍵初始化：重新執行各模組的偵測與載入（不重建每秒計時器），供設定頁「一鍵初始化」重整。</summary>
    public Task ReinitializeAllAsync() => StartupSequence.ReinitializeAsync(this);

    /// <summary>建立磁碟活動走勢的可切換檢視（獨立 CollectionView，過濾不影響裝置清單）。</summary>
    internal void SetupDiskActivityView()
    {
        if (Live is null) return;

        var choices = new List<string> { "全部顯示" };
        choices.AddRange(Live.Drives.Select(d => d.Name));
        DiskViewChoices = choices;

        var view = new CollectionViewSource { Source = Live.Drives }.View;
        view.Filter = o =>
        {
            if (_diskViewIndex <= 0) return true;                          // 全部顯示
            return o is StorageRow r
                   && _diskViewIndex < _diskViewChoices.Count
                   && r.Name == _diskViewChoices[_diskViewIndex];          // 單獨顯示所選硬碟
        };
        DiskActivityView = view;
    }

    /// <summary>為 AI 評價彙整一份精簡的硬體＋即時數據文字快照（全為真實讀值）。</summary>
    public string BuildAiSnapshot() => AiSnapshotBuilder.Build(this);

    /// <summary>彙整目前所有資訊並匯出報告（HTML／Markdown／純文字，依所選副檔名）。</summary>
    public void ExportReport()
    {
        try
        {
            var path = ReportService.Export(this);
            if (path is not null) StatusText = "報告已匯出：" + path;
        }
        catch (Exception ex)
        {
            StatusText = "匯出失敗：" + ex.Message;
        }
    }

    public void Stop()
    {
        _pump?.Stop();
        try { Events.NoteAppStop(); } catch { /* 事件為附加功能 */ }
        try { History.Flush(); } catch { /* 未關閉的分鐘寫不進去只損失最後一分鐘 */ }
        try { SensorLog.StopLogging(); } catch { /* 停止記錄並關檔失敗不影響關閉 */ }
        try { FanCurves.DisableAll(); } catch { /* 交還風扇自動控制失敗，SensorService.Dispose 會再試一次 */ }
        try { Overclock.Dispose(); } catch { /* 釋放超頻引擎（重設監測 / 卸載 SDK）失敗不影響關閉 */ }
        try { GpuOc.Dispose(); } catch { /* 釋放顯示卡超頻引擎（卸載 NVML/NVAPI）失敗不影響關閉 */ }
        try { Terminal.Dispose(); } catch { /* 終止常駐 Shell 行程失敗不影響關閉 */ }
    }
}
