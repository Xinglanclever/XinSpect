using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>倍頻表的一列：某個「作用中核心數上限」對應的倍頻。</summary>
public sealed class TurboRatioRow
{
    public TurboRatioRow(int cores, int ratio, double bclkMhz, bool applicable)
    { Cores = cores; Ratio = ratio; BclkMhz = bclkMhz; Applicable = applicable; }
    public int Cores { get; }
    public int Ratio { get; }
    public double BclkMhz { get; }
    public bool Applicable { get; }
    public string CoresText => $"≤ {Cores} 核";
    public string RatioText => $"{Ratio}×";
    public string FreqText => BclkMhz > 0 ? $"{Ratio * BclkMhz:0} MHz" : "—";
    public string Note => Applicable ? "" : "超出本機核心數，此列不適用";
    public double Opacity => Applicable ? 1.0 : 0.45;
}

/// <summary>單一邏輯處理器的實測有效時脈。</summary>
public sealed class EffectiveClockRow
{
    public EffectiveClockRow(ProcessorRef lp, double mhz, double ratio, bool multiGroup = false)
    { Ref = lp; Mhz = mhz; Ratio = ratio; MultiGroup = multiGroup; }

    /// <summary>此列對應的邏輯處理器（含處理器群組）。</summary>
    public ProcessorRef Ref { get; }
    /// <summary>是否為多群組機器（決定標籤要不要標明群組）。</summary>
    public bool MultiGroup { get; }
    public int Lp => Ref.Index;
    /// <summary>有效時脈（MHz）；-1＝讀不到。</summary>
    public double Mhz { get; }
    /// <summary>APERF/MPERF 比值；乘以 TSC 頻率即有效時脈。</summary>
    public double Ratio { get; }
    public string LpText => MultiGroup ? $"G{Ref.Group}·LP {Ref.Index}" : $"LP {Ref.Index}";
    public string MhzText => Mhz < 0 ? "—" : $"{Mhz:0} MHz";
    public string RatioText => Mhz < 0 ? "—" : $"{Ratio:0.00}×TSC";
    public double BarFraction { get; set; }
}

/// <summary>
/// 頻率真相：全部由 MSR 直讀與實測得到，不解析任何第三方報告、不套用規格書數字。
/// <list type="bullet">
/// <item>MSR 0xCE（PLATFORM_INFO）：最大非渦輪倍頻、最低效能倍頻、最低運作倍頻、倍頻是否解鎖。</item>
/// <item>MSR 0x10（TSC）對 QPC 實測 TSC 頻率；除以最大非渦輪倍頻即得<b>實際 BCLK</b>（不假設 100 MHz）。</item>
/// <item>MSR 0x1AD／0x1AE（TURBO_RATIO_LIMIT／..._CORES）：倍頻表。</item>
/// <item>MSR 0xE7／0xE8（MPERF／APERF）逐邏輯處理器差分：實測有效時脈。</item>
/// <item>MSR 0x770／0x771（HWP 啟用／能力）：有 HWP 才能讀出逐核心最佳性能等級（黃金核心）。</item>
/// </list>
/// </summary>
/// <remarks>
/// 誠實界線：<b>倍頻表是「現在的設定」，不是原廠規格</b>——BIOS 若解鎖並改過倍頻，這裡讀到的就是改過的值，
/// 本工具如實顯示，不還原成規格書數字。0x1AE 若不是遞增的核心數，就不是本格式，會如實說明無法解讀而非硬套。
/// HWP 讀不到時<b>不宣稱</b>任何一顆是黃金核心（那需要 0x771 的逐核心 Highest Performance）。
/// 有效時脈是取樣窗內的平均，不是瞬時峰值；MPERF 以 TSC 速率計數，故比值乘 TSC 頻率才是真實時脈。
/// </remarks>
public sealed class FrequencyTruthService : ObservableObject
{
    private const uint MsrTsc = 0x10;
    private const uint MsrPlatformInfo = 0xCE;
    private const uint MsrMperf = 0xE7;
    private const uint MsrAperf = 0xE8;
    private const uint MsrTurboRatioLimit = 0x1AD;
    private const uint MsrTurboRatioLimitCores = 0x1AE;
    private const uint MsrPmEnable = 0x770;
    private const uint MsrHwpCapabilities = 0x771;

    /// <summary>取樣窗（毫秒）。太短則 APERF/MPERF 差分的量化雜訊明顯。</summary>
    private const int WindowMs = 250;

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _status = "尚未量測。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _bclkText = "—";
    /// <summary>實測 BCLK（TSC 頻率 ÷ 最大非渦輪倍頻）。</summary>
    public string BclkText { get => _bclkText; private set => SetProperty(ref _bclkText, value); }

    private string _tscText = "—";
    public string TscText { get => _tscText; private set => SetProperty(ref _tscText, value); }

    private string _baseText = "—";
    public string BaseText { get => _baseText; private set => SetProperty(ref _baseText, value); }

    private string _minText = "—";
    public string MinText { get => _minText; private set => SetProperty(ref _minText, value); }

    private string _unlockText = "—";
    public string UnlockText { get => _unlockText; private set => SetProperty(ref _unlockText, value); }

    private string _hwpText = "尚未量測。";
    /// <summary>黃金核心（逐核心最佳性能等級）能否讀出的誠實說明。</summary>
    public string HwpText { get => _hwpText; private set => SetProperty(ref _hwpText, value); }

    private string _crossCheckText = "";
    /// <summary>CPUID 0x15／0x16 的交叉驗證說明（實測 BCLK 對得上晶振比例嗎）。</summary>
    public string CrossCheckText { get => _crossCheckText; private set => SetProperty(ref _crossCheckText, value); }

    private string _turboNote = "";
    public string TurboNote { get => _turboNote; private set => SetProperty(ref _turboNote, value); }

    public ObservableCollection<TurboRatioRow> TurboRows { get; } = [];
    public ObservableCollection<EffectiveClockRow> ClockRows { get; } = [];

    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        Status = "量測中…（約 0.5 秒）";
        try
        {
            var r = await Task.Run(Measure);
            Apply(r);
        }
        catch (Exception ex)
        {
            Status = "量測失敗：" + ex.Message;
        }
        finally { IsRunning = false; }
    }

    private void Apply(Measurement r)
    {
        TurboRows.Clear();
        ClockRows.Clear();
        if (r.Error is { } err) { Status = err; return; }

        BaseText = r.MaxNonTurbo > 0 ? $"{r.MaxNonTurbo}× → {r.MaxNonTurbo * r.BclkMhz:0} MHz" : "—";
        MinText = r.MinEfficiency > 0 ? $"{r.MinEfficiency}× → {r.MinEfficiency * r.BclkMhz:0} MHz" : "—";
        BclkText = r.BclkMhz > 0 ? $"{r.BclkMhz:0.00} MHz" : "—";
        TscText = r.TscMhz > 0 ? $"{r.TscMhz:0.0} MHz" : "—";
        UnlockText = r.RatioUnlocked ? "倍頻已解鎖（0xCE 位 28）" : "倍頻鎖定（0xCE 位 28 = 0）";
        HwpText = r.HwpText;
        CrossCheckText = r.CrossCheckText;
        TurboNote = r.TurboNote;

        foreach (var (cores, ratio) in r.TurboGroups)
            TurboRows.Add(new TurboRatioRow(cores, ratio, r.BclkMhz, cores <= r.LpCount));

        double max = r.Clocks.Count > 0 ? r.Clocks.Max(c => c.Mhz) : 0;
        if (max <= 0) max = 1;
        foreach (var (lp, mhz, ratio) in r.Clocks)
            ClockRows.Add(new EffectiveClockRow(lp, mhz, ratio, r.MultiGroup)
            {
                BarFraction = mhz < 0 ? 0 : Math.Clamp(mhz / max, 0.02, 1),
            });

        Status = $"完成：{r.LpCount} 個邏輯處理器、倍頻表 {TurboRows.Count} 組，取樣窗 {WindowMs} ms。"
               + (r.MultiGroup
                    ? $" 本機有 {CpuAffinity.GroupCount} 個處理器群組，全部群組皆已列入；"
                    + "多群組路徑未在實機驗證過。"
                    : "");
    }

    private sealed record Measurement
    {
        public string? Error { get; init; }
        public int MaxNonTurbo { get; init; }
        public int MinEfficiency { get; init; }
        public int MinOperating { get; init; }
        public bool RatioUnlocked { get; init; }
        public double TscMhz { get; init; }
        public double BclkMhz { get; init; }
        public string HwpText { get; init; } = "";
        public string CrossCheckText { get; init; } = "";
        public string TurboNote { get; init; } = "";
        public int LpCount { get; init; }
        /// <summary>是否為多群組機器（＞64 邏輯處理器）；決定標籤是否標明群組並在狀態列如實聲明。</summary>
        public bool MultiGroup { get; init; }
        public IReadOnlyList<(int Cores, int Ratio)> TurboGroups { get; init; } = [];
        public IReadOnlyList<(ProcessorRef Lp, double Mhz, double Ratio)> Clocks { get; init; } = [];
    }

    private Measurement Measure()
    {
        if (!X86Base.IsSupported) return new Measurement { Error = "非 x86 平台，沒有這些 MSR。" };
        using var bridge = WinRing0Bridge.Create();
        if (!bridge.Available) return new Measurement { Error = "WinRing0 橋接不可用：" + bridge.Error };

        var lps = CpuAffinity.AllLogicalProcessors();
        if (lps.Count == 0) return new Measurement { Error = "取不到可用的邏輯處理器。" };

        if (bridge.ReadMsrPair64(MsrPlatformInfo) is not { } platform)
            return new Measurement { Error = "讀不到 MSR 0xCE（PLATFORM_INFO），無法推導 BCLK。" };
        var (maxNonTurbo, minEff, minOp, unlocked) = FrequencyTruthMath.DecodePlatformInfo(platform);

        // TSC 頻率：QPC 夾在 TSC 讀取「前後」，取中點，抵銷橋接呼叫本身的延遲造成的系統性偏低。
        double tscHz = MeasureTscHz(bridge, lps[0]);
        double bclk = FrequencyTruthMath.BclkMhz(tscHz, maxNonTurbo);

        // 倍頻表
        ulong ratios = bridge.ReadMsrPair64(MsrTurboRatioLimit) ?? 0;
        ulong cores = bridge.ReadMsrPair64(MsrTurboRatioLimitCores) ?? 0;
        IReadOnlyList<(int, int)> groups = [];
        string turboNote;
        if (ratios == 0)
            turboNote = "MSR 0x1AD 讀不到或為 0：此處理器未提供渦輪倍頻表。";
        else if (FrequencyTruthMath.LooksLikeCoreCountFormat(cores))
        {
            groups = FrequencyTruthMath.DecodeTurboGroups(ratios, cores);
            turboNote = "格式：0x1AE 為遞增的作用中核心數門檻、0x1AD 為對應倍頻（Skylake-SP 分組式）。"
                      + "此為目前設定，BIOS 改過就是改過的值，本工具不還原成規格書數字。";
        }
        else
        {
            groups = FrequencyTruthMath.DecodeLegacyTurboTable(ratios);
            turboNote = "0x1AE 不是遞增的核心數門檻，改按傳統格式解讀（0x1AD 的八個位元組＝1～8 顆作用中核心）。";
        }

        // HWP：黃金核心要靠 0x771 的逐核心 Highest Performance，讀不到就明說讀不到。
        ulong pmEnable = bridge.ReadMsrPair64(MsrPmEnable) ?? 0;
        string hwp;
        if (bridge.ReadMsrPair64(MsrHwpCapabilities) is { } cap && cap != 0)
        {
            hwp = $"HWP 可用（0x770 = 0x{pmEnable:X}）：本核心 Highest Performance = {(cap & 0xFF)}、"
                + $"Guaranteed = {((cap >> 8) & 0xFF)}、Efficient = {((cap >> 16) & 0xFF)}、Lowest = {((cap >> 24) & 0xFF)}。";
        }
        else
        {
            hwp = $"本機讀不到 MSR 0x771（HWP_CAPABILITIES），0x770 = 0x{pmEnable:X}："
                + "沒有逐核心最佳性能等級可讀，因此不宣稱哪一顆是「黃金核心」。下表的有效時脈是實測值，不是體質評級。";
        }

        var clocks = MeasureEffectiveClocks(bridge, lps, tscHz);

        return new Measurement
        {
            MaxNonTurbo = maxNonTurbo,
            MinEfficiency = minEff,
            MinOperating = minOp,
            RatioUnlocked = unlocked,
            TscMhz = tscHz / 1e6,
            BclkMhz = bclk,
            HwpText = hwp,
            CrossCheckText = CrossCheck(tscHz, maxNonTurbo),
            TurboNote = turboNote,
            LpCount = lps.Count,
            MultiGroup = CpuAffinity.IsMultiGroup,
            TurboGroups = groups,
            Clocks = clocks,
        };
    }

    /// <summary>
    /// 用 CPUID 0x15（TSC／晶振比例）與 0x16（標示頻率）交叉驗證實測 BCLK。
    /// 晶振欄（ECX）為 0 時<b>不猜</b>固定值——改由實測反推，再說明最接近哪個標準晶振。
    /// </summary>
    private static string CrossCheck(double tscHz, int maxNonTurbo)
    {
        if (X86Base.CpuId(0, 0).Eax < 0x16) return "此處理器沒有 CPUID 0x15／0x16 葉，無從交叉驗證。";
        var l15 = X86Base.CpuId(0x15, 0);
        var l16 = X86Base.CpuId(0x16, 0);
        double ratio = FrequencyTruthMath.TscRatio((uint)l15.Ebx, (uint)l15.Eax);
        uint crystalHz = (uint)l15.Ecx;
        var parts = new List<string>();

        if (ratio > 0 && crystalHz > 0)
        {
            double nominal = crystalHz / 1e6 * ratio;
            parts.Add($"CPUID 0x15：晶振 {crystalHz / 1e6:0.###} MHz × {ratio:0.###} → 標準 TSC {nominal:0.0} MHz"
                    + $"（實測差 {(tscHz / 1e6 - nominal) / nominal * 100:+0.00;-0.00}%）");
        }
        else if (ratio > 0)
        {
            double implied = FrequencyTruthMath.ImpliedCrystalMhz(tscHz, ratio);
            parts.Add($"CPUID 0x15：比例 {(uint)l15.Ebx}／{(uint)l15.Eax} = {ratio:0.###}，"
                    + $"晶振欄為 0（處理器未回報，本工具不代填）；由實測反推晶振約 {implied:0.00} MHz"
                    + FrequencyTruthMath.DescribeCrystal(implied));
        }
        else parts.Add("CPUID 0x15 未提供有效的 TSC／晶振比例。");

        if ((uint)l16.Eax > 0)
        {
            double bclk = FrequencyTruthMath.BclkMhz(tscHz, maxNonTurbo);
            parts.Add($"CPUID 0x16 標示：基頻 {(uint)l16.Eax} MHz、最高 {(uint)l16.Ebx} MHz、匯流排 {(uint)l16.Ecx} MHz"
                    + "（這三個值是處理器自報的取整標示值，"
                    + (bclk > 0 ? $"實測 BCLK {bclk:0.00} MHz 才是這台機器現在真正的值）" : "本工具以實測值為準）"));
        }
        return string.Join("；", parts) + "。";
    }

    /// <summary>實測 TSC 頻率（Hz）。釘選單一核心，QPC 取中點以抵銷 MSR 讀取的呼叫延遲。</summary>
    private static double MeasureTscHz(WinRing0Bridge bridge, ProcessorRef target)
    {
        using var pin = CpuAffinity.Pinned(target);
        if (!pin.Ok) { Diag.Swallow("TSC 實測釘選核心", null, "TSC／BCLK 顯示 —"); return 0; }
        if (!Sample(out double q0, out ulong t0)) return 0;
        Thread.Sleep(WindowMs);
        if (!Sample(out double q1, out ulong t1)) return 0;
        double sec = (q1 - q0) / Stopwatch.Frequency;
        return sec > 0 && t1 > t0 ? (t1 - t0) / sec : 0;

        bool Sample(out double qpcMid, out ulong tsc)
        {
            long a = Stopwatch.GetTimestamp();
            ulong? v = bridge.ReadMsrPair64(MsrTsc);
            long b = Stopwatch.GetTimestamp();
            qpcMid = (a + b) / 2.0;
            tsc = v ?? 0;
            return v is not null;
        }
    }

    /// <summary>
    /// 逐邏輯處理器的有效時脈。<b>兩趟掃描共用同一個取樣窗</b>（先全部讀起始值、睡一次、再全部讀結束值），
    /// 而不是每顆各睡一次——後者要 36×窗長，且各核的量測期不同段，無法互相比較。
    /// </summary>
    private static List<(ProcessorRef Lp, double Mhz, double Ratio)> MeasureEffectiveClocks(
        WinRing0Bridge bridge, List<ProcessorRef> lps, double tscHz)
    {
        int n = lps.Count;
        var m0 = new ulong[n]; var a0 = new ulong[n]; var ok0 = new bool[n];
        var m1 = new ulong[n]; var a1 = new ulong[n]; var ok1 = new bool[n];

        void Pass(ulong[] m, ulong[] a, bool[] ok)
        {
            for (int i = 0; i < n; i++)
            {
                using var pin = CpuAffinity.Pinned(lps[i]);
                if (!pin.Ok) continue;
                try
                {
                    if (bridge.ReadMsrPair64(MsrMperf) is { } mv && bridge.ReadMsrPair64(MsrAperf) is { } av)
                    { m[i] = mv; a[i] = av; ok[i] = true; }
                }
                catch (Exception ex) { Diag.Swallow("MPERF／APERF 讀取", ex, $"{lps[i].Label(true)} 的有效時脈顯示 —"); }
            }
        }

        Pass(m0, a0, ok0);
        Thread.Sleep(WindowMs);
        Pass(m1, a1, ok1);

        var result = new List<(ProcessorRef, double, double)>(n);
        for (int i = 0; i < n; i++)
        {
            if (!ok0[i] || !ok1[i]) { result.Add((lps[i], -1, 0)); continue; }
            double ratio = FrequencyTruthMath.AperfMperfRatio(m0[i], m1[i], a0[i], a1[i]);
            result.Add(ratio <= 0 ? (lps[i], -1, 0) : (lps[i], ratio * tscHz / 1e6, ratio));
        }
        return result;
    }
}

/// <summary>頻率真相的純函式部分：MSR 位元解讀與換算。不接觸硬體，可單獨測試。</summary>
public static class FrequencyTruthMath
{
    /// <summary>
    /// 解 MSR 0xCE（PLATFORM_INFO）：位 15:8＝最大非渦輪倍頻、位 47:40＝最低效能倍頻、
    /// 位 55:48＝最低運作倍頻、位 28＝可程式化倍頻上限（解鎖）。
    /// </summary>
    public static (int MaxNonTurbo, int MinEfficiency, int MinOperating, bool RatioUnlocked) DecodePlatformInfo(ulong v)
        => ((int)((v >> 8) & 0xFF), (int)((v >> 40) & 0xFF), (int)((v >> 48) & 0xFF), (v & (1UL << 28)) != 0);

    /// <summary>BCLK（MHz）＝實測 TSC 頻率 ÷ 最大非渦輪倍頻。倍頻為 0 或頻率無效時回 0，不除以零。</summary>
    public static double BclkMhz(double tscHz, int maxNonTurboRatio)
        => maxNonTurboRatio <= 0 || !double.IsFinite(tscHz) || tscHz <= 0 ? 0 : tscHz / maxNonTurboRatio / 1e6;

    /// <summary>
    /// 0x1AE 是否為「遞增的作用中核心數門檻」格式（Skylake-SP 分組式）。
    /// 判準：第一個位元組非零，且往後嚴格遞增（傳統格式的 0x1AE 是倍頻，通常遞減或全等）。
    /// </summary>
    public static bool LooksLikeCoreCountFormat(ulong cores)
    {
        if ((cores & 0xFF) == 0) return false;
        int prev = -1, seen = 0;
        for (int i = 0; i < 8; i++)
        {
            int b = (int)((cores >> (i * 8)) & 0xFF);
            if (b == 0) break;
            if (b <= prev) return false;
            prev = b;
            seen++;
        }
        return seen >= 2;
    }

    /// <summary>分組式倍頻表：0x1AE 的位元組＝核心數門檻、0x1AD 的同序位元組＝倍頻。</summary>
    public static IReadOnlyList<(int Cores, int Ratio)> DecodeTurboGroups(ulong ratios, ulong cores)
    {
        var list = new List<(int, int)>(8);
        for (int i = 0; i < 8; i++)
        {
            int c = (int)((cores >> (i * 8)) & 0xFF);
            int r = (int)((ratios >> (i * 8)) & 0xFF);
            if (c == 0 || r == 0) continue;
            list.Add((c, r));
        }
        return list;
    }

    /// <summary>傳統倍頻表：0x1AD 的第 i 個位元組＝「i+1 顆作用中核心」的倍頻。</summary>
    public static IReadOnlyList<(int Cores, int Ratio)> DecodeLegacyTurboTable(ulong ratios)
    {
        var list = new List<(int, int)>(8);
        for (int i = 0; i < 8; i++)
        {
            int r = (int)((ratios >> (i * 8)) & 0xFF);
            if (r == 0) continue;
            list.Add((i + 1, r));
        }
        return list;
    }

    /// <summary>APERF/MPERF 差分比值。分母非正（回繞或未前進）時回 0，不除以零、不回負值。</summary>
    public static double AperfMperfRatio(ulong m0, ulong m1, ulong a0, ulong a1)
    {
        if (m1 <= m0 || a1 < a0) return 0;
        double r = (a1 - a0) / (double)(m1 - m0);
        return double.IsFinite(r) && r > 0 ? r : 0;
    }

    /// <summary>CPUID 0x15 的 TSC／晶振比例＝EBX ÷ EAX。任一為 0 時回 0。</summary>
    public static double TscRatio(uint numerator, uint denominator)
        => numerator == 0 || denominator == 0 ? 0 : numerator / (double)denominator;

    /// <summary>由實測 TSC 反推晶振頻率（MHz）。用於 CPUID 0x15 的晶振欄為 0 時，取代「假設 24 MHz」。</summary>
    public static double ImpliedCrystalMhz(double tscHz, double ratio)
        => ratio <= 0 || !double.IsFinite(tscHz) || tscHz <= 0 ? 0 : tscHz / ratio / 1e6;

    /// <summary>業界標準晶振頻率（MHz）。</summary>
    private static readonly double[] StandardCrystals = [19.2, 24.0, 25.0, 38.4];

    /// <summary>反推出的晶振最接近哪個標準值（誤差 1% 內才敢說）。回傳可直接接在句尾的說明。</summary>
    public static string DescribeCrystal(double impliedMhz)
    {
        if (impliedMhz <= 0) return "";
        foreach (double c in StandardCrystals)
            if (Math.Abs(impliedMhz - c) / c <= 0.01)
                return $"（與標準 {c:0.#} MHz 相符）";
        return "（不接近任何標準晶振，僅供參考）";
    }
}
