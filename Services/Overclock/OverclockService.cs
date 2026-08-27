using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly OcSettings _settings = OcSettings.Load();
    private readonly StressTestService _stress = new();

    private OcProfile? _rollback;         // 最後確認穩定的設定（看門狗逾時 / 燒機過熱時回復的目標）
    private bool _stressGuardTripped;
    private bool _stressTempMissingWarned;   // 燒機時溫度不可讀的提示僅顯示一次
    private bool _hasLastStable;

    public OverclockService()
    {
        OscilloVolt = new MetricHistory(140, "V", null, "0.000");
        OscilloCurrent = new MetricHistory(140, "A", null, "0");
        WatchdogEnabled = _settings.WatchdogEnabled;
        BootRestore = _settings.BootRestore;
    }

    // ── 燒機（本模組專用，獨立於效能分頁的烤機，避免互相干擾）────────────────
    public StressTestService Stress => _stress;

    // ── 引擎狀態 ───────────────────────────────────────────────────────────
    private bool _initializing;
    public bool Initializing { get => _initializing; private set => SetProperty(ref _initializing, value); }

    public bool EngineReady => _engine.Status == OcEngineStatus.Ready;
    public bool CanWrite => EngineReady && _engine.Knobs.Any(k => k.Writable);
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

    // ── 體質估算（矽晶品質；改為「按鈕觸發、取樣峰值」而非每秒亂跳）─────────────
    private int _siScore;
    public int SiliconScore { get => _siScore; private set { if (SetProperty(ref _siScore, value)) OnPropertyChanged(nameof(SiliconScoreText)); } }
    public string SiliconScoreText => _siScore > 0 ? $"{_siScore} / 100" : "—";
    private string _siVerdict = "尚未測試";
    public string SiliconVerdict { get => _siVerdict; private set => SetProperty(ref _siVerdict, value); }
    private Severity _siSev = Severity.Neutral;
    public Severity SiliconSeverity { get => _siSev; private set => SetProperty(ref _siSev, value); }
    private string _siDetail = "按「重新測試」開始取樣約 5 秒；請於高負載（或燒機）時測，閒置時頻率與電壓偏低會嚴重失真。";
    public string SiliconDetail { get => _siDetail; private set => SetProperty(ref _siDetail, value); }
    // 取樣狀態：測試期間於 Tick 累計「最高頻率及其當下電壓」作為 VF 品質點
    private bool _siTesting;
    public bool SiliconTesting { get => _siTesting; private set { if (SetProperty(ref _siTesting, value)) OnPropertyChanged(nameof(SiliconCanTest)); } }
    public bool SiliconCanTest => !_siTesting;
    private double _siPeakClock, _siPeakVcore;

    // ── PCIe 瓶頸 / 提醒 ───────────────────────────────────────────────────
    public bool PcieWarningVisible => BclkKnob is not null && Math.Abs(BclkKnob.Target - BclkKnob.Default) > 0.5;
    public string PcieWarningText =>
        "提高外頻 BCLK 會連動拉高 PCIe / DMI 匯流排時脈，可能導致 NVMe、顯示卡或 USB 不穩。"
        + "請於 BIOS 將 PCIe/DMI 鎖定在 100 MHz，並以小幅（每次 +1 MHz）測試。";

    // ── IHS 核心熱區圖 ─────────────────────────────────────────────────────
    public ObservableCollection<CoreRow>? Cores { get; private set; }

    // ── LLC（負載線校準，XTU 無法寫入 → 僅 BIOS 教學）──────────────────────
    public IReadOnlyList<string> LlcLevels { get; } = new[]
    {
        "Level 1（最小補償・壓降最大）", "Level 2", "Level 3",
        "Level 4（多數日常超頻建議）", "Level 5", "Level 6",
        "Level 7（接近零壓降）", "Level 8（最大補償・過衝風險最高）",
    };
    private int _llcIndex = 3;   // 預設 Level 4
    public int LlcIndex { get => _llcIndex; set { if (SetProperty(ref _llcIndex, value)) OnPropertyChanged(nameof(LlcAnnotation)); } }
    public string LlcAnnotation
    {
        get
        {
            string body = (_llcIndex + 1) switch
            {
                <= 2 => "補償最弱：負載時壓降（Vdroop）最大、過衝最小。最保守，適合追求安全裕度。",
                <= 4 => "輕～中度補償：壓降與過衝取得平衡，日常超頻常用區間。",
                <= 6 => "較強補償：壓降小，但瞬態過衝（overshoot）與漣波（ripple）上升。",
                _ => "最強補償：幾乎零壓降，但過衝與漣波風險最高，長期高負載需留意 VRM 與矽晶壽命。",
            };
            return "※ Intel XTU 無法寫入 LLC，此處僅供 BIOS 設定參考。\n" + body;
        }
    }

    // ── 看門狗（三層誠實方案）──────────────────────────────────────────────
    private bool _watchdogEnabled;
    public bool WatchdogEnabled
    {
        get => _watchdogEnabled;
        set { if (SetProperty(ref _watchdogEnabled, value)) { _settings.WatchdogEnabled = value; _settings.Save(); if (!value) DisarmWatchdog(); OnPropertyChanged(nameof(WatchdogStatusText)); } }
    }
    private bool _watchdogArmed;
    public bool WatchdogArmed { get => _watchdogArmed; private set { if (SetProperty(ref _watchdogArmed, value)) OnPropertyChanged(nameof(WatchdogStatusText)); } }
    private int _watchdogLeft;
    public int WatchdogSecondsLeft { get => _watchdogLeft; private set { if (SetProperty(ref _watchdogLeft, value)) OnPropertyChanged(nameof(WatchdogStatusText)); } }

    public string WatchdogStatusText =>
        !_watchdogEnabled ? "看門狗未啟用。啟用後，套用高風險項目將開始 30 秒倒數，須按「確認穩定」解除，否則自動回復。"
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
        set { if (SetProperty(ref _bootRestore, value)) { _settings.BootRestore = value; _settings.Save(); try { _engine.SetBootRestore(value); } catch { } } }
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

    // 寫入進行中旗標：套用走阻塞式 IPC，改為背景執行緒進行；此旗標防止重入/併發寫入，
    // 並供 UI 停用按鈕。所有 Apply* 進入時檢查，離開時（finally）復位。
    private bool _isApplying;
    public bool IsApplying { get => _isApplying; private set { if (SetProperty(ref _isApplying, value)) OnPropertyChanged(nameof(CanApply)); } }
    public bool CanApply => !_isApplying;

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

    public async Task InitializeAsync()
    {
        Initializing = true;
        RaiseEngineStatus();

        var engine = await Task.Run<IOcEngine>(() =>
        {
            var xtu = new XtuOcEngine();
            try { xtu.Initialize(); } catch { }
            if (xtu.Status == OcEngineStatus.Ready) return xtu;

            var status = xtu.Status == OcEngineStatus.NotInitialized ? OcEngineStatus.Failed : xtu.Status;
            string msg = xtu.StatusMessage;
            xtu.Dispose();
            return new NullOcEngine(status, msg);
        });

        _engine = engine;
        Categorize();

        // 套用持久偏好到硬體（開機還原）
        try { _engine.SetBootRestore(_bootRestore); } catch { }

        // 以目前（開機 / 現行）硬體值作為第一份「穩定基準」，供看門狗逾時回復
        _rollback = CaptureCurrent("初始基準");

        // Speed Optimizer 現況
        try { SpeedOptimizerOn = _engine.SpeedOptimizerState > 0; } catch { }
        SpeedOptimizerStatusText = SpeedOptimizerSupported
            ? (SpeedOptimizerOn ? "目前為開啟。" : "支援，目前為關閉。")
            : "此平台不支援。";

        RefreshProfiles();
        _hasLastStable = OcSettings.LoadLastStable() is not null;

        if (EngineReady)
            SetAction($"已連接超頻引擎，列出 {_engine.Knobs.Count} 個可調項（可寫 {_engine.Knobs.Count(k => k.Writable)} 個）。"
                      + (_hasLastStable ? "偵測到『最後穩定設定』，可於下方一鍵回復。" : ""), Severity.Good);
        else
            SetAction(_engine.StatusMessage, EngineStatusSeverity);

        Initializing = false;
        RaiseEngineStatus();
        RaisePlanner();
        OnPropertyChanged(nameof(HasLastStable));
        OnPropertyChanged(nameof(SpeedOptimizerSupported));
        OnPropertyChanged(nameof(HwWatchdogText));
        OnPropertyChanged(nameof(ProcessorFamilyText));
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
        bool needVcore = live?.CpuVoltage is null or <= 0;
        bool needVrm = live?.VrmTempC is null;
        double? engineVcore = null, engineVrm = null;
        if ((needVcore || needVrm) && EngineReady)
        {
            try
            {
                await Task.Run(() =>
                {
                    if (needVcore) engineVcore = _engine.ReadCoreVoltage();
                    if (needVrm) engineVrm = _engine.ReadMonitor("VR ", "VRM", "MOS", "VR MOS", "VCCIN");
                });
            }
            catch { /* 單次遙測讀取失敗不影響後續 */ }
        }
        TickCore(live, engineVcore, engineVrm);
    }

    private void TickCore(SensorService? live, double? engineVcore, double? engineVrm)
    {
        if (Cores is null && live?.CpuCores is not null) { Cores = live.CpuCores; OnPropertyChanged(nameof(Cores)); }

        // 電壓：LHM 優先 → XTU（背景預抓）→ 旋鈕現值
        double? vcore = live?.CpuVoltage;
        if (vcore is null or <= 0) vcore = engineVcore;
        if (vcore is null or <= 0 && VcoreKnob is not null) vcore = VcoreKnob.Active;

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
            VoltageValueText = "—";
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

        // 體質取樣：僅在「重新測試」進行中累計最高頻率及其當下電壓（VF 品質點），不再每秒亂跳
        if (_siTesting && vcore is double sv && sv > 0 && clock > _siPeakClock)
        {
            _siPeakClock = clock;
            _siPeakVcore = sv;
        }

        // 燒機：即時餵入 + 過熱保護（>100°C 自動暫停並回復）
        if (_stress.IsRunning)
        {
            _stress.Sample(temp, clock, live?.CpuLoad ?? 0);
            if (temp is double ht && ht >= 100 && !_stressGuardTripped)
            {
                _stressGuardTripped = true;
                _stress.Cancel();
                var r = RollbackToStable();
                SetAction($"⚠ 燒機過程偵測到 {ht:0}°C（≥100°C），已自動停止並回復：{r}", Severity.Critical);
            }
            // 誠實告知：讀不到 CPU 溫度時過熱保護無法運作，套用超頻後燒機風險由使用者承擔（僅提示一次）
            else if (temp is null && !_stressTempMissingWarned)
            {
                _stressTempMissingWarned = true;
                SetAction("⚠ 目前讀不到 CPU 溫度，燒機過熱自動保護無法運作，請自行留意散熱。", Severity.Serious);
            }
        }
        else { _stressGuardTripped = false; _stressTempMissingWarned = false; }

        // 看門狗倒數（第一層：軟體自動回復）
        if (_watchdogArmed)
        {
            if (WatchdogSecondsLeft > 0) WatchdogSecondsLeft--;
            if (WatchdogSecondsLeft <= 0)
            {
                var r = RollbackToStable();
                DisarmWatchdog();
                SetAction($"看門狗逾時：未在時限內確認穩定，已自動回復至最後穩定設定。{r}", Severity.Warning);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 體質（矽晶品質）重新測試：按鈕觸發、取樣約 5 秒峰值後才估算一次
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 重新測試體質。取樣期間（約 5 秒）由 <see cref="Tick"/> 累計「達到的最高頻率及其當下電壓」，
    /// 收樣後才以該 VF 品質點估算一次，避免閒置低頻低壓造成的每秒亂跳與虛高分數。
    /// 請於高負載（建議搭配燒機）時測，否則取樣到的頻率／電壓偏低會嚴重失真。
    /// </summary>
    public async Task RetestSilicon()
    {
        if (_siTesting) return;                 // 防止重入：測試進行中忽略再次點擊
        SiliconTesting = true;
        _siPeakClock = 0;
        _siPeakVcore = 0;
        SiliconVerdict = "測試中…";
        SiliconSeverity = Severity.Neutral;
        SiliconScore = 0;
        SiliconDetail = "取樣中（約 5 秒）：正記錄本次達到的最高頻率及其當下電壓，請保持高負載。";
        try
        {
            await Task.Delay(5000);             // 期間 Tick 於 UI 執行緒累計 _siPeakClock／_siPeakVcore
            if (_siPeakClock > 0 && _siPeakVcore > 0)
            {
                var si = SiliconEstimate.Compute(_siPeakVcore, _siPeakClock);
                SiliconScore = si.Score;
                SiliconVerdict = si.Verdict;
                SiliconSeverity = si.Severity;
                SiliconDetail = si.Detail
                    + $"（取樣峰值：{_siPeakClock / 1000.0:0.00} GHz / {_siPeakVcore:0.000} V）";
            }
            else
            {
                SiliconVerdict = "取樣失敗";
                SiliconSeverity = Severity.Warning;
                SiliconDetail = "未取得有效的頻率／電壓讀值；請確認正在高負載，並確保能讀到 Vcore 後再試。";
            }
        }
        finally
        {
            SiliconTesting = false;             // 無論成敗都解除測試狀態，重新啟用按鈕
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 寫入操作
    // ═══════════════════════════════════════════════════════════════════════

    // 單一旋鈕的實際寫入：阻塞式 IPC 移至背景執行緒，避免凍結 UI（看門狗倒數得以持續）。
    // 不處理 IsApplying 防重入（由各公開方法統一管理，避免巢狀誤觸）。寫入後回 UI 執行緒套用狀態。
    private async Task<OcApplyResult> WriteKnobAsync(OcKnob knob, double value)
    {
        var r = await Task.Run(() => _engine.Apply(knob, value));
        knob.ApplyStatus = r.Message;
        knob.ApplySeverity = r.Ok ? (knob.RequiresReboot ? Severity.Warning : Severity.Good) : Severity.Critical;
        return r;
    }

    public async Task ApplyKnob(OcKnob knob)
    {
        if (IsApplying) return;
        IsApplying = true;
        try
        {
            SetAction($"正在套用「{knob.Label}」…", Severity.Neutral);
            var r = await WriteKnobAsync(knob, knob.Target);
            SetAction(r.Message, r.Ok ? (knob.RequiresReboot ? Severity.Warning : Severity.Good) : Severity.Critical);
            if (r.Ok) ArmWatchdogIfNeeded(knob);
            RaisePlanner();
        }
        finally { IsApplying = false; }
    }

    // 目標時脈規劃：一次套用倍頻 + 外頻（於單一防重入區間內依序寫入）。
    public async Task ApplyPlannerAsync()
    {
        if (IsApplying) return;
        var targets = new List<OcKnob>();
        if (CoreRatioKnob is { } ratio) targets.Add(ratio);
        if (BclkKnob is { } bclk) targets.Add(bclk);
        if (targets.Count == 0) { SetAction("此平台無可調整的倍頻／外頻。", Severity.Neutral); return; }

        IsApplying = true;
        try
        {
            SetAction("正在套用目標時脈…", Severity.Neutral);
            int ok = 0; bool armed = false;
            foreach (var k in targets)
            {
                var r = await WriteKnobAsync(k, k.Target);
                if (r.Ok) { ok++; if (IsRisky(k)) armed = true; }
            }
            if (armed && _watchdogEnabled) ArmWatchdog();
            SetAction($"目標時脈：已套用 {ok} / {targets.Count} 項。" + (ok < targets.Count ? "部分項目未如預期生效，請檢視各項狀態。" : ""),
                      ok == targets.Count ? Severity.Good : Severity.Warning);
            RaisePlanner();
        }
        finally { IsApplying = false; }
    }

    // Vcore 卡「套用電壓」：聚合模式（此平台僅逐核心覆寫）時，把滑桿目標值一次寫入全部核心；
    // 若 VcoreKnob 為真實的全域旋鈕（例如 K 系列有獨立核心電壓覆寫），則走一般單一寫入。
    public async Task ApplyVcore()
    {
        if (VcoreKnob is null || IsApplying) return;
        if (_vcoreFanout.Count == 0) { await ApplyKnob(VcoreKnob); return; }

        double target = VcoreKnob.Target;
        foreach (var k in _vcoreFanout) k.Target = target;   // UI 執行緒：先同步各核心目標值

        IsApplying = true;
        try
        {
            SetAction($"正在對 {_vcoreFanout.Count} 個核心套用 {target:0.000} V…", Severity.Neutral);
            int ok = 0; bool risky = false;
            foreach (var k in _vcoreFanout)
            {
                var r = await WriteKnobAsync(k, target);
                if (r.Ok) { ok++; if (IsRisky(k)) risky = true; }
            }
            VcoreKnob.Active = target;   // 樂觀回讀；下一次 Tick 會以實測電壓覆蓋
            bool allok = ok == _vcoreFanout.Count;
            VcoreKnob.ApplyStatus = allok
                ? $"已對全部 {_vcoreFanout.Count} 個核心套用 {target:0.000} V"
                : $"僅 {ok}/{_vcoreFanout.Count} 個核心套用成功，請檢視「每核心獨立調節」各項狀態。";
            VcoreKnob.ApplySeverity = allok ? Severity.Good : Severity.Warning;
            if (risky && _watchdogEnabled) ArmWatchdog();
            SetAction(VcoreKnob.ApplyStatus, VcoreKnob.ApplySeverity);
            RaisePlanner();
        }
        finally { IsApplying = false; }
    }

    public async Task ApplyAll()
    {
        if (IsApplying) return;
        var dirty = _engine.Knobs.Where(k => k.Writable && k.IsDirty).ToList();
        if (dirty.Count == 0) { SetAction("沒有待套用的變更。", Severity.Neutral); return; }

        IsApplying = true;
        try
        {
            SetAction($"正在套用 {dirty.Count} 項變更…", Severity.Neutral);
            int ok = 0; bool armed = false;
            foreach (var k in dirty)
            {
                var r = await WriteKnobAsync(k, k.Target);
                if (r.Ok) { ok++; if (IsRisky(k)) armed = true; }
            }
            if (armed && _watchdogEnabled) ArmWatchdog();
            SetAction($"已套用 {ok} / {dirty.Count} 項變更。" + (ok < dirty.Count ? "部分項目未如預期生效，請檢視各項狀態。" : ""),
                      ok == dirty.Count ? Severity.Good : Severity.Warning);
            RaisePlanner();
        }
        finally { IsApplying = false; }
    }

    public async Task DiscardAll()
    {
        if (IsApplying) return;
        IsApplying = true;
        try
        {
            bool ok = await Task.Run(() => _engine.Discard());
            foreach (var k in _engine.Knobs) k.ResetTargetToActive();
            SetAction(ok ? "已取消所有未套用的變更，並重新讀取硬體現值。" : "取消變更失敗（引擎不可用）。", ok ? Severity.Good : Severity.Critical);
            RaisePlanner();
        }
        finally { IsApplying = false; }
    }

    public async Task RestoreDefaults()
    {
        if (IsApplying) return;
        IsApplying = true;
        try
        {
            SetAction("正在還原預設值…", Severity.Neutral);
            var r = await Task.Run(() => _engine.RestoreDefaults());
            SetAction(r.Message, r.Ok ? Severity.Good : Severity.Critical);
            DisarmWatchdog();
            RaisePlanner();
        }
        finally { IsApplying = false; }
    }

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
        if (_selProfile < 0 || _selProfile >= _profilePaths.Count) { SetAction("請先選擇一個設定檔。", Severity.Neutral); return; }
        var p = OcSettings.LoadProfile(_profilePaths[_selProfile]);
        if (p is null) { SetAction("無法讀取所選設定檔。", Severity.Critical); return; }
        await ApplyProfile(p, $"設定檔「{p.Name}」");
    }

    public async Task ImportProfileFrom(string path)
    {
        var p = OcSettings.LoadProfile(path);
        if (p is null) { SetAction("無法讀取設定檔：" + path, Severity.Critical); return; }
        await ApplyProfile(p, $"設定檔「{p.Name}」");
    }

    public async Task ApplyLastStable()
    {
        var p = OcSettings.LoadLastStable();
        if (p is null) { SetAction("尚無『最後穩定設定』可回復。", Severity.Neutral); return; }
        await ApplyProfile(p, "最後穩定設定");
    }

    /// <summary>把目前套用的目標值標記為「穩定」：存為 last-stable、更新看門狗回復基準、並解除看門狗。</summary>
    public void ConfirmStable()
    {
        var p = CaptureCurrent("最後穩定設定");
        _rollback = p;
        OcSettings.SaveLastStable(p);
        HasLastStable = true;
        DisarmWatchdog();
        SetAction("已將目前設定標記為穩定並存檔；看門狗已解除。", Severity.Good);
    }

    public async Task SetSpeedOptimizer(bool on, bool extreme)
    {
        if (IsApplying) return;
        IsApplying = true;
        try
        {
            SetAction(on ? "正在啟用 Intel Speed Optimizer…" : "正在關閉 Intel Speed Optimizer…", Severity.Neutral);
            var r = await Task.Run(() => _engine.SetSpeedOptimizer(on, extreme));
            if (r.Ok)
            {
                try { SpeedOptimizerOn = _engine.SpeedOptimizerState > 0; } catch { SpeedOptimizerOn = on; }
                SpeedOptimizerStatusText = SpeedOptimizerOn ? "目前為開啟。" : "支援，目前為關閉。";
            }
            SetAction(r.Message, r.Ok ? Severity.Good : Severity.Critical);
        }
        finally { IsApplying = false; }
    }

    public void StartStress() { _stressGuardTripped = false; _stress.Start(); SetAction("已開始簡易燒機（過熱 ≥100°C 將自動停止並回復）。", Severity.Warning); }
    public void StopStress() { _stress.Cancel(); SetAction("已請求停止燒機。", Severity.Neutral); }

    // ═══════════════════════════════════════════════════════════════════════
    // 內部工具
    // ═══════════════════════════════════════════════════════════════════════

    private async Task ApplyProfile(OcProfile p, string label)
    {
        if (IsApplying) return;
        IsApplying = true;
        try
        {
            SetAction($"正在套用{label}…", Severity.Neutral);
            int ok = 0, total = 0; bool armed = false;
            foreach (var item in p.Items)
            {
                var k = _engine.Knobs.FirstOrDefault(x => x.Id == item.Id);
                if (k is null || !k.Writable) continue;
                total++;
                k.Target = item.Value;
                var r = await WriteKnobAsync(k, item.Value);
                if (r.Ok) { ok++; if (IsRisky(k)) armed = true; }
            }
            if (armed && _watchdogEnabled) ArmWatchdog();
            SetAction($"已套用{label}：{ok} / {total} 項成功。", ok == total && total > 0 ? Severity.Good : Severity.Warning);
            RaisePlanner();
        }
        finally { IsApplying = false; }
    }

    /// <summary>擷取目前硬體現值（Active）為一份設定檔，作為回復基準。</summary>
    private OcProfile CaptureCurrent(string name)
    {
        var p = new OcProfile { Name = name };
        foreach (var k in _engine.Knobs.Where(k => k.Writable))
            p.Items.Add(new OcProfileItem { Id = k.Id, Name = k.RawName, Value = k.Active });
        return p;
    }

    /// <summary>回復至最後穩定基準（看門狗逾時 / 燒機過熱時呼叫）。回傳簡短結果供訊息串接。</summary>
    private string RollbackToStable()
    {
        if (_rollback is null || _rollback.Items.Count == 0)
        {
            var r = _engine.RestoreDefaults();
            return r.Ok ? "（已還原為預設值）" : "（回復失敗：" + r.Message + "）";
        }
        int ok = 0, total = 0;
        foreach (var item in _rollback.Items)
        {
            var k = _engine.Knobs.FirstOrDefault(x => x.Id == item.Id);
            if (k is null || !k.Writable) continue;
            total++;
            if (_engine.Apply(k, item.Value).Ok) { ok++; k.Target = item.Value; }
        }
        return $"（已回復 {ok}/{total} 項至穩定基準）";
    }

    private static bool IsRisky(OcKnob k) =>
        !k.RequiresReboot && k.Kind is OcKnobKind.Voltage or OcKnobKind.MemoryVoltage or OcKnobKind.CoreRatio or OcKnobKind.Bclk;

    private void ArmWatchdogIfNeeded(OcKnob k) { if (_watchdogEnabled && IsRisky(k)) ArmWatchdog(); }
    private void ArmWatchdog() { WatchdogArmed = true; WatchdogSecondsLeft = 30; }
    private void DisarmWatchdog() { WatchdogArmed = false; WatchdogSecondsLeft = 0; }

    private void RefreshProfiles()
    {
        Profiles.Clear(); _profilePaths.Clear();
        foreach (var (name, path) in OcSettings.ListProfiles())
        {
            Profiles.Add(name);
            _profilePaths.Add(path);
        }
    }

    private void SetAction(string msg, Severity sev) { ActionStatus = msg; ActionSeverity = sev; }

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

    public void Dispose()
    {
        if (CoreRatioKnob is not null) CoreRatioKnob.PropertyChanged -= OnPlannerKnobChanged;
        if (BclkKnob is not null) BclkKnob.PropertyChanged -= OnPlannerKnobChanged;
        try { _engine.Dispose(); } catch { }
    }
}
