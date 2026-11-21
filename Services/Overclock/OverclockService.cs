using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace XinSpect;

// ───────────────────────────────────────────────────────────────────────────
// 超頻模組的 VM 端協調者：把 IOcEngine（真實硬體讀寫）、SensorService（即時遙測）、
// StressTestService（燒機）與 OcSettings（持久化）整合成 OverclockView 直接繫結的表面。
//
// 誠實原則貫穿全檔：
//   • 電壓 / 頻率 / 溫度一律取自實測（LHM 優先，XTU 監控補位），取不到就顯示「—」。
//   • 電流以 I≈P/V 估算，明確標示為「估算」。
//   • LLC 因 XTU SDK 無法寫入，僅作 BIOS 設定教學，介面明白標示「僅供 BIOS 參考」。
//   • 看門狗第一層（軟體自動回復）＋第二層（開機還原）＋第三層（硬體看門狗狀態，唯讀）。
// ───────────────────────────────────────────────────────────────────────────

public sealed class OverclockService : ObservableObject, IDisposable
{
    private IOcEngine _engine = new NullOcEngine(OcEngineStatus.NotInitialized, "尚未初始化");
    private readonly OcSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly Func<SiliconProbeService.Options, IProgress<SiliconProbeProgress>?, CancellationToken, SiliconAssessment>
        _siliconProbe = SiliconProbeService.Run;
    private readonly StressTestService _stress = new();

    // Admission uses only this short state lock. Never wait for engine I/O while holding it.
    private readonly object _operationState = new();
    private readonly SemaphoreSlim _engineSerial = new(1, 1);
    private long _operationGeneration;
    private long? _activeApplyGeneration;
    private TaskCompletionSource<OcApplyResult>? _recoveryCompletion;
    private bool _disposed;
    private bool _initializationInFlight;

    private OcProfile? _rollback;         // 最後確認穩定的設定（看門狗逾時 / 燒機過熱時回復的目標）
    private bool _stressGuardTripped;
    private bool _stressTempMissingWarned;   // 燒機時溫度不可讀的提示僅顯示一次
    private bool _hasLastStable;
    private SensorService? _live;            // 最近一次遙測來源（體質特徵化的感測器補位用）

    public OverclockService() : this(OcSettings.Load()) { }

    internal OverclockService(OcSettings settings) : this(settings, TimeProvider.System) { }

    private OverclockService(OcSettings settings, TimeProvider timeProvider)
    {
        _settings = settings;
        _timeProvider = timeProvider;
        OscilloVolt = new MetricHistory(140, "V", null, "0.000");
        OscilloCurrent = new MetricHistory(140, "A", null, "0");
        _watchdogEnabled = _settings.WatchdogEnabled;
        _bootRestore = _settings.BootRestore;
    }

    // Injected engines exercise the production coordinator without SDK, VRM detection or user files.
    internal OverclockService(OcSettings settings, IOcEngine engine, TimeProvider? timeProvider = null,
        Func<SiliconProbeService.Options, IProgress<SiliconProbeProgress>?, CancellationToken, SiliconAssessment>? siliconProbe = null)
        : this(settings, timeProvider ?? TimeProvider.System)
    {
        _engine = engine;
        if (siliconProbe is not null) _siliconProbe = siliconProbe;
        Categorize();
        _rollback = CaptureCurrent("初始基準");
    }

    // ── 燒機（本模組專用，獨立於效能分頁的烤機，避免互相干擾）────────────────
    public StressTestService Stress => _stress;

    // ── 引擎狀態 ───────────────────────────────────────────────────────────
    private bool _initializing;
    public bool Initializing { get => _initializing; private set => SetProperty(ref _initializing, value); }

    public bool EngineReady => _engine.Status == OcEngineStatus.Ready;
    public bool CanWrite => EngineReady && _engine.Knobs.Any(k => k.Writable);
    public bool CanUseHardwareWrites => HardwareOperationGate.CanWriteHardware;
    public string EngineName => _engine.Name;
    public string EngineStatusText => _engine.StatusMessage;
    public Severity EngineStatusSeverity => _engine.Status switch
    {
        OcEngineStatus.Ready => Severity.Good,
        OcEngineStatus.Unsupported => Severity.Warning,
        OcEngineStatus.Missing => Severity.Neutral,
        OcEngineStatus.Failed => Severity.Critical,
        _ => Severity.Neutral,
    };
    public string ProcessorFamilyText => _engine.ProcessorFamily > 0 ? $"處理器家族 {_engine.ProcessorFamily}" : "—";

    // ── 可調項分區 ─────────────────────────────────────────────────────────
    public ObservableCollection<OcKnob> CoreKnobs { get; } = new();
    public ObservableCollection<OcKnob> CacheKnobs { get; } = new();   // 快取（Uncore／Ring）：電壓／偏移／倍頻，於 UI 緊接目標時脈規劃之下
    public ObservableCollection<OcKnob> PowerKnobs { get; } = new();
    public ObservableCollection<OcKnob> MemoryKnobs { get; } = new();
    public ObservableCollection<OcKnob> BusKnobs { get; } = new();
    public ObservableCollection<OcKnob> AdvancedKnobs { get; } = new();

    public bool HasCoreKnobs => CoreKnobs.Count > 0;
    public bool HasCacheKnobs => CacheKnobs.Count > 0;
    public bool HasPowerKnobs => PowerKnobs.Count > 0;
    public bool HasMemoryKnobs => MemoryKnobs.Count > 0;
    public bool HasBusKnobs => BusKnobs.Count > 0;
    public bool HasAdvancedKnobs => AdvancedKnobs.Count > 0;

    // ── 每核心合併調節（單核心倍頻上限＋電壓＋偏移，依核心序號配對）───────────
    public ObservableCollection<CoreTuneRow> CoreTuneRows { get; } = new();
    public bool HasCoreTuneRows => CoreTuneRows.Count > 0;

    // ── 頻率規劃（倍頻 × 外頻）─────────────────────────────────────────────
    public OcKnob? CoreRatioKnob { get; private set; }
    public OcKnob? BclkKnob { get; private set; }
    public bool HasFrequencyPlanner => CoreRatioKnob is not null;
    public bool HasBclk => BclkKnob is not null;   // 無外頻旋鈕（如 X299 常見）時隱藏該列，避免空白鬼影列

    public string TargetFrequencyText
    {
        get
        {
            if (CoreRatioKnob is null) return "—";
            double ratio = CoreRatioKnob.Target;
            double bclk = BclkKnob?.Target ?? 100.0;
            double mhz = ratio * bclk;
            return $"{mhz / 1000.0:0.00} GHz　（{ratio:0.#} × {bclk:0.0} MHz）";
        }
    }

    // ── 電壓徽章 / 電壓錶 ──────────────────────────────────────────────────
    public OcKnob? VcoreKnob { get; private set; }
    public bool HasVcore => VcoreKnob is not null;

    // 聚合式全核心電壓：此平台若無「全域核心電壓」旋鈕（如 Skylake-X／Cascade Lake-X 僅逐核心覆寫），
    // 則以這些「每核心電壓覆寫」為扇出目標，並用合成的聚合 VcoreKnob 當作 Vcore 卡滑桿模型
    // （見 Categorize 建立、ApplyVcore 一次寫入全部核心、Tick 以實測電壓回填現值）。
    private readonly List<OcKnob> _vcoreFanout = new();
    private bool _vcoreSeeded;
    private string _vcoreScope = "";
    public string VcoreScopeText { get => _vcoreScope; private set => SetProperty(ref _vcoreScope, value); }

    private double _voltmeter;
    public double VoltmeterValue { get => _voltmeter; private set => SetProperty(ref _voltmeter, value); }
    public double VoltmeterMax => 2.0;

    private string _vBadgeText = "—";
    public string VoltageBadgeText { get => _vBadgeText; private set => SetProperty(ref _vBadgeText, value); }

    private Brush _vBadgeBrush = SeverityToBrushConverter.Neutral;
    public Brush VoltageBadgeBrush { get => _vBadgeBrush; private set => SetProperty(ref _vBadgeBrush, value); }

    private bool _vBadgeFlash;
    public bool VoltageBadgeFlashing { get => _vBadgeFlash; private set => SetProperty(ref _vBadgeFlash, value); }

    private string _vValueText = "—";
    public string VoltageValueText { get => _vValueText; private set => SetProperty(ref _vValueText, value); }

    // ── 即時遙測 ───────────────────────────────────────────────────────────
    private string _coreTempText = "—";
    public string CoreTempText { get => _coreTempText; private set => SetProperty(ref _coreTempText, value); }
    private Severity _coreTempSev = Severity.Neutral;
    public Severity CoreTempSeverity { get => _coreTempSev; private set => SetProperty(ref _coreTempSev, value); }

    private string _effClock = "—";
    public string EffectiveClockText { get => _effClock; private set => SetProperty(ref _effClock, value); }

    private string _pl1 = "—";
    public string Pl1Text { get => _pl1; private set => SetProperty(ref _pl1, value); }
    private string _pl2 = "—";
    public string Pl2Text { get => _pl2; private set => SetProperty(ref _pl2, value); }

    private string _vrmText = "—";
    public string VrmTempText { get => _vrmText; private set => SetProperty(ref _vrmText, value); }
    private Severity _vrmSev = Severity.Neutral;
    public Severity VrmTempSeverity { get => _vrmSev; private set => SetProperty(ref _vrmSev, value); }

    private string _currentText = "—";
    public string CurrentText { get => _currentText; private set => SetProperty(ref _currentText, value); }

    // ── 示波器（電壓 + 電流走勢）────────────────────────────────────────────
    public MetricHistory OscilloVolt { get; }
    public MetricHistory OscilloCurrent { get; }

    // ── 體質（矽晶品質）特徵化 ─────────────────────────────────────────────
    // 由 SiliconProbeService 用負載階梯取多個 V/F 工作點，SiliconQuality 做擬合與評定。
    // 這裡只負責把結論攤成畫面要繫結的屬性，以及把「使用者是否已手動改過電壓」傳進去
    // ——電壓被覆寫過的時候，量到的是設定值而不是矽晶的要求，那就不該給百分位。
    private SiliconAssessment? _si;
    private int _siScore;
    public int SiliconScore { get => _siScore; private set { if (SetProperty(ref _siScore, value)) OnPropertyChanged(nameof(SiliconScoreText)); } }
    public string SiliconScoreText => _siScore > 0 ? $"{_siScore}" : "—";
    /// <summary>大數字旁的單位說明：百分位是「同代同頻分布裡的位置」，不是滿分 100 的分數。</summary>
    public string SiliconScoreUnit => _siScore > 0 ? "百分位（估）" : "尚無結果";
    private string _siVerdict = "尚未特徵化";
    public string SiliconVerdict { get => _siVerdict; private set => SetProperty(ref _siVerdict, value); }
    private Severity _siSev = Severity.Neutral;
    public Severity SiliconSeverity { get => _siSev; private set => SetProperty(ref _siSev, value); }
    private string _siDetail =
        "按「開始特徵化」：程式會用 1／2／4… 顆核心的負載階梯讓處理器自己走過睿頻表的各段，"
        + "逐段記下一個 V/F 工作點，再擬合出這顆的 V/F 曲線位置。全程唯讀，不寫入任何倍頻或電壓。";
    public string SiliconDetail { get => _siDetail; private set => SetProperty(ref _siDetail, value); }

    private string _siPercentile = "—";
    public string SiliconPercentileText { get => _siPercentile; private set => SetProperty(ref _siPercentile, value); }
    private string _siConfidence = "—";
    public string SiliconConfidenceText { get => _siConfidence; private set => SetProperty(ref _siConfidence, value); }
    private Severity _siConfSev = Severity.Neutral;
    public Severity SiliconConfidenceSeverity { get => _siConfSev; private set => SetProperty(ref _siConfSev, value); }
    private string _siMethod = "";
    public string SiliconMethodText { get => _siMethod; private set => SetProperty(ref _siMethod, value); }
    private string _siCaveats = "";
    public string SiliconCaveatText { get => _siCaveats; private set => SetProperty(ref _siCaveats, value); }
    public bool HasSiliconCaveats => _siCaveats.Length > 0;

    /// <summary>結果表：每一列都附「這個數字怎麼來的／代表什麼」。</summary>
    public ObservableCollection<SiliconMetric> SiliconMetrics { get; } = new();
    /// <summary>實際取到的 V/F 工作點，逐點列出——這是整個評定的原始素材，不該藏起來。</summary>
    public ObservableCollection<VfPoint> SiliconPoints { get; } = new();
    public bool HasSiliconResult => _si is { Ok: true };

    private bool _siTesting;
    public bool SiliconTesting { get => _siTesting; private set { if (SetProperty(ref _siTesting, value)) { OnPropertyChanged(nameof(SiliconCanTest)); OnPropertyChanged(nameof(SiliconTestButtonText)); } } }
    public bool SiliconCanTest => !_siTesting && HardwareOperationGate.CanRunAdvancedDiagnostics;
    public string SiliconTestButtonText => _siTesting ? "特徵化中…" : "開始特徵化";
    private double _siProgress;
    public double SiliconProgress { get => _siProgress; private set { if (SetProperty(ref _siProgress, value)) OnPropertyChanged(nameof(SiliconProgressPercent)); } }
    public double SiliconProgressPercent => _siProgress * 100;
    private string _siPhase = "";
    public string SiliconPhase { get => _siPhase; private set => SetProperty(ref _siPhase, value); }
    private CancellationTokenSource? _siCts;

    // ── PCIe 瓶頸 / 提醒 ───────────────────────────────────────────────────
    public bool PcieWarningVisible => BclkKnob is not null && Math.Abs(BclkKnob.Target - BclkKnob.Default) > 0.5;
    public string PcieWarningText =>
        "提高外頻 BCLK 會連動拉高 PCIe / DMI 匯流排時脈，可能導致 NVMe、顯示卡或 USB 不穩。"
        + "請於 BIOS 將 PCIe/DMI 鎖定在 100 MHz，並以小幅（每次 +1 MHz）測試。";

    // ── IHS 核心熱區圖 ─────────────────────────────────────────────────────
    public ObservableCollection<CoreRow>? Cores { get; private set; }

    // ── LLC（負載線校準）──────────────────────────────────────────────────
    // 偵測到 VRM PMBus 控制器時直接讀寫硬體；否則僅作 BIOS 設定教學。
    public VrmControllerService? Vrm { get; private set; }

    public IReadOnlyList<string> LlcLevels { get; } = new[]
    {
        "Level 1（最小補償・壓降最大）", "Level 2", "Level 3",
        "Level 4（多數日常超頻建議）", "Level 5", "Level 6",
        "Level 7（接近零壓降）", "Level 8（最大補償・過衝風險最高）",
    };
    private int _llcIndex = 3;   // 預設 Level 4
    public int LlcIndex
    {
        get => _llcIndex;
        set
        {
            if (!SetProperty(ref _llcIndex, value)) return;
            OnPropertyChanged(nameof(LlcAnnotation));
            // 偵測到 VRM 控制器時，選單變更直接寫入硬體
            if (Vrm?.DetectionResult is { Found: true, Chips: { Count: > 0 } chips })
                _ = ApplyLlc();
        }
    }
    public string LlcAnnotation
    {
        get
        {
            // 偵測到 VRM 控制器時，顯示實際硬體讀值
            if (Vrm?.DetectionResult is { Found: true } && Vrm.CurrentLlc is { } llc)
                return $"硬體讀值：{llc.ChipName} LLC = {llc.Level}（暫存器 0x{llc.RawByte:X2}）。\n"
                     + "⚠ 變更此值會直接透過 SMBus 寫入 VRM 控制器。";

            string body = (_llcIndex + 1) switch
            {
                <= 2 => "補償最弱：負載時壓降（Vdroop）最大、過衝最小。最保守，適合追求安全裕度。",
                <= 4 => "輕～中度補償：壓降與過衝取得平衡，日常超頻常用區間。",
                <= 6 => "較強補償：壓降小，但瞬態過衝（overshoot）與漣波（ripple）上升。",
                _ => "最強補償：幾乎零壓降，但過衝與漣波風險最高，長期高負載需留意 VRM 與矽晶壽命。",
            };
            return "※ 未偵測到 VRM PMBus 控制器，此處僅供 BIOS 設定參考。\n" + body;
        }
    }

    /// <summary>
    /// 將當前選取的 LLC 等級寫入 VRM 控制器。
    /// ⚠ 真實寫入——設錯可能導致 CPU 供電不穩、當機、甚至損壞硬體。
    /// </summary>
    /// <returns>寫入成功回 <c>true</c>；未偵測到控制器或寫入失敗回 <c>false</c>。</returns>
    public async Task<bool> ApplyLlc()
    {
        bool ok = false;
        await RunApplyBatchAsync(async generation =>
        {
            var vrm = Vrm;
            if (vrm is null || vrm.DetectionResult is not { Found: true, Chips: { Count: > 0 } chips })
            {
                SetAction("未偵測到 VRM PMBus 控制器，無法寫入 LLC。", Severity.Warning);
                return;
            }
            int level = _llcIndex;
            var chip = chips[0];
            var r = await RunApplyEngineAsync(generation, () => vrm.WriteLlc(chip, level)
                ? OcApplyResult.Success($"已寫入 {chip.ChipName} LLC = {level}（寫後讀回驗證通過）。", null)
                : OcApplyResult.Fail($"LLC 寫入失敗（{chip.ChipName}），請檢查匯流排狀態。"));
            if (r is null || !IsCurrentApply(generation)) return;
            ok = r.Ok;
            if (ok) OnPropertyChanged(nameof(LlcAnnotation));
            SetAction(r.Message, ok ? Severity.Warning : Severity.Critical);
        });
        return ok;
    }

    // ── 看門狗（三層誠實方案）──────────────────────────────────────────────
    private bool _watchdogEnabled;
    public bool WatchdogEnabled
    {
        get => _watchdogEnabled;
        set { if (SetProperty(ref _watchdogEnabled, value)) { _settings.WatchdogEnabled = value; _settings.Save(); if (!value) DisarmWatchdog(); OnPropertyChanged(nameof(WatchdogStatusText)); } }
    }
    private static readonly TimeSpan WatchdogWindow = TimeSpan.FromSeconds(30);
    private ITimer? _watchdogTimer;
    private long _watchdogArmGeneration;
    private long _watchdogStarted;
    private bool _watchdogArmed;
    public bool WatchdogArmed { get { lock (_operationState) return _watchdogArmed; } }
    private int _watchdogLeft;
    public int WatchdogSecondsLeft { get { lock (_operationState) return _watchdogLeft; } }

    public string WatchdogStatusText =>
        IsRecovering ? "安全回復中：後續套用已停止；已送出的引擎操作須先完成，回復結果尚未確認。"
        : !_watchdogEnabled ? "看門狗未啟用。啟用後，套用高風險項目將開始 30 秒倒數，須按「確認穩定」解除，否則自動回復。"
        : _watchdogArmed ? $"看門狗已武裝：{_watchdogLeft} 秒內未確認穩定，將自動回復至最後穩定設定。"
        : "看門狗待命中：套用電壓 / 倍頻 / 外頻後將自動開始 30 秒倒數。";

    public string HwWatchdogText =>
        !_engine.WatchdogPresent ? "硬體看門狗：此平台未偵測到（或 SDK 未提供狀態）。"
        : _engine.WatchdogFailed ? "硬體看門狗：曾觸發（上次可能因超頻不穩而重置）。"
        : _engine.WatchdogRunning ? "硬體看門狗：存在且執行中。"
        : "硬體看門狗：存在（未執行）。";

    private bool _bootRestore;
    public bool BootRestore
    {
        get => _bootRestore;
        set { if (SetProperty(ref _bootRestore, value)) { _settings.BootRestore = value; _settings.Save(); _ = SetBootRestoreAsync(value); } }
    }

    // ── Intel Speed Optimizer（可逆一鍵自動超頻）──────────────────────────
    public bool SpeedOptimizerSupported => _engine.SpeedOptimizerSupported;
    private bool _soOn;
    public bool SpeedOptimizerOn { get => _soOn; private set => SetProperty(ref _soOn, value); }
    private string _soStatus = "—";
    public string SpeedOptimizerStatusText { get => _soStatus; private set => SetProperty(ref _soStatus, value); }

    // ── 設定檔 ─────────────────────────────────────────────────────────────
    public ObservableCollection<string> Profiles { get; } = new();
    private readonly List<string> _profilePaths = new();
    private int _selProfile = -1;
    public int SelectedProfileIndex { get => _selProfile; set => SetProperty(ref _selProfile, value); }
    private string _profileName = "穩定性日常";
    public string ProfileNameInput { get => _profileName; set => SetProperty(ref _profileName, value); }
    public bool HasLastStable { get => _hasLastStable; private set => SetProperty(ref _hasLastStable, value); }

    // ── 全域操作回饋 ───────────────────────────────────────────────────────
    private string _action = "尚未執行任何操作。";
    public string ActionStatus { get => _action; private set => SetProperty(ref _action, value); }
    private Severity _actionSev = Severity.Neutral;
    public Severity ActionSeverity { get => _actionSev; private set => SetProperty(ref _actionSev, value); }

    // A revoked batch remains busy until its dispatched operation and continuation really finish.
    public bool IsApplying
    {
        get { lock (_operationState) return _initializationInFlight || _activeApplyGeneration is not null || _recoveryCompletion is not null; }
    }
    public bool IsRecovering
    {
        get { lock (_operationState) return _recoveryCompletion is not null; }
    }
    public bool CanApply
    {
        get { lock (_operationState) return !_disposed && !_initializationInFlight && _activeApplyGeneration is null && _recoveryCompletion is null; }
    }

    /// <summary>目前待套用之最高「核心」電壓（Vcore 滑桿目標與各核心電壓旋鈕目標取大值），供 UI 判斷是否需高壓確認；不含記憶體電壓。</summary>
    public double HighestPendingVoltage()
    {
        double max = 0;
        if (VcoreKnob is not null) max = Math.Max(max, VcoreKnob.Target);
        foreach (var k in _engine.Knobs)
            if (k.Writable && k.Kind == OcKnobKind.Voltage)
                max = Math.Max(max, k.Target);
        return max;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 初始化
    // ═══════════════════════════════════════════════════════════════════════

    public Task InitializeAsync() => InitializeAsync(
        () => Task.Run<IOcEngine>(() =>
        {
            var xtu = new XtuOcEngine();
            try { xtu.Initialize(); } catch { }
            if (xtu.Status == OcEngineStatus.Ready) return xtu;
            var status = xtu.Status == OcEngineStatus.NotInitialized ? OcEngineStatus.Failed : xtu.Status;
            string message = xtu.StatusMessage;
            xtu.Dispose();
            return new NullOcEngine(status, message);
        }),
        () => Task.Run(() => VrmControllerService.TryCreate(out _)), loadUserProfiles: true);

    // Factories inject completion timing without opening an SDK, bus, or user profile.
    // The slot is retained across actual initialization/detection, not released on caller cancellation.
    internal async Task InitializeAsync(Func<Task<IOcEngine>> createEngine,
        Func<Task<VrmControllerService?>> createVrm, bool loadUserProfiles)
    {
        long generation;
        lock (_operationState)
        {
            if (_disposed || _initializationInFlight || _activeApplyGeneration is not null || _recoveryCompletion is not null) return;
            _initializationInFlight = true;
            generation = ++_operationGeneration;
        }
        bool ownsSlot = false, adopted = false;
        IOcEngine? candidate = null;
        VrmControllerService? candidateVrm = null;
        try
        {
            PublishUiNotification(() => Initializing = true);
            RaiseOperationState();
            PublishUiNotification(RaiseEngineStatus);
            await _engineSerial.WaitAsync();
            ownsSlot = true;
            if (!IsCurrentInitialization(generation)) return;
            candidate = await createEngine();
            IOcEngine prior;
            lock (_operationState)
            {
                if (_disposed || _operationGeneration != generation) return;
                prior = _engine;
                _engine = candidate;
                adopted = true;
            }
            await Task.Run(() => { try { prior.Dispose(); } catch { } });
            if (!IsCurrentInitialization(generation)) return;
            Categorize();
            // Recheck on the worker at the dispatch boundary: Dispose may have revoked admission.
            await Task.Run(() =>
            {
                if (!IsCurrentInitialization(generation)) return;
                try { candidate.SetBootRestore(_bootRestore); }
                catch (Exception ex)
                {
                    // The persisted preference is optional; failure does not establish a hardware result.
                    try { Diag.Swallow("Overclock boot restore preference", ex,
                        "Preference write failed; initialization continues without claiming it was applied."); }
                    catch { }
                }
            });
            if (!IsCurrentInitialization(generation)) return;
            _rollback = CaptureCurrent("初始基準");
            try
            {
                candidateVrm = await createVrm();
                if (candidateVrm is not null)
                {
                    if (!IsCurrentInitialization(generation)) return;
                    await candidateVrm.DetectAsync();
                    if (!IsCurrentInitialization(generation)) return;
                    // Dispose queues behind this slot, so adopted VRM cannot be freed during detection.
                    var priorVrm = Vrm;
                    Vrm = candidateVrm;
                    candidateVrm = null;
                    await Task.Run(() => { try { priorVrm?.Dispose(); } catch { } });
                    if (!IsCurrentInitialization(generation)) return;
                    PublishUiNotification(() => OnPropertyChanged(nameof(LlcAnnotation)));
                }
            }
            catch { /* Optional VRM detection does not invalidate the CPU engine. */ }
            if (!IsCurrentInitialization(generation)) return;
            try { SpeedOptimizerOn = candidate.SpeedOptimizerState > 0; } catch { }
            SpeedOptimizerStatusText = SpeedOptimizerSupported
                ? (SpeedOptimizerOn ? "目前為開啟。" : "支援，目前為關閉。") : "此平台不支援。";
            if (loadUserProfiles)
            {
                RefreshProfiles();
                _hasLastStable = OcSettings.LoadLastStable() is not null;
            }
            if (EngineReady)
                SetAction($"已連接超頻引擎，列出 {candidate.Knobs.Count} 個可調項（可寫 {candidate.Knobs.Count(k => k.Writable)} 個）。"
                    + (_hasLastStable ? "偵測到『最後穩定設定』，可於下方一鍵回復。" : ""), Severity.Good);
            else SetAction(candidate.StatusMessage, EngineStatusSeverity);
            PublishUiNotification(RaisePlanner);
            foreach (var property in new[] { nameof(HasLastStable), nameof(SpeedOptimizerSupported),
                nameof(HwWatchdogText), nameof(ProcessorFamilyText) })
                PublishUiNotification(() => OnPropertyChanged(property));
        }
        finally
        {
            // A result arriving after revocation remains locally owned and is disposed before slot release.
            await Task.Run(() =>
            {
                try { candidateVrm?.Dispose(); } catch { }
                if (!adopted) { try { candidate?.Dispose(); } catch { } }
            });
            if (ownsSlot) _engineSerial.Release();
            lock (_operationState) _initializationInFlight = false;
            PublishUiNotification(() => Initializing = false);
            RaiseOperationState();
            PublishUiNotification(RaiseEngineStatus);
        }
    }

    private bool IsCurrentInitialization(long generation)
    {
        lock (_operationState)
            return !_disposed && _initializationInFlight && _operationGeneration == generation;
    }

    private void Categorize()
    {
        CoreKnobs.Clear(); CacheKnobs.Clear(); PowerKnobs.Clear(); MemoryKnobs.Clear(); BusKnobs.Clear(); AdvancedKnobs.Clear();
        CoreTuneRows.Clear();

        // 頻率規劃用旋鈕先挑出：全核心倍頻優先取「可寫的 Performance Core Ratio」，
        // 而非唯讀的 Id0（舊版誤取唯讀項，導致看不到可調整的全核心倍頻）。
        CoreRatioKnob =
            _engine.Knobs.FirstOrDefault(k => k.Kind == OcKnobKind.CoreRatio && k.Writable
                && k.RawName.Equals("Performance Core Ratio", StringComparison.OrdinalIgnoreCase))
            ?? _engine.Knobs.FirstOrDefault(k => k.Kind == OcKnobKind.CoreRatio && k.Writable
                && !k.RawName.Contains("Active", StringComparison.OrdinalIgnoreCase)
                && OcNaming.CoreIndexOf(k.RawName) is null)
            ?? _engine.Knobs.FirstOrDefault(k => k.Kind == OcKnobKind.CoreRatio && k.Writable)
            ?? _engine.Knobs.FirstOrDefault(k => k.Kind == OcKnobKind.CoreRatio);
        BclkKnob = _engine.Knobs.FirstOrDefault(k => k.Kind == OcKnobKind.Bclk);

        // 合併的每核心調節列（倍頻上限＋電壓覆寫＋電壓偏移，依核心序號配對）
        var perCore = new Dictionary<int, (OcKnob? Ratio, OcKnob? Voltage, OcKnob? Offset)>();

        foreach (var k in _engine.Knobs)
        {
            // 規劃區已呈現的兩個旋鈕不重複列於分區清單
            if (ReferenceEquals(k, CoreRatioKnob) || ReferenceEquals(k, BclkKnob)) continue;

            // 每核心睿頻族（Performance Core N ...）→ 併入 CoreTuneRows
            if (k.Section == OcSection.Advanced && OcNaming.CoreIndexOf(k.RawName) is int ci)
            {
                var cur = perCore.TryGetValue(ci, out var t) ? t : (null, null, null);
                if (k.Kind == OcKnobKind.CoreRatio) cur.Ratio = k;
                else if (k.Kind == OcKnobKind.Voltage) cur.Voltage = k;
                else if (k.Kind == OcKnobKind.VoltageOffset) cur.Offset = k;
                else { AdvancedKnobs.Add(k); continue; }
                perCore[ci] = cur;
                continue;
            }

            switch (k.Section)
            {
                case OcSection.Core: CoreKnobs.Add(k); break;
                case OcSection.Cache: CacheKnobs.Add(k); break;
                case OcSection.Power: PowerKnobs.Add(k); break;
                case OcSection.Memory: MemoryKnobs.Add(k); break;
                case OcSection.Bus: BusKnobs.Add(k); break;
                default: AdvancedKnobs.Add(k); break;
            }
        }

        foreach (var ci in perCore.Keys.OrderBy(x => x))
        {
            var (r, v, o) = perCore[ci];
            CoreTuneRows.Add(new CoreTuneRow(ci, r, v, o));
        }

        // Vcore 調節旋鈕挑選：一律限 Section=Core（分類器已把快取／Uncore／Ring 歸為 Cache，
        // 天然排除，故不會再發生「錯抓快取電壓當 Vcore」的混淆）。挑選順序：
        //   1) 絕對核心電壓覆寫（Kind=Voltage），名稱含 "core" 者優先，其餘同 Section 亦可；
        //   2) 平台無絕對覆寫時（如本機、多數僅支援偏移的世代），退用「核心電壓偏移」（Kind=VoltageOffset）。
        // 如此既避免核心／快取混淆，又確保只要平台提供任一種核心電壓調節，就不會誤判為「無電壓可調」。
        VcoreKnob =
            _engine.Knobs.FirstOrDefault(k => k.Kind == OcKnobKind.Voltage && k.Section == OcSection.Core
                    && k.RawName.Contains("core", StringComparison.OrdinalIgnoreCase))
            ?? _engine.Knobs.FirstOrDefault(k => k.Kind == OcKnobKind.Voltage && k.Section == OcSection.Core)
            ?? _engine.Knobs.FirstOrDefault(k => k.Kind == OcKnobKind.VoltageOffset && k.Section == OcSection.Core
                    && k.RawName.Contains("core", StringComparison.OrdinalIgnoreCase))
            ?? _engine.Knobs.FirstOrDefault(k => k.Kind == OcKnobKind.VoltageOffset && k.Section == OcSection.Core);

        // 若平台無「全域核心電壓」旋鈕（本機即是：核心電壓僅能逐核心覆寫），
        // 蒐集全部「每核心電壓覆寫」（Processor.Turbo.VoltageOverride）作為扇出目標，
        // 並合成一個聚合旋鈕當作 Vcore 卡的滑桿模型；「套用電壓」時把同一目標值一次寫入全部核心，
        // 等同 Intel XTU 於此類平台呈現的單一「核心電壓」控制。各核心仍可於「每核心獨立調節」個別微調。
        _vcoreFanout.Clear();
        _vcoreSeeded = false;
        if (VcoreKnob is null)
        {
            var perCoreV = _engine.Knobs
                .Where(k => k.Kind == OcKnobKind.Voltage && k.Writable
                            && k.Category.Contains("VoltageOverride", StringComparison.OrdinalIgnoreCase)
                            && OcNaming.CoreIndexOf(k.RawName) is not null)
                .ToList();
            if (perCoreV.Count > 0)
            {
                _vcoreFanout.AddRange(perCoreV);
                var rep = perCoreV[0];
                VcoreKnob = new OcKnob(0xFFFFFF01u, "All-Core Voltage Override", "Aggregate.VoltageOverride",
                    rep.Unit, OcKnobKind.Voltage, rep.Min, rep.Max, rep.Default, rep.Boot, rep.Active,
                    rep.RealTime, rep.RequiresReboot, false, true);
                VcoreScopeText = $"此平台無全域核心電壓，僅提供逐核心「電壓覆寫」（Voltage Override，絕對值）。於此調整會一次寫入全部 {perCoreV.Count} 個核心，"
                               + "並以固定電壓取代原本隨負載變動的自適應曲線（等同把 Vcore 釘死為定值）。過低易當機、過高易過熱，請謹慎設定；"
                               + "若只想微調個別核心，請改用下方「每核心獨立調節」。";
            }
            else VcoreScopeText = "";
        }
        else VcoreScopeText = "";

        if (CoreRatioKnob is not null) CoreRatioKnob.PropertyChanged += OnPlannerKnobChanged;
        if (BclkKnob is not null) BclkKnob.PropertyChanged += OnPlannerKnobChanged;

        // 三個規劃旋鈕（CoreRatioKnob／BclkKnob／VcoreKnob）為非同步 Categorize 於檢視首次繫結後才指派；
        // 它們原為無通知的自動屬性，繫結（ContentControl.Content 與 VcoreKnob.* 子路徑）永遠停留在最初的 null，
        // 造成規劃卡出現空白鬼影列、Vcore 滑桿失效。此處明確發出屬性變更通知，讓相關繫結在指派後更新。
        foreach (var n in new[] { nameof(HasCoreKnobs), nameof(HasCacheKnobs), nameof(HasPowerKnobs), nameof(HasMemoryKnobs),
                                  nameof(HasBusKnobs), nameof(HasAdvancedKnobs), nameof(HasFrequencyPlanner), nameof(HasBclk),
                                  nameof(HasCoreTuneRows), nameof(HasVcore),
                                  nameof(CoreRatioKnob), nameof(BclkKnob), nameof(VcoreKnob) })
            OnPropertyChanged(n);
    }

    private void OnPlannerKnobChanged(object? _, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OcKnob.Target) or nameof(OcKnob.TargetText) or nameof(OcKnob.Active))
            RaisePlanner();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 每秒遙測（由 MainViewModel 的 1 秒計時器呼叫）
    // ═══════════════════════════════════════════════════════════════════════

    // 每秒遙測。ReadCoreVoltage／ReadMonitor 走阻塞式 IPC（可達數百毫秒～數秒），
    // 若壓在 UI 執行緒會造成每秒卡頓、並拖累看門狗倒數；故先在背景執行緒預抓，再回 UI 執行緒更新。
    public async Task TickAsync(SensorService? live)
    {
        _live = live;               // 體質特徵化在 MSR 不可用時要靠感測器補位，留一份最新的引用
        bool needVcore = live?.CpuVoltage is null or <= 0;
        bool needVrm = live?.VrmTempC is null;
        double? engineVcore = null, engineVrm = null;
        if ((needVcore || needVrm) && EngineReady)
        {
            try
            {
                await RunEngineAsync(() =>
                {
                    if (needVcore) engineVcore = _engine.ReadCoreVoltage();
                    if (needVrm) engineVrm = _engine.ReadMonitor("VR ", "VRM", "MOS", "VR MOS", "VCCIN");
                    return true;
                });
            }
            catch { /* 單次遙測讀取失敗不影響後續 */ }
        }
        await TickCore(live, engineVcore, engineVrm);
    }

    private async Task TickCore(SensorService? live, double? engineVcore, double? engineVrm)
    {
        if (Cores is null && live?.CpuCores is not null) { Cores = live.CpuCores; OnPropertyChanged(nameof(Cores)); }

        // Only measured voltage: control values (especially mV offsets) are not absolute Vcore telemetry.
        double? vcore = live?.CpuVoltage;
        if (vcore is null or <= 0) vcore = engineVcore;

        // 聚合 Vcore：以實測電壓回填滑桿「現值」；首次取得有效電壓時，把目標值種為目前電壓（滑桿起點＝目前電壓）。
        if (VcoreKnob is not null && _vcoreFanout.Count > 0 && vcore is double vc && vc > 0)
        {
            VcoreKnob.Active = vc;
            if (!_vcoreSeeded) { VcoreKnob.Target = vc; _vcoreSeeded = true; }
        }

        double clock = live?.CpuClock ?? 0;
        double? temp = live?.CpuTemp;
        double? power = live?.CpuPowerW;

        // 電壓錶 + 徽章 + 示波器
        if (vcore is double v && v > 0)
        {
            VoltmeterValue = v;
            VoltageValueText = $"{v:0.000} V";
            var band = VoltageBand.Eval(v);
            VoltageBadgeText = band.Label;
            VoltageBadgeBrush = band.Brush;
            VoltageBadgeFlashing = band.Flashing;
            OscilloVolt.Push(v);
        }
        else
        {
            VoltmeterValue = 0;
            VoltageValueText = "—";
            VoltageBadgeText = "—";
            VoltageBadgeBrush = SeverityToBrushConverter.Neutral;
            VoltageBadgeFlashing = false;
            OscilloVolt.Push(0);
        }

        // 電流：I ≈ P / V（估算）
        if (power is double p && p > 0 && vcore is double vv && vv > 0)
        {
            double amps = p / vv;
            CurrentText = $"≈ {amps:0} A（估算 P÷V）";
            OscilloCurrent.Push(amps);
        }
        else
        {
            CurrentText = "—";
            OscilloCurrent.Push(0);
        }

        // 核心溫度
        if (temp is double t)
        {
            CoreTempText = $"{t:0} °C";
            CoreTempSeverity = Health.Cpu(t);
        }
        else { CoreTempText = "—"; CoreTempSeverity = Severity.Neutral; }

        // 有效頻率
        EffectiveClockText = clock > 0 ? $"{clock / 1000.0:0.00} GHz（{clock:0} MHz）" : "—";

        // PL1 / PL2（取自功耗牆旋鈕現值）
        Pl1Text = PowerKnobs.FirstOrDefault(k => k.RawName.Contains("Power Max", StringComparison.OrdinalIgnoreCase)
                                              || k.Label.Contains("PL1"))?.ActiveText ?? "—";
        Pl2Text = PowerKnobs.FirstOrDefault(k => k.RawName.Contains("Short", StringComparison.OrdinalIgnoreCase)
                                              || k.Label.Contains("PL2"))?.ActiveText ?? "—";

        // VRM 溫度：LHM 優先，缺則採背景預抓的 XTU 監控值
        double? vrm = live?.VrmTempC ?? engineVrm;
        if (vrm is double vt)
        {
            VrmTempText = $"{vt:0} °C";
            VrmTempSeverity = vt >= 100 ? Severity.Critical : vt >= 85 ? Severity.Serious : vt >= 60 ? Severity.Warning : Severity.Good;
        }
        else { VrmTempText = "—"; VrmTempSeverity = Severity.Neutral; }

        // 體質特徵化不再從這裡取樣：它自己控制負載階梯、自己以 MSR 高頻取樣（見 SiliconProbeService），
        // 每秒一筆的遙測撐不起一條 V/F 迴歸線。

        // 燒機：即時餵入 + 過熱保護（>100°C 自動暫停並回復）
        if (_stress.IsRunning)
        {
            _stress.Sample(temp, clock, live?.CpuLoad ?? 0);
            if (temp is double ht && ht >= 100 && !_stressGuardTripped)
            {
                _stressGuardTripped = true;
                _stress.Cancel();
                await RollbackToStableAsync($"⚠ 燒機過程偵測到 {ht:0}°C（≥100°C），已請求停止燒機", Severity.Critical);
            }
            // 誠實告知：讀不到 CPU 溫度時過熱保護無法運作，套用超頻後燒機風險由使用者承擔（僅提示一次）
            else if (temp is null && !_stressTempMissingWarned)
            {
                _stressTempMissingWarned = true;
                SetAction("⚠ 目前讀不到 CPU 溫度，燒機過熱自動保護無法運作，請自行留意散熱。", Severity.Serious);
            }
        }
        else { _stressGuardTripped = false; _stressTempMissingWarned = false; }

        // Display refresh is monotonic too; recovery admission does not depend on telemetry ticks.
        long arm;
        bool expired;
        lock (_operationState)
        {
            arm = _watchdogArmGeneration;
            var left = WatchdogWindow - _timeProvider.GetElapsedTime(_watchdogStarted);
            _watchdogLeft = _watchdogArmed ? Math.Max(0, (int)Math.Ceiling(left.TotalSeconds)) : 0;
            expired = _watchdogArmed && left <= TimeSpan.Zero;
        }
        PublishWatchdogState();
        if (expired) await ExpireWatchdogAsync(arm);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 體質（矽晶品質）特徵化：自控負載階梯 ＋ 多點 V/F 擬合
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 開始體質特徵化。實際取樣在背景執行緒（<see cref="SiliconProbeService.Run"/>）：它自己起負載
    /// 階梯、自己以 MSR 高頻取樣，約 20–30 秒後回一份完整結論，期間可按「停止」中止。
    /// 全程唯讀——不寫入任何倍頻或電壓，只是控制「同時有幾顆核心在忙」讓處理器自己走睿頻表。
    /// </summary>
    public async Task RetestSilicon()
    {
        if (_siTesting) return;                 // 防止重入：取樣進行中忽略再次點擊
        if (!HardwareOperationGate.CanRunAdvancedDiagnostics)
        {
            SetAction("CPU 體質特徵化已阻止：請先在設定啟用第一級進階診斷。", Severity.Warning);
            return;
        }
        SiliconTesting = true;
        _siCts = new CancellationTokenSource();
        var ct = _siCts.Token;

        SiliconVerdict = "特徵化中…";
        SiliconSeverity = Severity.Neutral;
        SiliconScore = 0;
        SiliconPercentileText = "—";
        SiliconConfidenceText = "—";
        SiliconConfidenceSeverity = Severity.Neutral;
        SiliconCaveatText = "";
        SiliconMethodText = "";
        SiliconMetrics.Clear();
        SiliconPoints.Clear();
        _si = null;
        OnPropertyChanged(nameof(HasSiliconResult));
        OnPropertyChanged(nameof(HasSiliconCaveats));
        SiliconProgress = 0;
        SiliconPhase = "準備中";
        SiliconDetail = "取樣進行中：處理器會被逐階推到滿載，畫面動畫已暫停，"
                      + "以免重繪本身的功耗與發熱進到量測裡。期間請不要跑其他吃 CPU 的程式。";

        var opt = new SiliconProbeService.Options
        {
            Sensors = BuildSensorFallback(),
            ManualVoltage = HasManualVoltage(out string note),
            ManualVoltageNote = note,
        };
        var progress = new Progress<SiliconProbeProgress>(p =>
        {
            SiliconProgress = p.Fraction;
            SiliconPhase = p.Phase;
        });

        try
        {
            var result = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                // Recheck at dispatch: disabling the gate before the worker starts prevents load.
                if (!HardwareOperationGate.CanRunAdvancedDiagnostics)
                    throw new InvalidOperationException("第一級進階診斷已停用，未開始體質特徵化。");
                return _siliconProbe(opt, progress, ct);
            }, ct);
            PublishSilicon(result);
        }
        catch (OperationCanceledException)
        {
            SiliconVerdict = "已中止";
            SiliconSeverity = Severity.Neutral;
            SiliconDetail = "特徵化已中止。已取到的工作點不足以擬合一條可信的 V/F 線，因此不出結果。";
        }
        catch (Exception ex)
        {
            SiliconVerdict = "特徵化失敗";
            SiliconSeverity = Severity.Warning;
            SiliconDetail = "特徵化失敗：" + ex.Message;
            Diag.Swallow("體質特徵化", ex, "本頁如實顯示失敗原因，不給估算值");
        }
        finally
        {
            SiliconTesting = false;             // 無論成敗都解除，重新啟用按鈕
            SiliconProgress = 0;
            SiliconPhase = "";
            _siCts?.Dispose();
            _siCts = null;
        }
    }

    /// <summary>中止進行中的特徵化。負載執行緒會在下一次檢查取消時收工。</summary>
    public void CancelSilicon() => _siCts?.Cancel();

    private void PublishSilicon(SiliconAssessment a)
    {
        _si = a;
        SiliconScore = a.Percentile;
        SiliconVerdict = a.Grade;
        SiliconSeverity = a.Severity;
        SiliconDetail = a.Summary;
        SiliconPercentileText = a.PercentileText;
        SiliconConfidenceText = a.ConfidenceText;
        SiliconConfidenceSeverity = a.ConfidenceSeverity;
        SiliconMethodText = a.MethodText;
        SiliconCaveatText = a.CaveatText;
        SiliconMetrics.Clear();
        foreach (var m in a.Metrics) SiliconMetrics.Add(m);
        SiliconPoints.Clear();
        foreach (var p in a.Points) SiliconPoints.Add(p);
        OnPropertyChanged(nameof(HasSiliconCaveats));
        OnPropertyChanged(nameof(HasSiliconResult));
    }

    /// <summary>
    /// 這台機器現在有沒有被手動改過核心電壓。有的話，量到的電壓是<b>設定值</b>而不是晶片自己要求的
    /// 值——這正是舊版最大的漏洞：手動降壓過的機器會拿到一個虛高的體質分數，而畫面上看不出來。
    /// </summary>
    private bool HasManualVoltage(out string note)
    {
        note = "";
        var hits = new List<string>();
        foreach (var k in _engine.Knobs)
        {
            if (k.Kind == OcKnobKind.Voltage && k.Active > 0 && Math.Abs(k.Active - k.Default) > 0.005)
                hits.Add($"{k.Label} 現為 {k.Fmt(k.Active)}（預設 {k.Fmt(k.Default)}）");
            else if (k.Kind == OcKnobKind.VoltageOffset && Math.Abs(k.Active - k.Default) > 1e-6)
                hits.Add($"{k.Label} 現為 {k.Fmt(k.Active)}");
        }
        if (hits.Count == 0) return false;
        note = "偵測到手動電壓設定（" + string.Join("、", hits.Take(3)) + (hits.Count > 3 ? " 等" : "")
             + "）。此時量到的是你設定的電壓，不是這顆晶片自己要求的電壓；拿去跟世代參考線相比，"
             + "得到的是關於「設定」而不是關於「體質」的數字，因此本次不給百分位。"
             + "要量體質請先還原預設值（本頁下方「還原預設」）再重測——ΔV 與 V/F 斜率仍是有效的實測量。";
        return true;
    }

    /// <summary>MSR 不可用時的補位讀值。包成委派傳進去，取樣服務因此不必認識 SensorService。</summary>
    private SiliconProbeService.SensorFallback BuildSensorFallback() => new(
        () => _live?.CpuVoltage,
        () => _live?.CpuClock ?? 0,
        () => _live?.CpuTemp,
        () => _live?.CpuPowerW);

    // ═══════════════════════════════════════════════════════════════════════
    // 寫入操作
    // ═══════════════════════════════════════════════════════════════════════

    // All normal mutations enter one batch. Recovery revokes that batch before waiting for I/O.
    private async Task RunApplyBatchAsync(Func<long, Task> apply)
    {
        long generation;
        lock (_operationState)
        {
            if (_disposed || _initializationInFlight || _activeApplyGeneration is not null || _recoveryCompletion is not null) return;
            generation = ++_operationGeneration;
            _activeApplyGeneration = generation;
        }
        try
        {
            RaiseOperationState();
            await apply(generation);
        }
        catch (Exception ex)
        {
            if (IsCurrentApply(generation))
                SetAction("引擎操作發生例外，硬體狀態須重新確認：" + ex.Message, Severity.Critical);
        }
        finally
        {
            lock (_operationState)
            {
                if (_activeApplyGeneration == generation) _activeApplyGeneration = null;
            }
            RaiseOperationState();
        }
    }

    private bool IsCurrentApply(long generation)
    {
        lock (_operationState)
            return !_disposed && _activeApplyGeneration == generation
                && _operationGeneration == generation && _recoveryCompletion is null;
    }

    // No cancellation/timeout wrapper: a dispatched SDK/IPC call owns the slot until it returns.
    private async Task<T> RunEngineAsync<T>(Func<T> operation)
    {
        await _engineSerial.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(operation).ConfigureAwait(false); }
        finally { _engineSerial.Release(); }
    }

    private Task<OcApplyResult?> RunApplyEngineAsync(long generation, Func<OcApplyResult> operation)
        => RunEngineAsync<OcApplyResult?>(() =>
        {
            // This check is the dispatch boundary. Recovery waits for any call already dispatched.
            if (!IsCurrentApply(generation)) return null;
            return operation();
        });

    private async Task SetBootRestoreAsync(bool value)
    {
        try { await RunEngineAsync(() => { lock (_operationState) { if (_disposed) return false; }
                _engine.SetBootRestore(value); return true; }); }
        catch { /* Persisted preference failure does not claim a hardware result. */ }
    }

    private void RaiseOperationState()
    {
        foreach (var property in new[] { nameof(IsApplying), nameof(IsRecovering), nameof(CanApply), nameof(WatchdogStatusText) })
            PublishUiNotification(() => OnPropertyChanged(property));
    }

    private static void PublishUiNotification(Action notification)
    {
        try { notification(); }
        catch (Exception ex)
        {
            // An observer fault must not strand admission, abandon recovery, or lose its completion.
            try { Diag.Swallow("超頻 UI 通知", ex, "硬體操作協調仍會完成；部分畫面通知未送達。"); }
            catch { }
        }
    }

    private async Task<OcApplyResult?> WriteKnobAsync(long generation, OcKnob knob, double value)
    {
        if (!IsCurrentApply(generation)) return null;
        using var writePermit = HardwareOperationGate.TryBeginHighRiskOperation();
        if (writePermit is null && !HardwareOperationGate.CanWriteHardware)
            return new OcApplyResult(false, "高風險硬體寫入未啟用；未套用此設定。", null);
        var r = await RunApplyEngineAsync(generation, () => _engine.Apply(knob, value));
        if (r is null || !IsCurrentApply(generation)) return null;
        knob.ApplyStatus = r.Message;
        knob.ApplySeverity = r.Ok ? (knob.RequiresReboot ? Severity.Warning : Severity.Good) : Severity.Critical;
        return r;
    }

    public Task ApplyKnob(OcKnob knob) => RunApplyBatchAsync(async generation =>
    {
        SetAction($"正在套用「{knob.Label}」…", Severity.Neutral);
        var r = await WriteKnobAsync(generation, knob, knob.Target);
        if (r is null || !IsCurrentApply(generation)) return;
        SetAction(r.Message, r.Ok ? (knob.RequiresReboot ? Severity.Warning : Severity.Good) : Severity.Critical);
        if (r.Ok) ArmWatchdogIfNeeded(knob);
        RaisePlanner();
    });

    public Task ApplyPlannerAsync() => RunApplyBatchAsync(async generation =>
    {
        var targets = new List<OcKnob>();
        if (CoreRatioKnob is { } ratio) targets.Add(ratio);
        if (BclkKnob is { } bclk) targets.Add(bclk);
        if (targets.Count == 0) { SetAction("此平台無可調整的倍頻／外頻。", Severity.Neutral); return; }
        SetAction("正在套用目標時脈…", Severity.Neutral);
        int ok = 0; bool armed = false;
        foreach (var k in targets)
        {
            var r = await WriteKnobAsync(generation, k, k.Target);
            if (r is null || !IsCurrentApply(generation)) return;
            if (r.Ok) { ok++; if (IsRisky(k)) armed = true; }
        }
        if (armed && _watchdogEnabled) ArmWatchdog();
        SetAction($"目標時脈：已套用 {ok} / {targets.Count} 項。" + (ok < targets.Count ? "部分項目未如預期生效，請檢視各項狀態。" : ""),
                  ok == targets.Count ? Severity.Good : Severity.Warning);
        RaisePlanner();
    });

    // Aggregate Vcore is a measured display; a requested target is never assigned to Active.
    public Task ApplyVcore()
    {
        var aggregate = VcoreKnob;
        if (aggregate is null) return Task.CompletedTask;
        if (_vcoreFanout.Count == 0) return ApplyKnob(aggregate);
        return RunApplyBatchAsync(async generation =>
        {
            double target = aggregate.Target;
            var targets = _vcoreFanout.ToArray();
            foreach (var k in targets) k.Target = target;
            SetAction($"正在對 {targets.Length} 個核心套用 {target:0.000} V…", Severity.Neutral);
            int ok = 0; bool risky = false;
            foreach (var k in targets)
            {
                var r = await WriteKnobAsync(generation, k, target);
                if (r is null || !IsCurrentApply(generation)) return;
                if (r.Ok) { ok++; if (IsRisky(k)) risky = true; }
            }
            bool allok = ok == targets.Length;
            aggregate.ApplyStatus = allok
                ? $"已對全部 {targets.Length} 個核心送出 {target:0.000} V；聚合現值等待感測器更新。"
                : $"僅 {ok}/{targets.Length} 個核心套用成功，請檢視「每核心獨立調節」各項狀態。";
            aggregate.ApplySeverity = allok ? Severity.Good : Severity.Warning;
            if (risky && _watchdogEnabled) ArmWatchdog();
            SetAction(aggregate.ApplyStatus, aggregate.ApplySeverity);
            RaisePlanner();
        });
    }

    public Task ApplyAll() => RunApplyBatchAsync(async generation =>
    {
        var dirty = _engine.Knobs.Where(k => k.Writable && k.IsDirty).ToList();
        if (dirty.Count == 0) { SetAction("沒有待套用的變更。", Severity.Neutral); return; }
        SetAction($"正在套用 {dirty.Count} 項變更…", Severity.Neutral);
        int ok = 0; bool armed = false;
        foreach (var k in dirty)
        {
            var r = await WriteKnobAsync(generation, k, k.Target);
            if (r is null || !IsCurrentApply(generation)) return;
            if (r.Ok) { ok++; if (IsRisky(k)) armed = true; }
        }
        if (armed && _watchdogEnabled) ArmWatchdog();
        SetAction($"已套用 {ok} / {dirty.Count} 項變更。" + (ok < dirty.Count ? "部分項目未如預期生效，請檢視各項狀態。" : ""),
                  ok == dirty.Count ? Severity.Good : Severity.Warning);
        RaisePlanner();
    });

    public Task DiscardAll() => RunApplyBatchAsync(async generation =>
    {
        var r = await RunApplyEngineAsync(generation, () => _engine.Discard()
            ? OcApplyResult.Success("已取消所有未套用的變更，並重新讀取硬體現值。", null)
            : OcApplyResult.Fail("取消變更失敗（引擎不可用）。"));
        if (r is null || !IsCurrentApply(generation)) return;
        if (r.Ok) foreach (var k in _engine.Knobs) k.ResetTargetToActive();
        SetAction(r.Message, r.Ok ? Severity.Good : Severity.Critical);
        RaisePlanner();
    });

    public Task RestoreDefaults() => RunApplyBatchAsync(async generation =>
    {
        using var recovery = HardwareOperationGate.BeginRecoveryOperation();
        SetAction("正在還原預設值…", Severity.Neutral);
        var r = await RunApplyEngineAsync(generation, () => _engine.RestoreDefaults());
        if (r is null || !IsCurrentApply(generation)) return;
        SetAction(r.Message, r.Ok ? Severity.Good : Severity.Critical);
        if (r.Ok) DisarmWatchdog();
        RaisePlanner();
    });

    // ═══════════════════════════════════════════════════════════════════════
    // 設定檔
    // ═══════════════════════════════════════════════════════════════════════

    public void CaptureProfile()
    {
        var p = new OcProfile { Name = string.IsNullOrWhiteSpace(_profileName) ? "未命名" : _profileName.Trim() };
        foreach (var k in _engine.Knobs.Where(k => k.Writable))
            p.Items.Add(new OcProfileItem { Id = k.Id, Name = k.RawName, Value = k.Target });
        try
        {
            string path = OcSettings.SaveProfile(p);
            RefreshProfiles();
            SetAction($"已儲存設定檔「{p.Name}」（{p.Items.Count} 項）至 {path}", Severity.Good);
        }
        catch (Exception ex) { SetAction("儲存設定檔失敗：" + ex.Message, Severity.Critical); }
    }

    public void ExportProfileTo(string path)
    {
        var p = new OcProfile { Name = string.IsNullOrWhiteSpace(_profileName) ? "未命名" : _profileName.Trim() };
        foreach (var k in _engine.Knobs.Where(k => k.Writable))
            p.Items.Add(new OcProfileItem { Id = k.Id, Name = k.RawName, Value = k.Target });
        try { OcSettings.ExportProfile(p, path); SetAction($"已匯出設定檔至 {path}", Severity.Good); }
        catch (Exception ex) { SetAction("匯出設定檔失敗：" + ex.Message, Severity.Critical); }
    }

    public async Task ApplySelectedProfile()
    {
        if (IsApplying) return;
        if (_selProfile < 0 || _selProfile >= _profilePaths.Count) { SetAction("請先選擇一個設定檔。", Severity.Neutral); return; }
        var p = OcSettings.LoadProfile(_profilePaths[_selProfile]);
        if (p is null) { SetAction("無法讀取所選設定檔。", Severity.Critical); return; }
        await ApplyProfile(p, $"設定檔「{p.Name}」");
    }

    public async Task ImportProfileFrom(string path)
    {
        if (IsApplying) return;
        var p = OcSettings.LoadProfile(path);
        if (p is null) { SetAction("無法讀取設定檔：" + path, Severity.Critical); return; }
        await ApplyProfile(p, $"設定檔「{p.Name}」");
    }

    public async Task ApplyLastStable()
    {
        if (IsApplying) return;
        if (!HardwareOperationGate.IsHighRiskModeEnabled)
        {
            WriteGate.Record("CPU last-stable profile", nameof(ApplyLastStable),
                "高風險模式未啟用，未套用歷史設定", succeeded: false);
            SetAction("套用最後穩定設定需要啟用第二級高風險硬體操作。", Severity.Warning);
            return;
        }
        var p = OcSettings.LoadLastStable();
        if (p is null) { SetAction("尚無『最後穩定設定』可回復。", Severity.Neutral); return; }
        await ApplyProfile(p, "最後穩定設定");
    }

    /// <summary>把目前套用的目標值標記為「穩定」：存為 last-stable、更新看門狗回復基準、並解除看門狗。</summary>
    public void ConfirmStable()
    {
        OcProfile p;
        ITimer? timer;
        lock (_operationState)
        {
            // Confirmation and deadline admission share the same ordering point.
            if (_disposed || _initializationInFlight || _activeApplyGeneration is not null || _recoveryCompletion is not null)
                return;
            p = CaptureCurrent("最後穩定設定");
            _rollback = p;
            timer = DisarmWatchdogLocked();
        }
        timer?.Dispose();
        PublishWatchdogState();
        OcSettings.SaveLastStable(p);
        HasLastStable = true;
        SetAction("已將目前設定標記為穩定並存檔；看門狗已解除。", Severity.Good);
    }

    public Task SetSpeedOptimizer(bool on, bool extreme) => RunApplyBatchAsync(async generation =>
    {
        using var writePermit = HardwareOperationGate.TryBeginHighRiskOperation();
        if (writePermit is null)
        {
            WriteGate.TryAuthorizeHardwareWrite("CPU Speed Optimizer", nameof(SetSpeedOptimizer), on ? "啟用" : "停用");
            SetAction("高風險硬體寫入未啟用；未變更 Speed Optimizer。", Severity.Warning);
            return;
        }
        SetAction(on ? "正在啟用 Intel Speed Optimizer…" : "正在關閉 Intel Speed Optimizer…", Severity.Neutral);
        var r = await RunApplyEngineAsync(generation, () => _engine.SetSpeedOptimizer(on, extreme));
        if (r is null || !IsCurrentApply(generation)) return;
        if (r.Ok)
        {
            try
            {
                SpeedOptimizerOn = _engine.SpeedOptimizerState > 0;
                SpeedOptimizerStatusText = SpeedOptimizerOn ? "目前為開啟。" : "支援，目前為關閉。";
            }
            catch { SpeedOptimizerStatusText = "引擎已接受操作，但無法確認目前狀態。"; }
        }
        SetAction(r.Message, r.Ok ? Severity.Good : Severity.Critical);
    });

    public void StartStress()
    {
        if (!HardwareOperationGate.CanRunAdvancedDiagnostics)
        {
            SetAction("CPU 壓力測試已阻止：請先在設定啟用第一級進階診斷。", Severity.Warning);
            return;
        }
        _stressGuardTripped = false;
        _stress.Start();
        if (_stress.IsRunning) SetAction("已開始簡易燒機（過熱 ≥100°C 將自動停止並回復）。", Severity.Warning);
        else SetAction(_stress.StatusLine, Severity.Warning);
    }

    public void RefreshPolicy()
    {
        OnPropertyChanged(nameof(CanUseHardwareWrites));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(SiliconCanTest));
        _stress.RefreshPolicy();
    }
    public void StopStress() { _stress.Cancel(); SetAction("已請求停止燒機。", Severity.Neutral); }

    // ═══════════════════════════════════════════════════════════════════════
    // 內部工具
    // ═══════════════════════════════════════════════════════════════════════

    private Task ApplyProfile(OcProfile p, string label) => RunApplyBatchAsync(async generation =>
    {
        SetAction($"正在套用{label}…", Severity.Neutral);
        int ok = 0, total = 0; bool armed = false;
        foreach (var item in p.Items)
        {
            if (!IsCurrentApply(generation)) return;
            var k = _engine.Knobs.FirstOrDefault(x => x.Id == item.Id);
            if (k is null || !k.Writable) continue;
            total++;
            k.Target = item.Value;
            var r = await WriteKnobAsync(generation, k, item.Value);
            if (r is null || !IsCurrentApply(generation)) return;
            if (r.Ok) { ok++; if (IsRisky(k)) armed = true; }
        }
        if (armed && _watchdogEnabled) ArmWatchdog();
        SetAction($"已套用{label}：{ok} / {total} 項成功。", ok == total && total > 0 ? Severity.Good : Severity.Warning);
        RaisePlanner();
    });

    /// <summary>擷取目前硬體現值（Active）為一份設定檔，作為回復基準。</summary>
    private OcProfile CaptureCurrent(string name)
    {
        var p = new OcProfile { Name = name };
        foreach (var k in _engine.Knobs.Where(k => k.Writable))
            p.Items.Add(new OcProfileItem { Id = k.Id, Name = k.RawName, Value = k.Active });
        return p;
    }

    private sealed record RecoveryWrite(OcKnob Knob, double Value, OcApplyResult Result);
    private sealed record RecoveryOutcome(OcApplyResult Result, IReadOnlyList<RecoveryWrite> Writes);

    /// <summary>Revoke later writes immediately, then await dispatched I/O before restoring the full baseline.</summary>
    internal Task<OcApplyResult> RollbackToStableAsync(string reason, Severity reasonSeverity = Severity.Warning)
        => BeginRecoveryAsync(reason, reasonSeverity, null);

    private Task<OcApplyResult> ExpireWatchdogAsync(long arm)
        => BeginRecoveryAsync("看門狗逾時：未在時限內確認穩定", Severity.Warning, arm);

    private Task<OcApplyResult> BeginRecoveryAsync(string reason, Severity reasonSeverity, long? expectedArm)
    {
        TaskCompletionSource<OcApplyResult> completion;
        ITimer? timer;
        lock (_operationState)
        {
            if (_disposed) return Task.FromResult(OcApplyResult.Fail("服務已結束，未執行回復。"));
            if (expectedArm is long arm)
            {
                if (!_watchdogEnabled || !_watchdogArmed || arm != _watchdogArmGeneration)
                    return Task.FromResult(OcApplyResult.Fail("看門狗期限已解除或取代。"));
                var remaining = WatchdogWindow - _timeProvider.GetElapsedTime(_watchdogStarted);
                if (remaining > TimeSpan.Zero)
                {
                    _watchdogTimer?.Change(remaining, Timeout.InfiniteTimeSpan);
                    return Task.FromResult(OcApplyResult.Fail("尚未到看門狗期限。"));
                }
            }
            if (_recoveryCompletion is not null) return _recoveryCompletion.Task;
            ++_operationGeneration;
            _recoveryCompletion = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            timer = DisarmWatchdogLocked();
        }
        timer?.Dispose();
        PublishWatchdogState();
        RaiseOperationState();
        SetAction(reason + "：已停止後續套用；正在等待已送出的引擎操作完成，再回復穩定基準。", reasonSeverity);
        // Own a snapshot: ConfirmStable is blocked while this recovery is pending.
        var baseline = _rollback is null ? null : new OcProfile
        {
            Name = _rollback.Name,
            Items = _rollback.Items.Select(i => new OcProfileItem { Id = i.Id, Name = i.Name, Value = i.Value }).ToList()
        };
        _ = CompleteRecoveryAsync(reason, reasonSeverity, baseline, completion);
        return completion.Task;
    }

    private async Task CompleteRecoveryAsync(string reason, Severity reasonSeverity, OcProfile? baseline,
        TaskCompletionSource<OcApplyResult> completion)
    {
        RecoveryOutcome outcome;
        try
        {
            using var recovery = HardwareOperationGate.BeginRecoveryOperation();
            // Hold the engine slot across the whole restoration, including individual failures.
            outcome = await RunEngineAsync(() => RestoreStableCore(baseline));
        }
        catch (Exception ex)
        {
            outcome = new(OcApplyResult.Fail("回復發生例外，硬體狀態尚未確認：" + ex.Message), []);
        }
        try
        {
            foreach (var write in outcome.Writes)
            {
                PublishUiNotification(() => write.Knob.ApplyStatus = write.Result.Message);
                PublishUiNotification(() => write.Knob.ApplySeverity = write.Result.Ok ? Severity.Warning : Severity.Critical);
                if (write.Result.Ok) PublishUiNotification(() => write.Knob.Target = write.Value);
            }
            SetAction(reason + "：" + outcome.Result.Message,
                outcome.Result.Ok ? reasonSeverity : Severity.Critical);
            PublishUiNotification(RaisePlanner);
        }
        finally
        {
            lock (_operationState)
            {
                if (ReferenceEquals(_recoveryCompletion, completion)) _recoveryCompletion = null;
            }
            try { RaiseOperationState(); }
            finally { completion.TrySetResult(outcome.Result); }
        }
    }

    private RecoveryOutcome RestoreStableCore(OcProfile? baseline)
    {
        if (baseline is null || baseline.Items.Count == 0)
            return new(_engine.RestoreDefaults(), []);
        var writes = new List<RecoveryWrite>();
        foreach (var item in baseline.Items)
        {
            var k = _engine.Knobs.FirstOrDefault(x => x.Id == item.Id);
            if (k is null || !k.Writable) continue;
            OcApplyResult r;
            try { r = _engine.Apply(k, item.Value); }
            catch (Exception ex) { r = OcApplyResult.Fail("回復此項發生例外：" + ex.Message); }
            writes.Add(new(k, item.Value, r));
        }
        int ok = writes.Count(w => w.Result.Ok), total = baseline.Items.Count;
        bool allOk = total > 0 && ok == total;
        string message = total == 0 ? "沒有可回復的穩定基準項目，硬體狀態尚未確認。"
            : $"穩定基準回復：{ok}/{total} 項由引擎回報成功。"
                + (allOk ? "各項讀回限制見調節項狀態。" : "回復未完成，請檢視各項失敗狀態。");
        return new(new OcApplyResult(allOk, message, null), writes);
    }

    private static bool IsRisky(OcKnob k) =>
        !k.RequiresReboot && k.Kind is OcKnobKind.Voltage or OcKnobKind.VoltageOffset
            or OcKnobKind.MemoryVoltage or OcKnobKind.CoreRatio or OcKnobKind.Bclk;

    private void ArmWatchdogIfNeeded(OcKnob k) { if (_watchdogEnabled && IsRisky(k)) ArmWatchdog(); }
    private void ArmWatchdog()
    {
        ITimer? previous;
        lock (_operationState)
        {
            if (_disposed || _recoveryCompletion is not null || !_watchdogEnabled) return;
            previous = DisarmWatchdogLocked();
            long arm = ++_watchdogArmGeneration;
            _watchdogStarted = _timeProvider.GetTimestamp();
            _watchdogArmed = true;
            _watchdogLeft = 30;
            _watchdogTimer = CreateWatchdogTimer(arm);
            _watchdogTimer.Change(WatchdogWindow, Timeout.InfiniteTimeSpan);
        }
        previous?.Dispose();
        PublishWatchdogState();
    }

    private ITimer CreateWatchdogTimer(long arm)
    {
        // Arming can occur inside a high-risk write scope. Do not let a long-lived
        // timer inherit that AsyncLocal permit; recovery obtains its own explicit scope.
        if (ExecutionContext.IsFlowSuppressed())
            return _timeProvider.CreateTimer(state => { _ = ExpireWatchdogAsync(arm); },
                null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        using (ExecutionContext.SuppressFlow())
            return _timeProvider.CreateTimer(state => { _ = ExpireWatchdogAsync(arm); },
                null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // Caller holds _operationState. No UI callbacks or hardware work under this lock.
    private ITimer? DisarmWatchdogLocked()
    {
        var timer = _watchdogTimer;
        _watchdogTimer = null;
        ++_watchdogArmGeneration;
        _watchdogArmed = false;
        _watchdogLeft = 0;
        return timer;
    }

    private void DisarmWatchdog()
    {
        ITimer? timer;
        lock (_operationState) timer = DisarmWatchdogLocked();
        timer?.Dispose();
        PublishWatchdogState();
    }

    private void PublishWatchdogState()
    {
        PublishUiNotification(() => OnPropertyChanged(nameof(WatchdogArmed)));
        PublishUiNotification(() => OnPropertyChanged(nameof(WatchdogSecondsLeft)));
        PublishUiNotification(() => OnPropertyChanged(nameof(WatchdogStatusText)));
    }

    private void RefreshProfiles()
    {
        Profiles.Clear(); _profilePaths.Clear();
        foreach (var (name, path) in OcSettings.ListProfiles())
        {
            Profiles.Add(name);
            _profilePaths.Add(path);
        }
    }

    private void SetAction(string msg, Severity sev)
    {
        PublishUiNotification(() => ActionStatus = msg);
        PublishUiNotification(() => ActionSeverity = sev);
    }

    private void RaisePlanner()
    {
        OnPropertyChanged(nameof(TargetFrequencyText));
        OnPropertyChanged(nameof(PcieWarningVisible));
        OnPropertyChanged(nameof(PcieWarningText));
    }

    private void RaiseEngineStatus()
    {
        foreach (var n in new[] { nameof(EngineReady), nameof(CanWrite), nameof(EngineName),
                                  nameof(EngineStatusText), nameof(EngineStatusSeverity), nameof(Initializing) })
            OnPropertyChanged(n);
    }

    private async Task DisposeEngineAsync()
    {
        try
        {
            await RunEngineAsync(() =>
            {
                try { _engine.Dispose(); } catch { }
                try { Vrm?.Dispose(); } catch { }
                return true;
            });
        }
        catch { }
    }

    public void Dispose()
    {
        if (CoreRatioKnob is not null) CoreRatioKnob.PropertyChanged -= OnPlannerKnobChanged;
        if (BclkKnob is not null) BclkKnob.PropertyChanged -= OnPlannerKnobChanged;
        ITimer? timer;
        lock (_operationState)
        {
            if (_disposed) return;
            _disposed = true;
            ++_operationGeneration;
            timer = DisarmWatchdogLocked();
        }
        timer?.Dispose();
        PublishWatchdogState();
        _ = DisposeEngineAsync();
        RaiseOperationState();

    }
}
