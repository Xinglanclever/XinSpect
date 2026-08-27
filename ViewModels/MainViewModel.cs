using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>主檢視模型：協調靜態資訊讀取、每秒感測器更新、背景時序讀取、網路/行程監控與報告匯出。</summary>
public sealed class MainViewModel : ObservableObject
{
    private DispatcherTimer? _timer;
    private bool _rankGpuDone;   // 天梯榜：本機顯示卡是否已於首個可用感測拍完成高亮標示

    private SensorService? _live;
    public SensorService? Live { get => _live; private set => SetProperty(ref _live, value); }

    private NetworkService? _net;
    public NetworkService? Net { get => _net; private set => SetProperty(ref _net, value); }

    private SystemSummary _system = new();
    public SystemSummary System { get => _system; private set => SetProperty(ref _system, value); }

    private CpuStatic _cpu = new();
    public CpuStatic Cpu { get => _cpu; private set => SetProperty(ref _cpu, value); }

    private CpuTopology _cpuTopology = new();
    public CpuTopology CpuTopology { get => _cpuTopology; private set => SetProperty(ref _cpuTopology, value); }

    public ObservableCollection<MemoryModuleInfo> Modules { get; } = new();

    /// <summary>磁碟區容量（甜甜圈圖 / 健康總評用）。開機即建立，之後每數秒更新已用量。</summary>
    public VolumeService Volumes { get; } = new();

    /// <summary>健康總評：每秒由即時感測值 + 磁碟容量彙整為狀態燈與綜合分數。</summary>
    public HealthReport Health { get; } = new();

    /// <summary>綜合效能測試（單/多執行緒運算 + 記憶體頻寬）。使用者手動觸發。</summary>
    public BenchService Bench { get; } = new();

    /// <summary>烤機（穩定度壓力測試）：全執行緒滿載，觀察高負載下的溫度 / 頻率 / 降頻。</summary>
    public StressTestService Stress { get; } = new();

    /// <summary>快取 / 記憶體延遲測試（指標追逐法，推估 L1/L2/L3/RAM 延遲）。</summary>
    public CacheBenchService Cache { get; } = new();

    /// <summary>SuperPI 圓周率運算（Machin 公式定點運算，耗時即分數）。</summary>
    public SuperPiService SuperPi { get; } = new();

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
    public IReadOnlyList<MonitorGamutInfo> Monitors { get => _monitors; private set { if (SetProperty(ref _monitors, value)) { OnPropertyChanged(nameof(HasMonitors)); OnPropertyChanged(nameof(HasNoMonitors)); } } }
    public bool HasMonitors => _monitors.Count > 0;
    public bool HasNoMonitors => _monitors.Count == 0;

    private MemoryTimings _timings = new() { Status = "讀取中…" };
    public MemoryTimings Timings { get => _timings; private set => SetProperty(ref _timings, value); }

    // 由 CPU-Z 報告解析出的深度規格（讀取一次後整體指派，指派即觸發繫結更新）
    private CpuDetail _cpuDetail = new();
    public CpuDetail CpuDetail { get => _cpuDetail; private set => SetProperty(ref _cpuDetail, value); }

    private MainboardDetail _mainboard = new();
    public MainboardDetail Mainboard { get => _mainboard; private set => SetProperty(ref _mainboard, value); }

    private IReadOnlyList<SpdModule> _spdModules = new List<SpdModule>();
    public IReadOnlyList<SpdModule> SpdModules { get => _spdModules; private set { if (SetProperty(ref _spdModules, value)) OnPropertyChanged(nameof(HasSpdModules)); } }
    public bool HasSpdModules => _spdModules.Count > 0;

    private IReadOnlyList<GpuDetail> _gpuDetails = new List<GpuDetail>();
    public IReadOnlyList<GpuDetail> GpuDetails { get => _gpuDetails; private set { if (SetProperty(ref _gpuDetails, value)) OnPropertyChanged(nameof(HasGpuDetails)); } }
    public bool HasGpuDetails => _gpuDetails.Count > 0;

    /// <summary>原生象棋節點吞吐跑分（perft，無執行緒上限）＋ 原版 Fritz 對照啟動。</summary>
    public ChessBenchService Chess { get; } = new();

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

    /// <summary>環境自檢：偵測各功能所需執行階段／驅動／服務是否就緒，缺少者附官方取得連結。</summary>
    public EnvCheckService EnvCheck { get; } = new();

    /// <summary>AI 評價：把真實硬體規格與即時感測數據交給使用者自選的 AI 模型（本機免費 Ollama 或 OpenAI 相容 API）評價。</summary>
    public AiService Ai { get; }

    public MainViewModel()
    {
        Ai = new AiService(Settings) { SnapshotProvider = BuildAiSnapshot };
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
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private string _clock = "";
    public string Clock { get => _clock; private set => SetProperty(ref _clock, value); }

    // 紀年切換：預設西元；變更時立即重新格式化時鐘，不必等下一拍
    private EraMode _era = EraMode.Gregorian;
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
            Clock = EraCalendar.Format(DateTime.Now, _era);
        }
    }

    public string AppTitle => "曦覽 XinSpect";
    public string AppSubtitle => "硬體資訊總覽";

    // ── 音效卡 / 網路卡偵測（WMI，開機背景讀取一次）────────────────────────────
    private IReadOnlyList<string> _soundDevices = new List<string>();
    public IReadOnlyList<string> SoundDevices { get => _soundDevices; private set { if (SetProperty(ref _soundDevices, value)) OnPropertyChanged(nameof(HasSoundDevices)); } }
    public bool HasSoundDevices => _soundDevices.Count > 0;

    private IReadOnlyList<NicInfo> _installedNics = new List<NicInfo>();
    public IReadOnlyList<NicInfo> InstalledNics { get => _installedNics; private set { if (SetProperty(ref _installedNics, value)) OnPropertyChanged(nameof(HasInstalledNics)); } }
    public bool HasInstalledNics => _installedNics.Count > 0;

    // ── 實體磁碟深度靜態資訊（WMI Win32_DiskDrive，開機背景讀取一次）──────────
    private IReadOnlyList<PhysicalDiskInfo> _physicalDisks = new List<PhysicalDiskInfo>();
    public IReadOnlyList<PhysicalDiskInfo> PhysicalDisks { get => _physicalDisks; private set { if (SetProperty(ref _physicalDisks, value)) OnPropertyChanged(nameof(HasPhysicalDisks)); } }
    public bool HasPhysicalDisks => _physicalDisks.Count > 0;

    // ── CUDA 版本（無 NVIDIA CUDA 驅動時顯示 ****）───────────────────────────
    private string _cudaVersion = "****";
    public string CudaVersion { get => _cudaVersion; private set => SetProperty(ref _cudaVersion, value); }

    // ── 磁碟活動走勢的「全部顯示 / 單獨顯示（可切換硬碟）」切換 ──────────────
    // 用獨立的 CollectionView 過濾（不動到裝置清單所用的預設檢視）。
    private ICollectionView? _diskActivityView;
    public ICollectionView? DiskActivityView { get => _diskActivityView; private set => SetProperty(ref _diskActivityView, value); }

    private IReadOnlyList<string> _diskViewChoices = new List<string>();
    public IReadOnlyList<string> DiskViewChoices { get => _diskViewChoices; private set => SetProperty(ref _diskViewChoices, value); }

    private int _diskViewIndex;
    public int DiskViewIndex
    {
        get => _diskViewIndex;
        set { if (SetProperty(ref _diskViewIndex, value)) DiskActivityView?.Refresh(); }
    }

    public async void Initialize()
    {
        // 0) 套用已存設定：啟動時預設紀年
        if (Settings.DefaultEra >= 0 && Settings.DefaultEra < EraCalendar.Names.Length)
        {
            _era = (EraMode)Settings.DefaultEra;
            OnPropertyChanged(nameof(EraIndex));
        }

        // 0.1) 工具箱插槽：回填已保存的本機執行檔路徑（工具箱先於設定建立，故於此接上設定服務）
        try { Toolbox.AttachSettings(Settings); } catch { /* 插槽為附加功能 */ }

        // 1) 靜態資訊（WMI）於背景讀取
        // 整段以 try/catch 包覆：Initialize 為 async void，任何 WMI 呼叫拋例外都會直接終結整個應用程式；
        // 失敗時記錄狀態並向下降級（感測器/網路/行程等後續步驟仍各自嘗試初始化），不得中斷啟動。
        StatusText = "正在讀取系統與硬體資訊…";
        try
        {
            var (summary, cpu, modules, sound, nics) = await Task.Run(() =>
                (SystemInfoService.GetSystemSummary(),
                 SystemInfoService.GetCpu(),
                 SystemInfoService.GetMemoryModules(),
                 SystemInfoService.GetSoundDevices(),
                 SystemInfoService.GetNetworkAdapters()));

            System = summary;
            Cpu = cpu;
            CpuTopology = await Task.Run(CpuTopologyService.Build);
            Modules.Clear();
            foreach (var m in modules) Modules.Add(m);
            SoundDevices = sound;
            InstalledNics = nics;

            // 主機板分頁先以 WMI 廠商/型號填入，稍後 CPU-Z 報告再補全晶片組/BIOS 等深度欄位
            Mainboard = new MainboardDetail { Vendor = summary.BoardVendor, Model = summary.BoardModel };

            // 天梯榜：先以處理器名稱標示本機所在名次（顯示卡名稱待感測器就緒後於首拍補標）
            try { Ranking.Highlight(Cpu.Name, null); } catch { /* 天梯高亮為附加功能 */ }
        }
        catch (Exception ex)
        {
            StatusText = "系統靜態資訊讀取失敗（WMI 不可用）：" + ex.Message;
        }

        // 2) 感測器（LHM，需載入 Ring0 驅動，於背景初始化）
        StatusText = "正在啟動感測器引擎…";
        try
        {
            Live = await Task.Run(() => new SensorService());
        }
        catch (Exception ex)
        {
            StatusText = "感測器初始化失敗（溫度/頻率不可用）：" + ex.Message;
        }

        // 3) 網路監控
        try { Net = await Task.Run(() => new NetworkService()); } catch { /* 網路資訊為附加功能 */ }

        // 3.5) 磁碟容量 / 類型 / HDD 健康（WMI 較慢：背景查詢，回 UI 執行緒就地套用到磁碟列）
        if (Live is not null)
        {
            try
            {
                var disks = await Task.Run(DiskInfoService.Query);
                Live.ApplyDiskInfo(disks);
                PhysicalDisks = disks;
            }
            catch { /* 磁碟靜態資訊為附加，失敗則容量/類型維持預設 */ }

            // 3.6) 磁碟活動走勢：建立可切換「全部顯示 / 單獨顯示（可切換硬碟）」的檢視
            try { SetupDiskActivityView(); } catch { /* 磁碟活動走勢為附加功能，失敗不影響其餘頁面 */ }
        }

        // 3.7) CUDA 版本偵測（透過 nvcuda.dll；無 NVIDIA CUDA 驅動時維持 ****）
        try { CudaVersion = await Task.Run(CudaService.DetectVersion) ?? "****"; }
        catch { CudaVersion = "****"; }

        // 3.8) 磁碟效能測試：列出可測試的固定磁碟機代號
        try { DiskBench.PopulateDrives(); } catch { /* 磁碟清單為附加功能 */ }

        // 3.9) 螢幕色域：自 EDID 解析各顯示器的色域覆蓋率（背景讀取登錄檔）
        try { Monitors = await Task.Run(EdidService.Detect); } catch { /* 色域為附加功能 */ }

        // 3.95) 超頻引擎（載入 Intel XTU SDK；未就緒時降級為唯讀監測，不影響主程式）
        try { _ = Overclock.InitializeAsync(); } catch { /* 超頻為測試版附加功能 */ }

        // 3.96) 顯示卡超頻引擎（NVML/NVAPI；無 NVIDIA 卡或介面缺失時降級停用，不影響主程式）
        try { _ = GpuOc.InitializeAsync(); } catch { /* 顯示卡超頻為測試版附加功能 */ }

        // 4) 定期更新（間隔由設定決定；變更設定即時套用）
        int tick = 0;
        bool ticking = false;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Settings.UpdateIntervalSec) };
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsService.UpdateIntervalSec) && _timer is not null)
                _timer.Interval = TimeSpan.FromSeconds(Settings.UpdateIntervalSec);
        };
        _timer.Tick += async (_, _) =>
        {
            if (ticking) return;   // 上一拍尚未完成（LHM 走訪偶爾較慢）就跳過本拍，避免堆積造成卡頓
            ticking = true;
            try
            {
                var live = Live;
                // 最重的一段（走訪 LHM 硬體樹）移至背景執行緒；格式化與通知回到 UI 執行緒
                if (live is not null)
                {
                    try { await Task.Run(live.Poll); } catch { /* 單次讀取失敗不影響後續 */ }
                    try { live.Publish(); } catch { }
                }

                try { CoreLoads.Refresh(); } catch { /* 各邏輯處理器走勢為附加，失敗略過 */ }
                if (live is not null)
                {
                    CpuLoadHist.Push(live.CpuLoad);
                    CpuTempHist.Push(live.CpuTemp ?? 0);
                    MemHist.Push(live.MemLoad);
                    var g = live.PrimaryGpu;
                    GpuHist.Push(g?.LoadPercent ?? 0);
                    GpuTempHist.Push(g?.TempC ?? 0);
                    GpuVramHist.Push(g?.VramUsedMB ?? 0);
                    CpuClockHist.Push(live.CpuClock);
                    // 烤機進行時餵入即時值，累計最高溫 / 頻率極值並偵測降頻
                    if (Stress.IsRunning) Stress.Sample(live.CpuTemp, live.CpuClock, live.CpuLoad);
                    // 顯示卡名稱就緒後，於天梯榜補標本機顯示卡（僅需一次）
                    if (!_rankGpuDone && live.PrimaryGpu?.Name is { Length: > 0 } gpuName)
                    {
                        _rankGpuDone = true;
                        try { Ranking.Highlight(null, gpuName); } catch { /* 天梯高亮為附加功能 */ }
                    }
                    try { SensorLog.Sample(live, Settings); } catch { /* CSV 記錄為附加，寫入失敗不影響監控 */ }
                    try { Alerts.Check(live, Settings); } catch { /* 警示為附加 */ }
                }
                try { Net?.Refresh(); } catch { }
                if (tick % 5 == 0) { try { Volumes.Refresh(); } catch { } } // 磁碟容量變動慢，每 5 秒一次
                try { Health.Update(live, Volumes); } catch { }             // 彙整健康總評（讀取現值，成本低）
                try { await Overclock.TickAsync(live); } catch { }           // 超頻模組即時遙測（電壓/溫度/頻率/看門狗）；IPC 讀值在背景執行緒
                try { GpuOc.Tick(); } catch { }                              // 顯示卡超頻模組即時遙測（時脈/溫度/功耗/風扇）
                tick++;
                Clock = EraCalendar.Format(DateTime.Now, _era);
            }
            catch { /* 任何未預期例外都不得使 async void 崩潰 */ }
            finally { ticking = false; }
        };
        _timer.Start();
        Clock = EraCalendar.Format(DateTime.Now, _era);
        StatusText = Live is null ? StatusText : "就緒 ・ 每秒更新中";

        // 5) 深度規格（背景呼叫 CPU-Z 產生報告，約需 10 餘秒）＋ WinSAT 快取分數
        _ = LoadTimingsAsync();
        _ = Winsat.LoadCachedAsync();

        // 6) 首次啟動即自動環境自檢：待感測器首拍、超頻/顯卡引擎與 winget 偵測稍稍就緒後於背景執行一次。
        _ = RunStartupEnvCheckAsync();
    }

    // 首次啟動的環境自檢：略候片刻讓各引擎與感測器就緒，再於背景跑一次；失敗不影響主程式。
    private async Task RunStartupEnvCheckAsync()
    {
        try
        {
            await Task.Delay(2500);
            if (!EnvCheck.HasRun && !EnvCheck.IsRunning) await EnvCheck.RunAsync(this);
        }
        catch { /* 環境自檢為附加功能，失敗不影響其餘功能 */ }
    }

    /// <summary>所有功能一鍵初始化：重新執行各模組的偵測與載入（不重建每秒計時器），供設定頁「一鍵初始化」重整。</summary>
    public async Task ReinitializeAllAsync()
    {
        StatusText = "正在重新初始化所有功能…";

        // 靜態硬體資訊（WMI）重新讀取
        try
        {
            var (summary, cpu, modules, sound, nics) = await Task.Run(() =>
                (SystemInfoService.GetSystemSummary(),
                 SystemInfoService.GetCpu(),
                 SystemInfoService.GetMemoryModules(),
                 SystemInfoService.GetSoundDevices(),
                 SystemInfoService.GetNetworkAdapters()));
            System = summary;
            Cpu = cpu;
            CpuTopology = await Task.Run(CpuTopologyService.Build);
            Modules.Clear();
            foreach (var m in modules) Modules.Add(m);
            SoundDevices = sound;
            InstalledNics = nics;
            Mainboard = new MainboardDetail { Vendor = summary.BoardVendor, Model = summary.BoardModel };
            try { Ranking.Highlight(Cpu.Name, Live?.PrimaryGpu?.Name); } catch { /* 天梯高亮為附加功能 */ }
        }
        catch (Exception ex) { StatusText = "重新初始化：靜態資訊讀取失敗 — " + ex.Message; }

        // 感測器 / 網路引擎：先前失敗者重試建立
        if (Live is null) { try { Live = await Task.Run(() => new SensorService()); } catch { /* 感測器不可用 */ } }
        if (Net is null) { try { Net = await Task.Run(() => new NetworkService()); } catch { /* 網路資訊為附加 */ } }

        // 磁碟靜態資訊 / 磁碟效能清單 / 螢幕色域 / CUDA 版本
        if (Live is not null)
        {
            try { var disks = await Task.Run(DiskInfoService.Query); Live.ApplyDiskInfo(disks); PhysicalDisks = disks; }
            catch { /* 磁碟靜態資訊為附加 */ }
        }
        try { DiskBench.PopulateDrives(); } catch { /* 磁碟清單為附加 */ }
        try { Monitors = await Task.Run(EdidService.Detect); } catch { /* 色域為附加 */ }
        try { CudaVersion = await Task.Run(CudaService.DetectVersion) ?? "****"; } catch { CudaVersion = "****"; }

        // 超頻 / 顯示卡超頻引擎重新初始化（未就緒者降級為唯讀監測）
        try { _ = Overclock.InitializeAsync(); } catch { /* 超頻為測試版附加 */ }
        try { _ = GpuOc.InitializeAsync(); } catch { /* 顯示卡超頻為測試版附加 */ }

        // 一鍵裝機：重新偵測 winget 是否可用
        try { await Winget.DetectAsync(); } catch { /* winget 偵測為附加 */ }

        // 深度規格（CPU-Z 報告）＋ WinSAT 快取分數
        _ = LoadTimingsAsync();
        _ = Winsat.LoadCachedAsync();

        StatusText = Live is null ? "重新初始化完成（部分模組不可用）" : "重新初始化完成 ・ 每秒更新中";
    }

    /// <summary>建立磁碟活動走勢的可切換檢視（獨立 CollectionView，過濾不影響裝置清單）。</summary>
    private void SetupDiskActivityView()
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

    private async Task LoadTimingsAsync()
    {
        // 整段包覆：本方法以 _ = LoadTimingsAsync() 射後不理呼叫，CPU-Z 子行程失敗若逸出將成為
        // 未觀察的 Task 例外；失敗時深度規格維持 WMI 既有值即可，不影響主程式。
        try
        {
            var report = await CpuzReportService.ReadAsync();

            // 時序（含次要時序），沿用既有繫結物件
            report.Timings.RaiseAll();
            Timings = report.Timings;

            // 主機板廠商 CPU-Z 常只給代碼，以 WMI 值補入後再指派（讓 Brand 解析正確）
            if (report.Board.Vendor == "—" && System.BoardVendor != "—")
                report.Board.Vendor = System.BoardVendor;
            if (report.Board.Model == "—" && System.BoardModel != "—")
                report.Board.Model = System.BoardModel;

            CpuDetail = report.Cpu;
            Mainboard = report.Board;
            SpdModules = report.Spd;
            GpuDetails = report.Gpus;

            if (Live is not null)
                StatusText = report.Ran ? "就緒 ・ 每秒更新中 ・ 深度規格已讀取" : "就緒 ・ 每秒更新中";
        }
        catch { /* 深度規格為附加，讀取失敗維持 WMI 值 */ }
    }

    /// <summary>為 AI 評價彙整一份精簡的硬體＋即時數據文字快照（全為真實讀值）。</summary>
    public string BuildAiSnapshot()
    {
        var sb = new System.Text.StringBuilder();
        var s = System;
        sb.AppendLine($"作業系統：{s.OsName}（{s.OsArch}），版本 {s.OsVersion}");
        sb.AppendLine($"機型：{s.SystemManufacturer} {s.SystemModel}");
        sb.AppendLine($"主機板：{s.BoardVendor} {s.BoardModel}；BIOS：{s.BiosVendor} {s.BiosVersion}（{s.BiosDate}）");

        var live = Live;
        sb.Append($"處理器：{Cpu.Name}，{Cpu.Cores} 核 {Cpu.Threads} 執行緒");
        if (Cpu.MaxClockMHz > 0) sb.Append($"，額定 {Cpu.MaxClockMHz:0} MHz");
        if (CpuDetail.Loaded)
        {
            if (!string.IsNullOrWhiteSpace(CpuDetail.Technology) && CpuDetail.Technology != "—") sb.Append($"，製程 {CpuDetail.Technology}");
            if (!string.IsNullOrWhiteSpace(CpuDetail.TdpLimit) && CpuDetail.TdpLimit != "—") sb.Append($"，TDP {CpuDetail.TdpLimit}");
        }
        sb.AppendLine();
        if (live is not null)
        {
            sb.AppendLine($"處理器即時：時脈 {live.CpuClockText}、負載 {live.CpuLoadText}、溫度 {live.CpuTempText}、功耗 {live.CpuPowerText}");
            sb.AppendLine($"記憶體：{live.MemUsageText}，負載 {live.MemLoadText}（實體模組 {Modules.Count} 條）");
            var g = live.PrimaryGpu;
            if (g is not null)
                sb.AppendLine($"顯示卡：{g.Name}，負載 {live.GpuLoadText}、溫度 {live.GpuTempText}");
        }
        else
        {
            sb.AppendLine($"記憶體：實體模組 {Modules.Count} 條（感測器尚未就緒，暫無即時用量）");
        }

        if (Volumes.Volumes.Count > 0)
        {
            sb.Append("儲存磁碟區：");
            sb.AppendLine(string.Join("；", Volumes.Volumes.Select(v => $"{v.CaptionText} {v.SizeText}")));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>彙整目前所有資訊並匯出報告（HTML / 純文字）。</summary>
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
        _timer?.Stop();
        try { SensorLog.StopLogging(); } catch { /* 停止記錄並關檔失敗不影響關閉 */ }
        try { Overclock.Dispose(); } catch { /* 釋放超頻引擎（重設監測 / 卸載 SDK）失敗不影響關閉 */ }
        try { GpuOc.Dispose(); } catch { /* 釋放顯示卡超頻引擎（卸載 NVML/NVAPI）失敗不影響關閉 */ }
        try { Terminal.Dispose(); } catch { /* 終止常駐 Shell 行程失敗不影響關閉 */ }
    }
}
