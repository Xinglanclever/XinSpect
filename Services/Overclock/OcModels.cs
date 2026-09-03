using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace XinSpect;

// ───────────────────────────────────────────────────────────────────────────
// 超頻模組領域模型：旋鈕（可調項）、電壓危險分級、體質估算、設定檔。
// 全部資料皆來自硬體實測（透過 Intel XTU SDK 反射列舉的真實可調控制項），
// 不做任何模擬；無法讀到的項目一律誠實標記為不可用，而非填假值。
// ───────────────────────────────────────────────────────────────────────────

/// <summary>可調項的種類，決定步進、格式與歸屬的區塊。</summary>
public enum OcKnobKind
{
    Voltage,        // 絕對電壓（Vcore / 快取電壓 / 每核心電壓覆寫）
    VoltageOffset,  // 電壓偏移（快取電壓偏移、每核心電壓偏移）——可能以 mV 為原始單位
    CoreRatio,      // 核心倍頻（全核心 / 睿頻表 / 每核心倍頻上限）
    CacheRatio,     // 快取 / Uncore 倍頻
    Bclk,           // 外頻（基準時脈）
    PowerLimit,     // 功耗牆 PL1 / PL2
    Current,        // 電流上限
    MemoryVoltage,  // 記憶體電壓 VDDQ / VDD2 / SA
    MemoryRatio,    // 記憶體倍頻
    Offset,         // AVX 降頻等「倍頻」偏移量（整數）
    Other,
}

/// <summary>UI 分區。快取自成一區並緊接主頻規劃之下。</summary>
public enum OcSection { Core, Cache, Power, Memory, Bus, Advanced }

/// <summary>套用結果（回讀 ActiveValue 作為成功與否的證據，與列舉無關）。</summary>
public sealed record OcApplyResult(bool Ok, string Message, double? NewActive)
{
    public static OcApplyResult Fail(string msg) => new(false, msg, null);
    public static OcApplyResult Success(string msg, double? active) => new(true, msg, active);
}

/// <summary>
/// 單一可調硬體控制項。由引擎自 XTU SDK 反射列舉的 ClientTuningControl 建構，
/// 之後 Target 由滑桿雙向繫結；按「套用」時交給引擎以該控制項自身的 Id 執行 Tune()。
/// </summary>
public sealed class OcKnob : ObservableObject
{
    public OcKnob(uint id, string rawName, string category, string unit, OcKnobKind kind,
                  double min, double max, double @default, double boot, double active,
                  bool realTime, bool requiresReboot, bool readOnly, bool enabled)
    {
        Id = id;
        RawName = rawName;
        Category = category;
        Unit = unit;
        Kind = kind;
        Min = min;
        Max = max;
        Default = @default;
        Boot = boot;
        _active = active;
        _target = active;
        RealTime = realTime;
        RequiresReboot = requiresReboot;
        ReadOnly = readOnly;
        Enabled = enabled;
    }

    public uint Id { get; }
    public string RawName { get; }
    public string Category { get; }
    public string Unit { get; }
    public OcKnobKind Kind { get; }
    public double Min { get; }
    public double Max { get; }
    public double Default { get; }
    public double Boot { get; }
    public bool RealTime { get; }
    public bool RequiresReboot { get; }
    public bool ReadOnly { get; }
    public bool Enabled { get; }

    /// <summary>是否真的可寫（啟用、非唯讀、且區間有效）。決定滑桿是否可拖動。</summary>
    public bool Writable => Enabled && !ReadOnly && Max > Min;

    /// <summary>此項在本平台無法操作（唯讀 / 停用 / 區間無效）→ UI 明白標注失效或暫時棄用。</summary>
    public bool Unavailable => !Writable;
    /// <summary>SDK 回報停用（Enabled=false）：功能存在但本平台不開放，視為暫時棄用。</summary>
    public bool Deprecated => !Enabled;
    /// <summary>UI 角落狀態徽章文字（空字串代表正常可調）。</summary>
    public string StatusBadge =>
        !Enabled ? "停用"
        : Max <= Min ? "失效"
        : ReadOnly ? "唯讀"
        : "";

    /// <summary>整數倍種類（XTU 僅支援整數倍頻，Target 一律對齊整數）。</summary>
    public bool IsRatio => Kind is OcKnobKind.CoreRatio or OcKnobKind.CacheRatio or OcKnobKind.MemoryRatio;

    /// <summary>原始值以毫伏（mV）表示：如快取電壓偏移 −1000..999。UI 一律換算為 V 顯示（÷1000）。</summary>
    public bool IsMilliVolt => Kind == OcKnobKind.VoltageOffset
        && Unit != null && Unit.Trim().Equals("mV", StringComparison.OrdinalIgnoreCase) && Max > 5;
    private double VScale => IsMilliVolt ? 1000.0 : 1.0;

    public OcSection Section => OcNaming.SectionOf(RawName, Category, Kind);

    /// <summary>合理步進（滑桿 SmallChange / 對齊用；整數倍種類固定為 1）。</summary>
    public double Step => Kind switch
    {
        OcKnobKind.Voltage or OcKnobKind.MemoryVoltage => 0.005,
        OcKnobKind.VoltageOffset => IsMilliVolt ? 5 : 0.005,   // 5 mV ≒ 0.005 V
        OcKnobKind.CoreRatio or OcKnobKind.CacheRatio or OcKnobKind.MemoryRatio or OcKnobKind.Offset => 1,
        OcKnobKind.Bclk => 0.1,
        OcKnobKind.PowerLimit or OcKnobKind.Current => 1,
        _ => Math.Max((Max - Min) / 100.0, 0.001),
    };

    /// <summary>整數倍種類的滑桿對齊間隔（TickFrequency）；非整數種類回 0 表不對齊。</summary>
    public double TickFrequency => IsRatio ? 1 : 0;
    public bool SnapToTick => IsRatio;

    // ── 微調鈕（每滑桿下方三段：小 / 中 / 大）──────────────────────────────
    //   電壓類：±0.01 / ±0.05 / ±0.1 V（依使用者規格）；倍頻類：±1 / ±5 / ±10（整數）。
    private double[] Mags => Kind switch
    {
        OcKnobKind.Voltage or OcKnobKind.MemoryVoltage => new[] { 0.01, 0.05, 0.1 },
        OcKnobKind.VoltageOffset => IsMilliVolt ? new[] { 10.0, 50.0, 100.0 } : new[] { 0.01, 0.05, 0.1 },
        OcKnobKind.CoreRatio or OcKnobKind.CacheRatio or OcKnobKind.MemoryRatio or OcKnobKind.Offset => new[] { 1.0, 5.0, 10.0 },
        OcKnobKind.PowerLimit or OcKnobKind.Current => new[] { 1.0, 5.0, 10.0 },
        OcKnobKind.Bclk => new[] { 0.1, 0.5, 1.0 },
        _ => new[] { Step, Step * 5, Step * 10 },
    };
    public double Nudge1 => Mags[0];
    public double Nudge2 => Mags[1];
    public double Nudge3 => Mags[2];
    // 微調鈕文字（帶符號）：由大到小的減，再由小到大的加。
    public string NudgeMinus3Text => "－" + NudgeLabel(2);
    public string NudgeMinus2Text => "－" + NudgeLabel(1);
    public string NudgeMinus1Text => "－" + NudgeLabel(0);
    public string NudgePlus1Text => "＋" + NudgeLabel(0);
    public string NudgePlus2Text => "＋" + NudgeLabel(1);
    public string NudgePlus3Text => "＋" + NudgeLabel(2);
    private string NudgeLabel(int i)
    {
        double m = Mags[i];
        return Kind switch
        {
            OcKnobKind.Voltage or OcKnobKind.MemoryVoltage or OcKnobKind.VoltageOffset
                => $"{(m / VScale).ToString("0.###", CultureInfo.InvariantCulture)} V",
            OcKnobKind.CoreRatio or OcKnobKind.CacheRatio or OcKnobKind.MemoryRatio or OcKnobKind.Offset
                => $"{m.ToString("0", CultureInfo.InvariantCulture)}×",
            OcKnobKind.PowerLimit => $"{m:0} W",
            OcKnobKind.Current => $"{m:0} A",
            OcKnobKind.Bclk => $"{m.ToString("0.#", CultureInfo.InvariantCulture)} MHz",
            _ => m.ToString("0.###", CultureInfo.InvariantCulture),
        };
    }
    /// <summary>依帶符號索引微調：±1→小、±2→中、±3→大。Target 設值器會夾限並對齊整數。</summary>
    public void NudgeByIndex(int signedIndex)
    {
        int i = Math.Abs(signedIndex) - 1;
        if (i < 0 || i > 2) return;
        Target += Math.Sign(signedIndex) * Mags[i];
    }

    // ── 硬體現值（套用後回讀）──────────────────────────────────────────────
    private double _active;
    public double Active
    {
        get => _active;
        set { if (SetProperty(ref _active, value)) { OnPropertyChanged(nameof(ActiveText)); OnPropertyChanged(nameof(DriftText)); } }
    }

    // ── 目標值（滑桿雙向繫結）──────────────────────────────────────────────
    private double _target;
    public double Target
    {
        get => _target;
        set
        {
            double v = double.IsNaN(value) ? _active : Math.Clamp(value, Min, Max);
            if (IsRatio) v = Math.Round(v);   // XTU 僅支援整數倍頻 → 一律對齊整數
            if (SetProperty(ref _target, v))
            {
                OnPropertyChanged(nameof(TargetText));
                OnPropertyChanged(nameof(TargetBandBrush));
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(DriftText));
            }
        }
    }

    // ── 套用回饋 ───────────────────────────────────────────────────────────
    private string _applyStatus = "";
    public string ApplyStatus { get => _applyStatus; set => SetProperty(ref _applyStatus, value); }

    private Severity _applySeverity = Severity.Neutral;
    public Severity ApplySeverity { get => _applySeverity; set => SetProperty(ref _applySeverity, value); }

    /// <summary>目標與現值不同，代表有尚未套用（或需重開機才生效）的變更。</summary>
    public bool IsDirty => Math.Abs(_target - _active) > Step * 0.5;

    // ── 顯示 ───────────────────────────────────────────────────────────────
    public string Label => OcNaming.Label(RawName, Kind);
    /// <summary>原始英文名（雙語顯示用；繁中譯名見 Label）。</summary>
    public string EnglishName => RawName;
    /// <summary>❓ 說明（該選項的解釋）。</summary>
    public string Help => OcNaming.Help(RawName, Kind);
    /// <summary>❓ 說明的引用來源。</summary>
    public string HelpSource => OcNaming.HelpSource;
    /// <summary>ToolTip 完整內容：說明 + 來源。</summary>
    public string HelpTip => $"{Help}\n\n{HelpSource}";
    public bool HasHelp => !string.IsNullOrEmpty(Help);

    public string ActiveText => Fmt(_active);
    public string TargetText => Fmt(_target);
    public string DefaultText => Fmt(Default);
    public string RangeText => $"{Fmt(Min)} ～ {Fmt(Max)}";
    public string DriftText => IsDirty ? $"（現值 {Fmt(_active)}）" : "";

    /// <summary>可寫即時 / 需重開機 / 唯讀 / 停用（暫時棄用）/ 失效 的誠實標籤。</summary>
    public string ApplyModeText =>
        !Enabled ? "此平台停用（暫時棄用）"
        : Max <= Min ? "區間無效（失效）"
        : ReadOnly ? "唯讀（此平台不可調）"
        : RequiresReboot ? "需重新開機生效"
        : RealTime ? "即時生效"
        : "套用後生效";

    public Severity ApplyModeSeverity =>
        !Writable ? Severity.Neutral
        : RequiresReboot ? Severity.Warning
        : Severity.Good;

    /// <summary>電壓類旋鈕的目標值危險色（偏移量與其餘種類為中性描邊）。</summary>
    public Brush TargetBandBrush =>
        Kind is OcKnobKind.Voltage or OcKnobKind.MemoryVoltage
            ? VoltageBand.Eval(_target).Brush
            : SeverityToBrushConverter.Neutral;

    public string Fmt(double v) => Kind switch
    {
        OcKnobKind.Voltage or OcKnobKind.MemoryVoltage => $"{v.ToString("0.000", CultureInfo.InvariantCulture)} V",
        OcKnobKind.VoltageOffset => $"{(v / VScale).ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture)} V",
        OcKnobKind.CoreRatio or OcKnobKind.CacheRatio or OcKnobKind.MemoryRatio => $"{v.ToString("0.##", CultureInfo.InvariantCulture)}×",
        OcKnobKind.Bclk => $"{v.ToString("0.00", CultureInfo.InvariantCulture)} MHz",
        OcKnobKind.PowerLimit => $"{v.ToString("0", CultureInfo.InvariantCulture)} W",
        OcKnobKind.Current => $"{v.ToString("0", CultureInfo.InvariantCulture)} A",
        OcKnobKind.Offset => $"−{Math.Abs(v).ToString("0", CultureInfo.InvariantCulture)}×",
        _ => string.IsNullOrEmpty(Unit) ? v.ToString("0.###", CultureInfo.InvariantCulture)
                                        : $"{v.ToString("0.###", CultureInfo.InvariantCulture)} {Unit}",
    };

    public void ResetTargetToActive() => Target = _active;
}

/// <summary>旋鈕原始英文名 → 繁中標籤、種類與分區判定，以及 ❓ 說明與來源。</summary>
public static class OcNaming
{
    // ── 種類判定 ─────────────────────────────────────────────────────────────
    //   XTU 類別（Category）最可靠 → 優先採用；名稱關鍵字為輔。
    //   關鍵順序：快取（Cache/Uncore/Ring）之電壓判定必須「先於」通用電壓判定，
    //   否則快取電壓偏移（原始 mV）會被誤判為絕對電壓而無法正確調節。
    public static OcKnobKind Classify(string rawName, string category)
    {
        string n = rawName.ToLowerInvariant();
        string c = (category ?? "").ToLowerInvariant();

        // 1) 依 XTU 類別優先（每核心睿頻族最可靠）
        if (c.Contains("turbo.voltageoffset")) return OcKnobKind.VoltageOffset;  // 每核心電壓偏移
        if (c.Contains("turbo.voltageoverride")) return OcKnobKind.Voltage;      // 每核心電壓覆寫
        if (c.Contains("turbo.ratios") || c.Contains("turbo.ratiolimit")) return OcKnobKind.CoreRatio; // 睿頻表／每核心倍頻上限

        // 2) 快取 / Uncore / Ring：電壓與偏移須先於通用電壓判定
        if (c.Contains("cache") || n.Contains("cache") || n.Contains("uncore") || n.Contains("ring"))
        {
            if (n.Contains("mode")) return OcKnobKind.Other;
            if (n.Contains("offset")) return OcKnobKind.VoltageOffset;   // 快取電壓偏移（mV）
            if (n.Contains("volt")) return OcKnobKind.Voltage;           // 快取電壓（絕對值）
            return OcKnobKind.CacheRatio;                                // 快取倍頻
        }

        // 3) AVX 降頻偏移（整數倍頻域）
        if (n.Contains("avx") && n.Contains("offset")) return OcKnobKind.Offset;

        // 4) 外頻 / 電流 / 功耗
        if (n.Contains("reference clock") || n.Contains("bclk") || n.Contains("base clock")) return OcKnobKind.Bclk;
        if (n.Contains("current")) return OcKnobKind.Current;
        if (n.Contains("power") && (n.Contains("max") || n.Contains("limit") || n.Contains("watt"))) return OcKnobKind.PowerLimit;

        // 5) 電壓 / 電壓偏移（記憶體 vs 核心）
        bool mem = c.Contains("memory") || n.Contains("memory") || n.Contains("dram") || n.Contains("vddq") || n.Contains("vdd2");
        if (n.Contains("volt") || n.Contains("vddq") || n.Contains("vddg") || n.Contains("vddp") || n.Contains("agent"))
        {
            if (n.Contains("offset")) return OcKnobKind.VoltageOffset;
            return mem ? OcKnobKind.MemoryVoltage : OcKnobKind.Voltage;
        }

        // 6) 倍頻
        if (mem && (n.Contains("ratio") || n.Contains("multiplier") || n.Contains("frequency"))) return OcKnobKind.MemoryRatio;
        if (n.Contains("ratio") || n.Contains("multiplier")) return OcKnobKind.CoreRatio;  // 含全核心「Performance Core Ratio」
        return OcKnobKind.Other;
    }

    // ── 分區判定 ─────────────────────────────────────────────────────────────
    //   快取（電壓／偏移／倍頻）自成一區，於 UI 緊接目標時脈規劃之下；
    //   每核心睿頻族（Turbo.*）歸「進階」，於該處合併呈現單核心倍頻＋電壓。
    public static OcSection SectionOf(string rawName, string category, OcKnobKind kind)
    {
        string n = rawName.ToLowerInvariant();
        string c = (category ?? "").ToLowerInvariant();

        if (c.StartsWith("processor.turbo")) return OcSection.Advanced;   // 每核心 / 睿頻表
        if (kind == OcKnobKind.CacheRatio) return OcSection.Cache;
        if (c.Contains("cache") || n.Contains("cache") || n.Contains("uncore") || n.Contains("ring")) return OcSection.Cache;

        return kind switch
        {
            OcKnobKind.Voltage or OcKnobKind.CoreRatio or OcKnobKind.VoltageOffset or OcKnobKind.Offset => OcSection.Core,
            OcKnobKind.PowerLimit or OcKnobKind.Current => OcSection.Power,
            OcKnobKind.MemoryVoltage or OcKnobKind.MemoryRatio => OcSection.Memory,
            OcKnobKind.Bclk => OcSection.Bus,
            _ => OcSection.Advanced,
        };
    }

    // ── 繁中標籤 ─────────────────────────────────────────────────────────────
    public static string Label(string rawName, OcKnobKind kind)
    {
        string n = rawName.ToLowerInvariant();

        // 全核心倍頻（使用者反映「看不到調整全核心倍頻」→ 給予明確標籤）
        if (n == "performance core ratio") return "全核心倍頻";

        // 睿頻表：N Active Performance Core(s) — 依同時作用核心數的倍頻上限
        var mAct = Regex.Match(n, @"^(\d+)\s+active performance cores?");
        if (mAct.Success) return $"{mAct.Groups[1].Value} 核負載倍頻上限";

        // 每核心：Performance Core N〔Voltage Override／Offset／Mode〕
        var mCore = Regex.Match(n, @"performance core\s+(\d+)");
        if (mCore.Success)
        {
            string idx = mCore.Groups[1].Value;
            if (n.Contains("voltage override")) return $"第 {idx} 核心電壓";
            if (n.Contains("voltage offset")) return $"第 {idx} 核心電壓偏移";
            if (n.Contains("voltage mode")) return $"第 {idx} 核心電壓模式";
            return $"第 {idx} 核心倍頻上限";
        }

        // 快取（Uncore／Ring）
        if (n.Contains("cache") || n.Contains("uncore") || (n.Contains("ring") && !n.Contains("string")))
        {
            if (n.Contains("offset")) return "快取電壓偏移";
            if (n.Contains("mode")) return "快取電壓模式";
            if (n.Contains("volt")) return "快取電壓";
            return "快取倍頻（Uncore／Ring）";
        }

        // 常見旋鈕給定精準繁中；其餘保留原名（誠實不臆造）
        if (n.Contains("core voltage") && n.Contains("offset")) return "核心電壓偏移";
        if (n.Contains("core voltage") && n.Contains("mode")) return "核心電壓模式";
        if (n.Contains("core voltage")) return "核心電壓 Vcore";
        if (n.Contains("system agent")) return "系統代理電壓 SA";
        if (n.Contains("vddq")) return "記憶體 VDDQ 電壓";
        if (n.Contains("vdd2") || (n.Contains("memory") && n.Contains("volt"))) return "記憶體電壓 VDD2";
        if (n.Contains("reference clock") || n.Contains("bclk")) return "外頻 BCLK（基準時脈）";
        if (n.Contains("max non turbo")) return "非睿頻上限倍頻";
        if (n.Contains("turbo boost short power")) return "PL2 短時功耗牆";
        if (n.Contains("turbo boost power max")) return "PL1 長時功耗牆";
        if (n.Contains("turbo boost power short time")) return "PL2 時間窗";
        if (n.Contains("turbo boost power time")) return "PL1 時間窗";
        if (n.Contains("core current")) return "核心電流上限";
        if (n.Contains("avx-512") || n.Contains("avx512")) return "AVX-512 降頻偏移";
        if (n.Contains("avx2")) return "AVX2 降頻偏移";
        if (n.Contains("avx")) return "AVX 降頻偏移";
        if (n.Contains("ratio") || n.Contains("multiplier"))
            return kind == OcKnobKind.CoreRatio ? "核心倍頻" : rawName;
        return rawName;
    }

    /// <summary>自「Performance Core N」擷取核心序號；非每核心項回 null。</summary>
    public static int? CoreIndexOf(string rawName)
    {
        var m = Regex.Match(rawName.ToLowerInvariant(), @"performance core\s+(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : (int?)null;
    }

    // ── ❓ 說明與引用來源 ─────────────────────────────────────────────────────
    //   說明文字譯自 Intel® XTU SDK 各 ClientTuningControl.Description（實測列舉所得），
    //   以繁體中文改寫；來源標註見 HelpSource。
    public const string HelpSource =
        "來源：Intel® Extreme Tuning Utility（XTU）SDK 控制項描述（ClientTuningControl.Description）｜"
        + "官方下載：intel.com 下載中心 編號 17881";

    public static string Help(string rawName, OcKnobKind kind)
    {
        string n = rawName.ToLowerInvariant();

        if (n == "performance core ratio")
            return "效能核心（P-Core）的運作倍頻。最終頻率 = 此倍頻 × 外頻（BCLK，通常 100 MHz）。"
                 + "XTU 僅支援整數倍調整。";
        if (Regex.IsMatch(n, @"^\d+\s+active performance cores?"))
            return "當「同時有這麼多顆核心在負載」時所允許的睿頻倍頻上限。核心數越多，通常上限越低，以控制發熱與功耗。整數倍。";
        var mCore = Regex.Match(n, @"performance core\s+(\d+)");
        if (mCore.Success)
        {
            string idx = mCore.Groups[1].Value;
            if (n.Contains("voltage override")) return $"直接設定第 {idx} 顆效能核心的運作電壓（絕對值）。過高會增加發熱與劣化風險。";
            if (n.Contains("voltage offset")) return $"在第 {idx} 顆效能核心的請求電壓上額外疊加的偏移量（可正可負），常用於降壓（undervolt）省溫。";
            if (n.Contains("voltage mode")) return $"第 {idx} 顆效能核心的電壓調節模式（自適應／覆寫等）。";
            return $"第 {idx} 顆效能核心單獨作用時可使用的最大倍頻。整數倍。";
        }
        if (n.Contains("cache") || n.Contains("uncore") || (n.Contains("ring") && !n.Contains("string")))
        {
            if (n.Contains("offset")) return "此偏移量會「隨時」疊加到請求的快取電壓上；用於微調快取（Uncore）供電。範圍限 −1 V～+1 V，預設 0 V。";
            if (n.Contains("mode")) return "快取電壓的調節模式。";
            if (n.Contains("volt")) return "供給處理器內部快取介面的電壓；此域與核心（Core）電壓相關聯。";
            return "連接處理器快取與各核心之介面（Uncore／Ring）的運作倍頻上限。整數倍。";
        }
        if (n.Contains("core voltage") && n.Contains("offset"))
            return "在請求的核心電壓上疊加的偏移量（可正可負），常用於降壓省溫或加壓穩定超頻。";
        if (n.Contains("core voltage"))
            return "處理器核心運作電壓 Vcore。頻率越高通常需越高電壓；過高會顯著增加發熱與長期劣化風險。";
        if (n.Contains("system agent"))
            return "系統代理（SA）電壓，影響記憶體控制器與 I/O；高頻記憶體超頻時可能需適度調整。";
        if (n.Contains("vddq") || n.Contains("vdd2") || (n.Contains("memory") && n.Contains("volt")))
            return "記憶體供電電壓。超頻高頻記憶體時可能需提高，但過高會增加記憶體發熱與風險。";
        if (n.Contains("reference clock") || n.Contains("bclk"))
            return "外頻（基準時脈），為各倍頻的乘數基準（通常 100 MHz）。微調外頻會同時影響核心、快取與其他匯流排，需謹慎。";
        if (n.Contains("turbo boost short power") || n.Contains("turbo boost power short time"))
            return "PL2 短時功耗牆／時間窗：處理器在短時間內允許超出的最大功耗與其持續時間。";
        if (n.Contains("turbo boost power max") || n.Contains("turbo boost power time"))
            return "PL1 長時功耗牆／時間窗：核心平均功耗須在此時間窗內維持於此上限之下。";
        if (n.Contains("current"))
            return "處理器允許的最大瞬間電流（IccMax）。過低會限制睿頻，過高則增加供電負擔。";
        if (n.Contains("avx"))
            return "執行 AVX2／AVX-512 這類高負載指令時，對每核心倍頻施加的負向偏移（降頻），以維持穩定與溫度。"
                 + "註：於第 10 代（10-Series）及之後，此偏移不套用於「作用核心倍頻」計算。";
        if (n.Contains("ratio") || n.Contains("multiplier"))
            return "倍頻乘數；最終頻率 = 倍頻 × 外頻。XTU 僅支援整數倍調整。";
        return "";
    }
}

/// <summary>
/// 電壓危險分級（依使用者規格）：
/// &lt;1.0V 藍｜≤1.25V（含 ≤1.1V）綠｜&gt;1.25V 黃｜≥1.4V 紅｜≥1.5V 閃爍暗紅。
/// </summary>
public static class VoltageBand
{
    public static readonly SolidColorBrush Blue = Freeze("#3987e5");
    public static readonly SolidColorBrush Green = Freeze("#0ca30c");
    public static readonly SolidColorBrush Yellow = Freeze("#fab219");
    public static readonly SolidColorBrush Red = Freeze("#d03b3b");
    public static readonly SolidColorBrush DarkRed = Freeze("#7a0d0d");

    public readonly record struct Result(SolidColorBrush Brush, string Label, bool Flashing, Severity Severity);

    public static Result Eval(double v)
    {
        if (v >= 1.5) return new(DarkRed, "危險！電壓過高", true, Severity.Critical);
        if (v >= 1.4) return new(Red, "極高電壓", false, Severity.Critical);
        if (v > 1.25) return new(Yellow, "偏高電壓", false, Severity.Warning);
        if (v < 1.0) return new(Blue, "低電壓", false, Severity.Good);
        return new(Green, "安全電壓", false, Severity.Good);
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>
/// 單顆效能核心的合併調節列：把「該核心倍頻上限＋電壓覆寫＋電壓偏移」依核心序號配對成一列，
/// 讓調整單核心頻率與單核心電壓集中於同一處（依使用者要求合併）。任一項缺席時對應區塊自動隱藏。
/// </summary>
public sealed class CoreTuneRow
{
    public CoreTuneRow(int coreIndex, OcKnob? ratio, OcKnob? voltage, OcKnob? offset)
    {
        CoreIndex = coreIndex;
        Ratio = ratio;
        Voltage = voltage;
        Offset = offset;
    }

    public int CoreIndex { get; }
    public string Title => $"第 {CoreIndex} 核心";
    public OcKnob? Ratio { get; }
    public OcKnob? Voltage { get; }
    public OcKnob? Offset { get; }
    public bool HasRatio => Ratio is not null;
    public bool HasVoltage => Voltage is not null;
    public bool HasOffset => Offset is not null;
}

/// <summary>超頻設定檔（.ocp，本程式自有 JSON 格式）。</summary>
public sealed class OcProfile
{
    public string Name { get; set; } = "未命名";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Note { get; set; } = "";
    public List<OcProfileItem> Items { get; set; } = new();
}

public sealed class OcProfileItem
{
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public double Value { get; set; }
}
