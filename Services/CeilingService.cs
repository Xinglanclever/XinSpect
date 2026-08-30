using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>量測窗要製造的負載型態。基線窗不製造負載，是後面三種的對照組。</summary>
public enum CeilingLoad
{
    /// <summary>不製造負載。用來取得能量計與溫度的靜止基準。</summary>
    Baseline,
    /// <summary>純整數乘加鏈，完全不碰向量單元——向量降頻的對照組。</summary>
    Integer,
    /// <summary>256 位元浮點乘法（VMULPD ymm）。</summary>
    Avx2,
    /// <summary>512 位元浮點乘法（VMULPD zmm）。僅在硬體支援 AVX-512F 時執行。</summary>
    Avx512,
}

/// <summary>
/// 效能天花板：回答「為什麼這顆 CPU 跑不到它該有的頻率」。
/// 靜態讀出所有硬性上限，再<b>自己製造負載</b>去撞，把撞到的牆用限制原因暫存器指出來。
/// <list type="bullet">
/// <item>0x1A2（TEMPERATURE_TARGET）：TCC 活化溫度與偏移 → 真正的節流點。</item>
/// <item>0x606／0x610／0x613／0x614（RAPL）：換算單位、PL1／PL2 與時間窗、節流累計、封裝功耗規格。</item>
/// <item>0x19C／0x1B1（THERM_STATUS）：逐核與封裝的溫度讀數與八組黏滯紀錄位。</item>
/// <item>0x64F／0x690（CORE_PERF_LIMIT_REASONS）：限制原因，狀態位 15:0、黏滯紀錄位 31:16。</item>
/// <item>0xE7／0xE8（MPERF／APERF）逐核差分：實測有效倍頻與「這顆核心醒著多久」。</item>
/// <item>0x1AD／0x1AE：倍頻表 → 目前作用核心數本來該給的倍頻。</item>
/// </list>
/// </summary>
/// <remarks>
/// <b>全程唯讀</b>：只讀 MSR，一個位元都不寫，也不清除任何黏滯紀錄位（清除需要寫入，會毀掉別的工具的證據）。
///
/// 三個刻意的設計決定，都是實測逼出來的：
/// <list type="number">
/// <item><b>能量計要先自我驗證才敢換算成瓦。</b>本機（Skylake-X）的 0x611 讀得到也在前進，
/// 但速率不隨真實功耗變化——閒置與全核 AVX2 只差 4%，硬換算會得出「18 核滿載 1.2 W」。
/// 故先跑基線窗，再拿負載窗跟它比，比不出差別就只顯示原始計數。見 <see cref="CeilingDecoder.ValidateEnergyCounter"/>。</item>
/// <item><b>負載執行緒跑一般優先權，並且比邏輯處理器數少兩個。</b>實測用最高優先權跑滿全部邏輯處理器時，
/// 9.08 秒的窗只取到 3 個樣本；改成一般優先權並留兩個邏輯處理器的餘裕後，取樣全部落在預定時點上。</item>
/// <item><b>逐核溫度只在窗末掃一次</b>，窗內高頻取樣改讀封裝級的 0x1B1——
/// 逐核掃描要 18 次釘選加 18 次 MSR 讀取，放進 50 ms 的取樣迴圈只會把自己變成負載。</item>
/// </list>
///
/// MSR 讀取經 <see cref="WinRing0Bridge"/>（風險聲明見該類別）。多群組機器的路徑未在實機驗證過。
/// </remarks>
public sealed class CeilingService : ObservableObject
{
    // ── MSR 位址 ────────────────────────────────────────────────────────────
    private const uint MsrTsc = 0x10;
    private const uint MsrPlatformInfo = 0xCE;
    private const uint MsrMperf = 0xE7;
    private const uint MsrAperf = 0xE8;
    private const uint MsrThermStatus = 0x19C;          // 逐核 IA32_THERM_STATUS
    private const uint MsrTempTarget = 0x1A2;           // MSR_TEMPERATURE_TARGET
    private const uint MsrTurboRatioLimit = 0x1AD;
    private const uint MsrTurboRatioLimitCores = 0x1AE;
    private const uint MsrMiscPwrMgmt = 0x1AA;          // MSR_MISC_PWR_MGMT
    private const uint MsrPkgThermStatus = 0x1B1;       // IA32_PACKAGE_THERM_STATUS
    private const uint MsrPowerCtl = 0x1FC;             // MSR_POWER_CTL
    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrPkgPowerLimit = 0x610;
    private const uint MsrPkgEnergyStatus = 0x611;
    private const uint MsrPkgPerfStatus = 0x613;        // 封裝功耗節流累計
    private const uint MsrPkgPowerInfo = 0x614;
    private const uint MsrDramEnergyStatus = 0x619;
    private const uint MsrUncoreRatioLimit = 0x620;
    private const uint MsrUncorePerfStatus = 0x621;
    private const uint MsrPp0PowerLimit = 0x638;
    private const uint MsrPp0EnergyStatus = 0x639;
    private const uint MsrLimitReasonsServer = 0x64F;   // Skylake-SP／X
    private const uint MsrLimitReasonsClient = 0x690;   // 用戶端平台

    /// <summary>負載壓上去之後、開始計數之前的沉降時間（毫秒）。頻率授權等級與風扇轉速都需要時間穩定。</summary>
    private const int SettleMs = 700;

    /// <summary>兩個負載窗之間的閒置間隔（毫秒）。讓溫度與功耗回落，下一窗才不是接著上一窗的餘熱在跑。</summary>
    private const int CooldownMs = 900;

    /// <summary>窗內取樣間隔（毫秒）。只讀封裝級暫存器，成本低到不會自己變成負載。</summary>
    private const int SampleMs = 50;

    /// <summary>判定一顆核心「在這個窗裡是作用中」的醒著比例門檻。</summary>
    private const double AwakeThreshold = 0.5;

    // ── 可繫結狀態 ──────────────────────────────────────────────────────────
    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _status = "尚未量測。按「讀取硬性上限」只做靜態讀取；按「開始撞牆量測」會自己製造負載。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private double _progress;
    /// <summary>0～1 的進度，供進度條使用。</summary>
    public double Progress { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }

    /// <summary>0～100 的進度，給 ProgressBar 用（預設 Maximum 是 100）。</summary>
    public double ProgressPercent => _progress * 100;

    private int _durationSec = 5;
    /// <summary>每個負載窗的計數秒數（不含沉降與冷卻）。</summary>
    public int DurationSec { get => _durationSec; set => SetProperty(ref _durationSec, Math.Clamp(value, 2, 20)); }

    /// <summary>硬性上限：溫度、功耗、頻率、電源開關。全為靜態讀取，不需要負載。</summary>
    public ObservableCollection<CeilingRow> LimitRows { get; } = [];

    /// <summary>自開機以來撞過的牆（黏滯紀錄位）。本工具不寫入，故不會清掉它們。</summary>
    public ObservableCollection<CeilingRow> HistoryRows { get; } = [];

    /// <summary>各負載窗的對照結果。</summary>
    public ObservableCollection<CeilingWindow> Windows { get; } = [];

    private string _energyText = "";
    /// <summary>能量計自我驗證的結論——決定本頁敢不敢顯示瓦數。</summary>
    public string EnergyText { get => _energyText; private set => SetProperty(ref _energyText, value); }

    private string _reasonRegText = "";
    /// <summary>哪一顆限制原因暫存器在本機真的有回應（0x64F 或 0x690），以及另一顆的情況。</summary>
    public string ReasonRegText { get => _reasonRegText; private set => SetProperty(ref _reasonRegText, value); }

    private string _verdictHeadline = "";
    public string VerdictHeadline { get => _verdictHeadline; private set => SetProperty(ref _verdictHeadline, value); }

    private string _verdictDetail = "";
    public string VerdictDetail { get => _verdictDetail; private set => SetProperty(ref _verdictDetail, value); }

    private Severity _verdictSeverity = Severity.Neutral;
    public Severity VerdictSeverity { get => _verdictSeverity; private set => SetProperty(ref _verdictSeverity, value); }

    private bool _hasVerdict;
    public bool HasVerdict { get => _hasVerdict; private set => SetProperty(ref _hasVerdict, value); }

    /// <summary>本頁的界線宣告，供畫面直接顯示。（寫成實例屬性：WPF 的繫結路徑解析不了靜態成員。）</summary>
    public string ScopeNotice => CeilingDecoder.ScopeNotice;

    /// <summary>本機是否支援 AVX-512F——不支援時畫面要說明少了哪一個量測窗，而不是靜默跳過。</summary>
    public bool Avx512Available => Avx512F.IsSupported;

    /// <summary>本機支援到的最寬向量指令集，供畫面說明會跑哪幾個窗。</summary>
    public string VectorSupportText =>
        Avx512F.IsSupported ? "本機支援 AVX-512F，會多跑一個 512 位元浮點窗（最寬的向量通常也最熱、降頻最深）。"
        : Avx2.IsSupported ? "本機支援 AVX2 但不支援 AVX-512F，故只跑到 256 位元浮點窗——不會假造一個 512 位元的結果。"
        : "本機不支援 AVX2，故只跑整數負載窗，本頁無法量出向量授權降頻。";

    // ── 負載產生器 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 自製 CPU 負載。這是本頁唯一「主動做事」的部分：溫度牆、功耗牆與向量降頻在閒置時完全看不見。
    /// </summary>
    /// <remarks>
    /// <b>優先權刻意留在 <see cref="ThreadPriority.Normal"/>，執行緒數也刻意比邏輯處理器少兩個。</b>
    /// 實測用 <c>Highest</c> 跑滿全部邏輯處理器時，取樣執行緒被餓死到 9.08 秒只取得 3 個樣本，
    /// 而且窗長超出預定值 50%——量測工具把自己量壞了。降回一般優先權後取樣完全準時。
    ///
    /// 每個迴圈都把結果餵回下一輪，並以一個永不成立的條件讀取它，
    /// 免得 JIT 判定整段沒有副作用而消掉——那樣就會量到「滿載卻沒升溫」。
    /// </remarks>
    private sealed class LoadGenerator : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Thread> _threads = [];

        public LoadGenerator(CeilingLoad kind, int threadCount)
        {
            if (kind == CeilingLoad.Baseline) return;
            for (int i = 0; i < threadCount; i++)
            {
                var t = new Thread(() => Run(kind, _cts.Token))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Normal,
                    Name = "XinSpect 天花板負載",
                };
                _threads.Add(t);
                t.Start();
            }
        }

        public int ThreadCount => _threads.Count;

        public void Dispose()
        {
            try { _cts.Cancel(); }
            catch (Exception ex) { Diag.Swallow("取消負載執行緒", ex, "負載執行緒改由行程結束時回收"); }
            foreach (var t in _threads)
            {
                try { t.Join(3000); }
                catch (Exception ex) { Diag.Swallow("等待負載執行緒結束", ex, "該執行緒為背景執行緒，不阻擋關閉"); }
            }
            _cts.Dispose();
        }

        private static void Run(CeilingLoad kind, CancellationToken ct)
        {
            switch (kind)
            {
                case CeilingLoad.Integer: Integer(ct); break;
                case CeilingLoad.Avx2: Avx2Chain(ct); break;
                case CeilingLoad.Avx512: Avx512Chain(ct); break;
            }
        }

        /// <summary>純整數乘加鏈：只用整數乘法器與 ALU，完全不觸發向量單元的頻率授權降級。</summary>
        private static void Integer(CancellationToken ct)
        {
            ulong a = 0x9E3779B97F4A7C15, b = 0xBF58476D1CE4E5B9, c = 0x94D049BB133111EB, d = 1;
            while (!ct.IsCancellationRequested)
            {
                for (int k = 0; k < 300_000; k++)
                {
                    a = a * 6364136223846793005UL + 1442695040888963407UL;
                    b = b * 2862933555777941757UL + 3037000493UL;
                    c = c * 3935559000370003845UL + 2691343689449507681UL;
                    d ^= (a >> 13) ^ (b >> 17) ^ (c >> 19);
                }
                if (d == 0) return;   // 永不成立；只為讓上面那段有可觀察的結果，不會被最佳化掉
            }
        }

        /// <summary>256 位元浮點乘法鏈。四條互不相依的鏈填滿乘法管線，把功耗與溫度真的推上去。</summary>
        private static void Avx2Chain(CancellationToken ct)
        {
            var m = Vector256.Create(1.0000000001);
            while (!ct.IsCancellationRequested)
            {
                var (x, y, z, w) = (Vector256.Create(1.5), Vector256.Create(2.5),
                                    Vector256.Create(3.5), Vector256.Create(4.5));
                for (int k = 0; k < 60_000; k++)
                {
                    x = Avx.Multiply(x, m); y = Avx.Multiply(y, m);
                    z = Avx.Multiply(z, m); w = Avx.Multiply(w, m);
                }
                if (x.GetElement(0) + y.GetElement(1) + z.GetElement(2) + w.GetElement(3) < 0) return;
            }
        }

        /// <summary>512 位元浮點乘法鏈。呼叫端已確認 <see cref="Avx512F.IsSupported"/>。</summary>
        private static void Avx512Chain(CancellationToken ct)
        {
            var m = Vector512.Create(1.0000000001);
            while (!ct.IsCancellationRequested)
            {
                var (x, y, z, w) = (Vector512.Create(1.5), Vector512.Create(2.5),
                                    Vector512.Create(3.5), Vector512.Create(4.5));
                for (int k = 0; k < 60_000; k++)
                {
                    x = Avx512F.Multiply(x, m); y = Avx512F.Multiply(y, m);
                    z = Avx512F.Multiply(z, m); w = Avx512F.Multiply(w, m);
                }
                if (x.GetElement(0) + y.GetElement(1) + z.GetElement(2) + w.GetElement(3) < 0) return;
            }
        }

    }

    // ── 執行流程 ────────────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;

    /// <summary>只做靜態讀取（硬性上限 ＋ 開機至今的黏滯紀錄），不製造任何負載。</summary>
    public void LoadStatic() => _ = RunAsync(false);

    /// <summary>完整量測：靜態讀取 ＋ 各負載窗對照 ＋ 判決。期間會自己製造 CPU 負載。</summary>
    public void Start() => _ = RunAsync(true);

    /// <summary>中止量測。負載執行緒會在 <see cref="LoadGenerator.Dispose"/> 裡被收掉。</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); }
        catch (Exception ex) { Diag.Swallow("中止天花板量測", ex, "量測會自行跑完，不影響資料正確性"); }
    }

    private async Task RunAsync(bool withLoad)
    {
        if (IsRunning) return;
        IsRunning = true;
        Progress = 0;
        Status = withLoad ? "準備量測…" : "讀取靜態上限…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        var report = new Progress<(double Frac, string Text)>(t => { Progress = t.Frac; Status = t.Text; });
        try
        {
            int dur = DurationSec;
            var r = await Task.Run(() => Measure(withLoad, dur, report, cts.Token), cts.Token);
            Apply(r, withLoad);
        }
        catch (OperationCanceledException)
        {
            Status = "已停止。負載執行緒已結束；本頁全程未做任何寫入。";
        }
        catch (Exception ex)
        {
            Status = "量測失敗：" + ex.Message;
        }
        finally { IsRunning = false; Progress = 0; _cts = null; cts.Dispose(); }
    }

    private void Apply(Result r, bool withLoad)
    {
        if (r.Error is { } err) { Status = err; return; }

        LimitRows.Clear();
        foreach (var row in r.Limits) LimitRows.Add(row);
        HistoryRows.Clear();
        foreach (var row in r.History) HistoryRows.Add(row);
        ReasonRegText = r.ReasonRegText;

        if (withLoad)
        {
            Windows.Clear();
            foreach (var w in r.Windows) Windows.Add(w);
            EnergyText = r.EnergyText;
            VerdictSeverity = r.VerdictSeverity;
            VerdictHeadline = r.VerdictHeadline;
            VerdictDetail = r.VerdictDetail;
            HasVerdict = r.HasVerdict;
        }
        Status = r.StatusText;
    }

    private sealed record Result
    {
        public string? Error { get; init; }
        public IReadOnlyList<CeilingRow> Limits { get; init; } = [];
        public IReadOnlyList<CeilingRow> History { get; init; } = [];
        public IReadOnlyList<CeilingWindow> Windows { get; init; } = [];
        public string EnergyText { get; init; } = "";
        public string ReasonRegText { get; init; } = "";
        public bool HasVerdict { get; init; }
        public Severity VerdictSeverity { get; init; }
        public string VerdictHeadline { get; init; } = "";
        public string VerdictDetail { get; init; } = "";
        public string StatusText { get; init; } = "";
    }

    // ── MSR 讀取小工具 ──────────────────────────────────────────────────────

    /// <summary>釘到指定邏輯處理器上讀一顆 MSR。釘不上就回 <c>null</c>，而不是在別的核心上照讀一個沒有意義的值。</summary>
    private static ulong? ReadPinned(WinRing0Bridge bridge, ProcessorRef lp, uint msr)
    {
        using var pin = CpuAffinity.Pinned(lp);
        if (!pin.Ok)
        {
            Diag.Swallow($"釘選 {lp.Label(CpuAffinity.IsMultiGroup)} 以讀 MSR 0x{msr:X}", null, "該核心此項顯示無資料");
            return null;
        }
        try { return bridge.ReadMsrPair64(msr); }
        catch (Exception ex) { Diag.Swallow($"讀取 MSR 0x{msr:X}", ex, "該項顯示無資料"); return null; }
    }

    /// <summary>讀一顆封裝／插槽層級的 MSR（不需要釘選）。讀不到回 0，由呼叫端判斷 0 代表「不提供」還是「真的是 0」。</summary>
    private static ulong Read(WinRing0Bridge bridge, uint msr)
    {
        try { return bridge.ReadMsrPair64(msr) ?? 0; }
        catch (Exception ex) { Diag.Swallow($"讀取 MSR 0x{msr:X}", ex, "該項顯示無資料"); return 0; }
    }

    private sealed record Core(int Index, ProcessorRef First, string LpText);

    /// <summary>逐實體核心掃一顆 MSR：每核只用第一個邏輯處理器，SMT 兄弟不重複計算。</summary>
    private static ulong[] SweepPerCore(WinRing0Bridge bridge, IReadOnlyList<Core> cores, uint msr)
    {
        var v = new ulong[cores.Count];
        if (msr == 0) return v;
        for (int i = 0; i < cores.Count; i++) v[i] = ReadPinned(bridge, cores[i].First, msr) ?? 0;
        return v;
    }

    /// <summary>
    /// 逐實體核心讀 MPERF／APERF。兩趟共用同一個取樣窗（先全讀起始值、等一個窗、再全讀結束值），
    /// 而不是每顆各等一次——後者每核的量測期落在不同時段，彼此無法比較。
    /// </summary>
    private static void PerfPass(WinRing0Bridge bridge, IReadOnlyList<Core> cores, ulong[] m, ulong[] a, bool[] ok)
    {
        for (int i = 0; i < cores.Count; i++)
        {
            using var pin = CpuAffinity.Pinned(cores[i].First);
            if (!pin.Ok) continue;
            try
            {
                if (bridge.ReadMsrPair64(MsrMperf) is { } mv && bridge.ReadMsrPair64(MsrAperf) is { } av)
                { m[i] = mv; a[i] = av; ok[i] = true; }
            }
            catch (Exception ex) { Diag.Swallow("MPERF／APERF 讀取", ex, $"核心 {cores[i].Index} 不列入有效倍頻平均"); }
        }
    }

    /// <summary>可中斷的等待。切成小段是為了讓「停止」按下之後能在 25 ms 內真的停下來。</summary>
    private static void Sleep(int ms, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(Math.Max(1, Math.Min(25, ms - (int)sw.ElapsedMilliseconds)));
        }
    }

    /// <summary>實測 TSC 頻率（Hz）。釘選單一核心，QPC 夾在 MSR 讀取前後取中點，抵銷橋接呼叫本身的延遲。</summary>
    private static double MeasureTscHz(WinRing0Bridge bridge, ProcessorRef target)
    {
        using var pin = CpuAffinity.Pinned(target);
        if (!pin.Ok) { Diag.Swallow("TSC 實測釘選核心", null, "頻率相關欄位顯示 —"); return 0; }
        if (!Sample(out double q0, out ulong t0)) return 0;
        Thread.Sleep(250);
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

    // ── 靜態讀取 ────────────────────────────────────────────────────────────

    /// <summary>一次靜態讀取的全部原始素材。原始值一律留著，畫面上每一列都要附出處。</summary>
    private sealed record Statics
    {
        public RaplUnits Units { get; init; }
        public ulong UnitsRaw { get; init; }
        public ulong PkgLimitRaw { get; init; }
        public PkgPowerInfo Info { get; init; }
        public ulong InfoRaw { get; init; }
        public (int TccC, int OffsetC, int ThrottleAtC, bool Valid) Target { get; init; }
        public ulong TargetRaw { get; init; }
        public int MaxNonTurbo { get; init; }
        public int MinEfficiency { get; init; }
        public bool RatioUnlocked { get; init; }
        public double TscHz { get; init; }
        public double BclkMhz { get; init; }
        public IReadOnlyList<(int Cores, int Ratio)> TurboGroups { get; init; } = [];
        public string TurboNote { get; init; } = "";
        public ulong UncoreLimitRaw { get; init; }
        public ulong UncoreStatusRaw { get; init; }
        public ulong PowerCtlRaw { get; init; }
        public ulong MiscPwrRaw { get; init; }
        public ulong EnergyRaw { get; init; }
        public ulong ThrottleRaw { get; init; }
        public ulong Pp0LimitRaw { get; init; }
        public ulong Pp0EnergyRaw { get; init; }
        public ulong DramEnergyRaw { get; init; }
        public ulong PkgThermRaw { get; init; }
        public ulong ReasonRaw { get; init; }
        public uint ReasonMsr { get; init; }
        public string ReasonRegText { get; init; } = "";
        public int CoreCount { get; init; }
        public int LpCount { get; init; }
        public bool MultiGroup { get; init; }
    }

    private static Statics ReadStatics(WinRing0Bridge bridge, IReadOnlyList<Core> cores, int lpCount)
    {
        ulong unitsRaw = Read(bridge, MsrRaplPowerUnit);
        var units = CeilingDecoder.DecodeRaplUnits(unitsRaw);
        ulong infoRaw = Read(bridge, MsrPkgPowerInfo);
        ulong targetRaw = Read(bridge, MsrTempTarget);
        ulong platform = Read(bridge, MsrPlatformInfo);
        var (maxNonTurbo, minEff, _, unlocked) = FrequencyTruthMath.DecodePlatformInfo(platform);
        double tscHz = MeasureTscHz(bridge, cores[0].First);

        ulong ratios = Read(bridge, MsrTurboRatioLimit);
        ulong coreThresholds = Read(bridge, MsrTurboRatioLimitCores);
        IReadOnlyList<(int Cores, int Ratio)> groups;
        string turboNote;
        if (ratios == 0)
        {
            groups = [];
            turboNote = "MSR 0x1AD 讀不到或為 0：此處理器未提供渦輪倍頻表，故本頁不宣稱「本來該跑多少」。";
        }
        else if (FrequencyTruthMath.LooksLikeCoreCountFormat(coreThresholds))
        {
            groups = FrequencyTruthMath.DecodeTurboGroups(ratios, coreThresholds);
            turboNote = $"0x1AD = 0x{ratios:X16}、0x1AE = 0x{coreThresholds:X16}（分組式：0x1AE 為遞增的作用中核心數門檻）。"
                      + "這是「目前的設定」，BIOS 改過就是改過的值，本工具不還原成規格書數字。";
        }
        else
        {
            groups = FrequencyTruthMath.DecodeLegacyTurboTable(ratios);
            turboNote = $"0x1AD = 0x{ratios:X16}；0x1AE 不是遞增的核心數門檻，改按傳統格式解讀"
                      + "（0x1AD 的八個位元組＝1～8 顆作用中核心）。";
        }

        var (reasonMsr, reasonRaw, reasonRegText) = ProbeReasonRegister(bridge, cores[0].First);

        return new Statics
        {
            Units = units,
            UnitsRaw = unitsRaw,
            PkgLimitRaw = Read(bridge, MsrPkgPowerLimit),
            Info = CeilingDecoder.DecodePkgPowerInfo(infoRaw, units),
            InfoRaw = infoRaw,
            Target = CeilingDecoder.DecodeTemperatureTarget(targetRaw),
            TargetRaw = targetRaw,
            MaxNonTurbo = maxNonTurbo,
            MinEfficiency = minEff,
            RatioUnlocked = unlocked,
            TscHz = tscHz,
            BclkMhz = FrequencyTruthMath.BclkMhz(tscHz, maxNonTurbo),
            TurboGroups = groups,
            TurboNote = turboNote,
            UncoreLimitRaw = Read(bridge, MsrUncoreRatioLimit),
            UncoreStatusRaw = Read(bridge, MsrUncorePerfStatus),
            PowerCtlRaw = Read(bridge, MsrPowerCtl),
            MiscPwrRaw = Read(bridge, MsrMiscPwrMgmt),
            EnergyRaw = Read(bridge, MsrPkgEnergyStatus),
            ThrottleRaw = Read(bridge, MsrPkgPerfStatus),
            Pp0LimitRaw = Read(bridge, MsrPp0PowerLimit),
            Pp0EnergyRaw = Read(bridge, MsrPp0EnergyStatus),
            DramEnergyRaw = Read(bridge, MsrDramEnergyStatus),
            PkgThermRaw = Read(bridge, MsrPkgThermStatus),
            ReasonRaw = reasonRaw,
            ReasonMsr = reasonMsr,
            ReasonRegText = reasonRegText,
            CoreCount = cores.Count,
            LpCount = lpCount,
            MultiGroup = CpuAffinity.IsMultiGroup,
        };
    }

    /// <summary>
    /// 找出本機真正有回應的限制原因暫存器。伺服器平台在 0x64F、用戶端在 0x690，
    /// <b>兩顆都試，並且如實回報是哪一顆答的</b>——猜錯的那一顆會回 0，而 0 看起來就像「一切正常」。
    /// </summary>
    private static (uint Msr, ulong Value, string Text) ProbeReasonRegister(WinRing0Bridge bridge, ProcessorRef lp)
    {
        ulong? srv = ReadPinned(bridge, lp, MsrLimitReasonsServer);
        ulong? cli = ReadPinned(bridge, lp, MsrLimitReasonsClient);
        static string Other(ulong? v) => v is null ? "讀取失敗（本平台沒有這顆暫存器）" : $"= 0x{v:X16}";

        if (srv is { } s && s != 0)
            return (MsrLimitReasonsServer, s,
                $"限制原因取自 MSR 0x64F（MSR_CORE_PERF_LIMIT_REASONS，Skylake-SP／X 位址），現值 0x{s:X16}。"
                + $"另一個候選 0x690（用戶端位址）{Other(cli)}，不採用。");

        if (cli is { } c && c != 0)
            return (MsrLimitReasonsClient, c,
                $"限制原因取自 MSR 0x690（MSR_CORE_PERF_LIMIT_REASONS，用戶端位址），現值 0x{c:X16}。"
                + $"另一個候選 0x64F（伺服器位址）{Other(srv)}，不採用。");

        return (0, 0,
            $"0x64F {Other(srv)}、0x690 {Other(cli)}：兩顆限制原因暫存器都沒有可用的值。"
            + "本頁因此不會宣稱任何限制原因——溫度與功耗的數字仍然有效，但「是誰在限制你」這一題本機答不出來。");
    }

    // ── 卡片一：硬性上限 ────────────────────────────────────────────────────

    private static List<CeilingRow> BuildLimitRows(Statics s)
    {
        var rows = new List<CeilingRow>(20);

        // 溫度
        rows.Add(s.Target.Valid
            ? new CeilingRow
            {
                Name = "節流溫度",
                Value = $"{s.Target.ThrottleAtC} °C",
                Evidence = $"MSR 0x1A2 = 0x{s.TargetRaw:X16}（位 23:16＝TCC {s.Target.TccC}、位 29:24＝偏移 {s.Target.OffsetC}）",
                Severity = Severity.Neutral,
                Note = s.Target.OffsetC > 0
                    ? $"TCC 活化溫度是 {s.Target.TccC} °C，但 BIOS／使用者設了 {s.Target.OffsetC} °C 的提前量，"
                      + $"所以實際到 {s.Target.ThrottleAtC} °C 就開始降頻。"
                    : "沒有設定 TCC 偏移，活化溫度就是節流溫度。",
            }
            : new CeilingRow
            {
                Name = "節流溫度",
                Value = "讀不到",
                Evidence = $"MSR 0x1A2 = 0x{s.TargetRaw:X16}",
                Severity = Severity.Warning,
                Note = "沒有 TCC 活化溫度就無法把 THERM_STATUS 的數位讀數換算成攝氏，"
                     + "所以本頁所有溫度都會顯示「—」。本工具不套規格書上的 100 或 105 進去。",
            });

        // 功耗換算單位
        rows.Add(s.Units.Valid
            ? new CeilingRow
            {
                Name = "RAPL 換算單位",
                Value = s.Units.Text,
                Evidence = $"MSR 0x606 = 0x{s.UnitsRaw:X16}（位 3:0／12:8／19:16，皆為 1/2^n）",
                Severity = Severity.Neutral,
                Note = "下面每一個瓦數與時間窗都是用這三個單位換算出來的；單位錯，全部就錯。",
            }
            : new CeilingRow
            {
                Name = "RAPL 換算單位",
                Value = "讀不到",
                Evidence = "MSR 0x606 = 0x0",
                Severity = Severity.Warning,
                Note = "沒有換算單位就無法把功耗上限的原始計數變成瓦，本頁的功耗欄位全部不顯示。",
            });

        // PL1／PL2
        if (s.Units.Valid && s.PkgLimitRaw != 0)
        {
            rows.Add(CeilingDecoder.DescribePowerLimit("長時功耗上限 PL1",
                CeilingDecoder.DecodePowerLimitHalf((uint)s.PkgLimitRaw, s.Units),
                s.Info, $"MSR 0x610 位 31:0 = 0x{(uint)s.PkgLimitRaw:X8}"));
            rows.Add(CeilingDecoder.DescribePowerLimit("短時功耗上限 PL2",
                CeilingDecoder.DecodePowerLimitHalf((uint)(s.PkgLimitRaw >> 32), s.Units),
                s.Info, $"MSR 0x610 位 63:32 = 0x{(uint)(s.PkgLimitRaw >> 32):X8}"));
            bool locked = CeilingDecoder.PowerLimitLocked(s.PkgLimitRaw);
            rows.Add(new CeilingRow
            {
                Name = "功耗上限鎖定",
                Value = locked ? "已鎖定" : "未鎖定",
                Evidence = "MSR 0x610 位 63",
                Severity = Severity.Neutral,
                Note = locked
                    ? "位 63 = 1：要改 PL1／PL2 必須重開機由韌體重設，任何軟體（包含本工具）都寫不進去。"
                    : "位 63 = 0：BIOS 與作業系統仍可改動這兩個上限。本工具唯讀，不會去改。",
            });
        }
        else
        {
            rows.Add(new CeilingRow
            {
                Name = "封裝功耗上限（PL1／PL2）",
                Value = "讀不到",
                Evidence = $"MSR 0x610 = 0x{s.PkgLimitRaw:X16}",
                Severity = Severity.Warning,
                Note = "本平台沒有可讀的封裝功耗上限暫存器，因此本頁不宣稱有沒有功耗牆。",
            });
        }

        // 封裝自報的功耗規格
        rows.Add(s.Info.Valid
            ? new CeilingRow
            {
                Name = "封裝功耗規格（矽自報）",
                Value = $"TDP {s.Info.TdpW:0} W ・ 可設定範圍 {s.Info.MinW:0}–{s.Info.MaxW:0} W",
                Evidence = $"MSR 0x614 = 0x{s.InfoRaw:X16}（位 14:0／30:16／46:32）",
                Severity = Severity.Neutral,
                Note = "這是這顆矽自己宣告的，不是從規格書抄來的。上面的 PL1／PL2 要跟這裡比，才知道算寬鬆還是算緊。",
            }
            : new CeilingRow
            {
                Name = "封裝功耗規格（矽自報）",
                Value = "本平台不提供",
                Evidence = $"MSR 0x614 = 0x{s.InfoRaw:X16}",
                Severity = Severity.Neutral,
                Note = "0x614 讀到 0：沒有可比的規格值，因此上面的功耗上限只能照原樣呈現，不判斷寬鬆與否。",
            });

        // 核心網域（PP0）功耗上限
        if (s.Pp0LimitRaw == 0)
            rows.Add(new CeilingRow
            {
                Name = "核心網域功耗上限（PP0）",
                Value = "本平台不提供",
                Evidence = "MSR 0x638 = 0x0",
                Severity = Severity.Neutral,
                Note = "讀到 0 代表這顆處理器沒有實作獨立的核心網域功耗上限——不是「上限為 0 W」。",
            });
        else
            rows.Add(new CeilingRow
            {
                Name = "核心網域功耗上限（PP0）",
                Value = $"{(s.Pp0LimitRaw & 0x7FFF) * s.Units.PowerW:0} W"
                      + ((s.Pp0LimitRaw & (1UL << 15)) != 0 ? "（已啟用）" : "（未啟用）"),
                Evidence = $"MSR 0x638 = 0x{s.Pp0LimitRaw:X16}（位 14:0＝上限、位 15＝啟用）",
                Severity = Severity.Neutral,
                Note = "核心網域自己的功耗上限，與封裝級 PL1／PL2 是不同層的牆。"
                     + "時間窗欄位（位 23:17）與封裝級的編碼不同，本工具不套用封裝的公式去猜，故只列上限與啟用位。",
            });

        // 兩個能量計
        rows.Add(new CeilingRow
        {
            Name = "封裝能量計現值",
            Value = $"{(uint)s.EnergyRaw} 計數",
            Evidence = $"MSR 0x611 = 0x{s.EnergyRaw:X16}（32 位元，會回繞）",
            Severity = Severity.Neutral,
            Note = "這是一個只往上加的累計器，單看現值沒有意義；要靠兩次讀取的差分才是功耗。"
                 + "本頁的負載量測會先驗證這顆計數器到底有沒有跟著功耗變化，通過驗證才敢換算成瓦。",
        });
        rows.Add(new CeilingRow
        {
            Name = "記憶體能量計（DRAM 網域）",
            Value = s.DramEnergyRaw == 0 ? "本平台不提供" : $"{(uint)s.DramEnergyRaw} 計數",
            Evidence = $"MSR 0x619 = 0x{s.DramEnergyRaw:X16}",
            Severity = Severity.Neutral,
            Note = s.DramEnergyRaw == 0
                ? "讀到 0 代表本平台沒有開放 DRAM 網域的能量計——不是「記憶體不耗電」。"
                : "DRAM 網域的累計能量；與封裝能量計同樣需要差分才有意義。",
        });

        // 頻率側的硬性上限
        rows.Add(new CeilingRow
        {
            Name = "基頻（最大非渦輪倍頻）",
            Value = s.MaxNonTurbo > 0 && s.BclkMhz > 0
                ? $"{s.MaxNonTurbo}× → {s.MaxNonTurbo * s.BclkMhz:0} MHz"
                : s.MaxNonTurbo > 0 ? $"{s.MaxNonTurbo}×" : "讀不到",
            Evidence = "MSR 0xCE 位 15:8",
            Severity = s.MaxNonTurbo > 0 ? Severity.Neutral : Severity.Warning,
            Note = "低於這個頻率就叫「掉到基頻以下」；PL1 的 clamp 位決定守功耗上限時允不允許掉到這裡以下。",
        });
        rows.Add(new CeilingRow
        {
            Name = "實測 BCLK ／ TSC",
            Value = s.BclkMhz > 0 ? $"{s.BclkMhz:0.00} MHz ／ {s.TscHz / 1e6:0.0} MHz" : "—",
            Evidence = "MSR 0x10 對 QPC 實測（QPC 取讀取前後中點）÷ 基頻倍頻",
            Severity = Severity.Neutral,
            Note = "BCLK 是實測反推的，不假設 100 MHz——超頻過的機器全部倍頻換算都會跟著變。",
        });

        int applicable = CeilingDecoder.ApplicableTurboRatio(s.TurboGroups, s.CoreCount);
        rows.Add(new CeilingRow
        {
            Name = $"{s.CoreCount} 顆核心全開時的倍頻上限",
            Value = applicable > 0
                ? (s.BclkMhz > 0 ? $"{applicable}× → {applicable * s.BclkMhz:0} MHz" : $"{applicable}×")
                : "讀不到倍頻表",
            Evidence = s.TurboGroups.Count > 0
                ? "MSR 0x1AD／0x1AE：" + string.Join("、", s.TurboGroups.Select(g => $"≤{g.Cores} 核 {g.Ratio}×"))
                : "MSR 0x1AD／0x1AE 無可用資料",
            Severity = applicable > 0 ? Severity.Neutral : Severity.Warning,
            Note = s.TurboNote
                 + (applicable > 0 ? " 這個值就是下面判決用的「本來該跑多少」。" : ""),
        });
        rows.Add(new CeilingRow
        {
            Name = "倍頻鎖定",
            Value = s.RatioUnlocked ? "已解鎖" : "鎖定",
            Evidence = "MSR 0xCE 位 28",
            Severity = Severity.Neutral,
            Note = s.RatioUnlocked
                ? "位 28 = 1：倍頻可程式化，所以上面那張倍頻表可能已經被 BIOS 改過，本工具照實顯示現值。"
                : "位 28 = 0：倍頻不可程式化，倍頻表就是原廠設定。",
        });

        // Uncore／Mesh
        if (s.UncoreLimitRaw == 0)
            rows.Add(new CeilingRow
            {
                Name = "Uncore／Mesh 頻率範圍",
                Value = "本平台不提供",
                Evidence = "MSR 0x620 = 0x0",
                Severity = Severity.Neutral,
                Note = "讀到 0 代表沒有可讀的 Uncore 倍頻限制暫存器。",
            });
        else
        {
            int umax = (int)(s.UncoreLimitRaw & 0x7F);
            int umin = (int)((s.UncoreLimitRaw >> 8) & 0x7F);
            int ucur = (int)(s.UncoreStatusRaw & 0x7F);
            rows.Add(new CeilingRow
            {
                Name = "Uncore／Mesh 頻率範圍",
                Value = (umin == umax ? $"固定 {umax}×" : $"{umin}× – {umax}×")
                      + (s.BclkMhz > 0 ? $"（{umin * s.BclkMhz:0}–{umax * s.BclkMhz:0} MHz）" : "")
                      + (ucur > 0 ? $"，目前 {ucur}×" : ""),
                Evidence = $"MSR 0x620 = 0x{s.UncoreLimitRaw:X16}（位 6:0＝最大、位 14:8＝最小）"
                         + (s.UncoreStatusRaw != 0 ? $"、MSR 0x621 = 0x{s.UncoreStatusRaw:X16}（位 6:0＝目前）" : ""),
                Severity = Severity.Neutral,
                Note = umin == umax
                    ? "上下限相同，Uncore／Mesh 被固定在單一頻率：這會影響記憶體與跨核延遲，但不會限制核心倍頻。"
                    : "Uncore／Mesh 允許的頻率範圍。它與核心是不同的頻率網域，核心跑不高不會是它造成的。",
            });
        }

        // 電源控制開關
        bool bdProchot = (s.PowerCtlRaw & 1UL) != 0;
        bool c1e = (s.PowerCtlRaw & 2UL) != 0;
        rows.Add(new CeilingRow
        {
            Name = "雙向 PROCHOT（BD PROCHOT）",
            Value = bdProchot ? "啟用" : "停用",
            Evidence = $"MSR 0x1FC 位 0（原始值 0x{s.PowerCtlRaw:X16}）",
            Severity = Severity.Neutral,
            Note = bdProchot
                ? "啟用時，主機板上的其他元件（VRM、感測器）可以拉低 PROCHOT# 迫使 CPU 降頻。"
                  + "若下面的限制原因出現 PROCHOT# 而 CPU 本身並不燙，就要往這條線查。"
                : "位 0 = 0：外部元件無法透過 PROCHOT# 迫使 CPU 降頻。",
        });
        rows.Add(new CeilingRow
        {
            Name = "C1E 自動升級",
            Value = c1e ? "啟用" : "停用",
            Evidence = $"MSR 0x1FC 位 1（原始值 0x{s.PowerCtlRaw:X16}）",
            Severity = Severity.Neutral,
            Note = c1e
                ? "C1 會被自動升級成 C1E（同時降倍頻與電壓）。省電，但離開閒置時多了一段恢復延遲。"
                : "位 1 = 0：不做 C1E 自動升級，閒置時的恢復較快、耗電較高。",
        });
        rows.Add(new CeilingRow
        {
            Name = "EIST 硬體協調 ／ 能效偏好",
            Value = ((s.MiscPwrRaw & 1UL) != 0 ? "協調已停用" : "協調啟用")
                  + "、" + ((s.MiscPwrRaw & 2UL) != 0 ? "逐核能效偏好已啟用" : "逐核能效偏好未啟用"),
            Evidence = $"MSR 0x1AA 位 0／位 1（原始值 0x{s.MiscPwrRaw:X16}）",
            Severity = Severity.Neutral,
            Note = "位 0 = 1 表示硬體不再跨核協調 P-state，由作業系統各自決定；"
                 + "位 1 = 1 表示可以逐核設定能效偏好（EPB）。"
                 + "這顆暫存器其餘位元 Intel 未在公開文件完整說明，本工具不翻譯，只附原始值供覆核。",
        });

        return rows;
    }

    // ── 卡片二：自開機以來撞過的牆 ──────────────────────────────────────────

    /// <summary>
    /// 黏滯紀錄位的總表。這些位元設起後<b>不會自己歸零</b>，所以涵蓋的是整個開機期間；
    /// 要清掉必須寫入，而寫入會毀掉別的工具的證據，所以本頁不清。
    /// </summary>
    private static List<CeilingRow> BuildHistoryRows(Statics s)
    {
        var rows = new List<CeilingRow>(24);

        uint throttleCounts = (uint)s.ThrottleRaw;
        double throttledSec = CeilingDecoder.ThrottledSeconds(throttleCounts, s.Units.TimeS);
        rows.Add(new CeilingRow
        {
            Name = "封裝功耗節流累計",
            Value = throttleCounts == 0
                ? "0（自開機以來從未累計）"
                : s.Units.Valid ? $"{throttledSec:0.000} 秒（{throttleCounts} 計數）" : $"{throttleCounts} 計數",
            Evidence = $"MSR 0x613 = 0x{s.ThrottleRaw:X16}（累計 × RAPL 時間單位）",
            Severity = throttleCounts == 0 ? Severity.Good : Severity.Warning,
            Note = throttleCounts == 0
                ? "這顆累計器從開機到現在一次都沒動：封裝從未因為功耗上限被降頻過。"
                : "這是自開機以來被封裝功耗上限壓住的總時間。它只往上加，本工具不清它。",
        });

        if (s.ReasonMsr == 0)
        {
            rows.Add(new CeilingRow
            {
                Name = "限制原因暫存器",
                Value = "本機無可用暫存器",
                Evidence = "0x64F 與 0x690 皆無值",
                Severity = Severity.Warning,
                Note = "少了這顆暫存器，「是誰在限制你」只能靠溫度與功耗計數旁證，無法直接指認。",
            });
        }
        else
        {
            rows.AddRange(CeilingDecoder.DescribeReasonRows(s.ReasonRaw));
            string undoc = CeilingDecoder.UndocumentedText(s.ReasonRaw);
            if (undoc.Length > 0)
                rows.Add(new CeilingRow
                {
                    Name = "未列於文件的位元",
                    Value = "有動作",
                    Evidence = $"MSR 0x{s.ReasonMsr:X3} = 0x{s.ReasonRaw:X16}",
                    Severity = Severity.Neutral,
                    Note = undoc + " turbostat 之類的工具同樣不替這些位元命名。",
                });
        }

        if (CeilingDecoder.ThermSanity(s.PkgThermRaw, "MSR 0x1B1") is { } sanity) rows.Add(sanity);
        else
        {
            var readout = CeilingDecoder.DecodeThermReadout(s.PkgThermRaw, s.Target.TccC);
            rows.Add(new CeilingRow
            {
                Name = "封裝目前溫度",
                Value = readout.TempKnown ? $"{readout.TempC} °C" : "—",
                Evidence = $"MSR 0x1B1 = 0x{s.PkgThermRaw:X16}（位 22:16＝低於 TCC {readout.DigitalReadout} 度、"
                         + $"位 30:27＝解析度 {readout.ResolutionC} °C、位 31＝讀值{(readout.ReadingValid ? "有效" : "無效")}）",
                Severity = Severity.Neutral,
                Note = readout.TempKnown
                    ? $"溫度是「節流點 {s.Target.TccC} °C 減去數位讀數 {readout.DigitalReadout}」算出來的，"
                      + $"解析度 {readout.ResolutionC} °C——這個讀數本身就只有這個精度，不是本工具四捨五入掉的。"
                    : "TCC 活化溫度未知或讀值無效，因此不換算成攝氏。",
            });
            rows.AddRange(CeilingDecoder.DescribeThermPairs(s.PkgThermRaw, "MSR 0x1B1"));
        }

        return rows;
    }

    // ── 卡片三：負載對照量測 ────────────────────────────────────────────────

    /// <summary>單一量測窗的共用參數。</summary>
    private sealed record Ctx
    {
        public required WinRing0Bridge Bridge { get; init; }
        public required IReadOnlyList<Core> Cores { get; init; }
        public uint ReasonMsr { get; init; }
        public int TccC { get; init; }
        public double TscHz { get; init; }
        public int LoadThreads { get; init; }
        public int DurationSec { get; init; }
    }

    /// <summary>
    /// 跑一個量測窗：壓上指定負載 → 沉降 → 取起始值 → 窗內取樣 → 取結束值 → 收掉負載。
    /// </summary>
    /// <remarks>
    /// 順序是刻意的：<b>逐核掃描全部在負載還在跑的時候做完</b>——負載一停，溫度與限制原因立刻開始回落，
    /// 掃到的就不是這個窗的狀態了。限制原因是逐核暫存器，取樣期間把取樣執行緒<b>釘在一顆有負載的核心上</b>，
    /// 釘在閒置核心上讀只會永遠讀到「什麼都沒發生」。
    /// </remarks>
    private static CeilingWindow RunWindow(Ctx c, string label, CeilingLoad kind,
        IProgress<(double, string)> report, double progFrom, double progTo, CancellationToken ct)
    {
        using var load = new LoadGenerator(kind, c.LoadThreads);
        string loadNote = kind switch
        {
            CeilingLoad.Baseline => "不製造負載（對照基線）",
            CeilingLoad.Integer => $"{load.ThreadCount} 條整數乘加執行緒（一般優先權）",
            CeilingLoad.Avx2 => $"{load.ThreadCount} 條 256 位元浮點乘法執行緒（一般優先權）",
            CeilingLoad.Avx512 => $"{load.ThreadCount} 條 512 位元浮點乘法執行緒（一般優先權）",
            _ => "",
        };

        report.Report((progFrom, $"{label}：沉降 {SettleMs} ms（等頻率授權等級與功耗控制環穩定）…"));
        Sleep(SettleMs, ct);

        int n = c.Cores.Count;
        var reasonStart = SweepPerCore(c.Bridge, c.Cores, c.ReasonMsr);
        ulong e0 = Read(c.Bridge, MsrPkgEnergyStatus);
        ulong t0 = Read(c.Bridge, MsrPkgPerfStatus);
        ulong pkg0 = Read(c.Bridge, MsrPkgThermStatus);

        var m0 = new ulong[n]; var a0 = new ulong[n]; var ok0 = new bool[n];
        var m1 = new ulong[n]; var a1 = new ulong[n]; var ok1 = new bool[n];
        PerfPass(c.Bridge, c.Cores, m0, a0, ok0);

        var sw = Stopwatch.StartNew();
        ulong union = 0;
        int samples = 0, failed = 0, pkgMax = 0;
        bool pkgKnown = false;
        Exception? lastEx = null;
        {
            using var pin = CpuAffinity.Pinned(c.Cores[0].First);
            while (sw.Elapsed.TotalSeconds < c.DurationSec)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (c.ReasonMsr != 0) union |= (c.Bridge.ReadMsrPair64(c.ReasonMsr) ?? 0) & 0xFFFFUL;
                    var tr = CeilingDecoder.DecodeThermReadout(c.Bridge.ReadMsrPair64(MsrPkgThermStatus) ?? 0, c.TccC);
                    if (tr.TempKnown) { pkgKnown = true; if (tr.TempC > pkgMax) pkgMax = tr.TempC; }
                    samples++;
                }
                catch (Exception ex) { lastEx = ex; failed++; }
                double left = c.DurationSec - sw.Elapsed.TotalSeconds;
                report.Report((progFrom + (progTo - progFrom) * Math.Clamp(sw.Elapsed.TotalSeconds / c.DurationSec, 0, 1),
                    $"{label}：量測中，還有 {Math.Max(0, left):0.0} 秒"
                    + (pkgKnown ? $"（封裝 {pkgMax} °C）" : "")));
                Thread.Sleep(SampleMs);
            }
        }
        if (failed > 0) Diag.Swallow("窗內取樣讀取 MSR", lastEx, $"{failed} 次取樣失敗，未列入統計");

        // 結束值：MPERF／APERF 先取（窗長以它為準），再趁負載還在跑掃逐核溫度與限制原因。
        ulong e1 = Read(c.Bridge, MsrPkgEnergyStatus);
        ulong t1 = Read(c.Bridge, MsrPkgPerfStatus);
        ulong pkg1 = Read(c.Bridge, MsrPkgThermStatus);
        PerfPass(c.Bridge, c.Cores, m1, a1, ok1);
        double sec = sw.Elapsed.TotalSeconds;
        var thermEnd = SweepPerCore(c.Bridge, c.Cores, MsrThermStatus);
        var reasonEnd = SweepPerCore(c.Bridge, c.Cores, c.ReasonMsr);

        // 有效倍頻（相對 TSC）與「這顆核心在窗內醒著多久」。
        // MPERF 只在核心處於 C0 時以 TSC 速率前進，所以 ΔMPERF ÷ (TSC 速率 × 窗長) 就是醒著的比例，
        // 這是真的量出來的作用中核心數，不是拿執行緒數去猜的。
        double sumRatio = 0;
        int counted = 0, active = 0;
        for (int i = 0; i < n; i++)
        {
            if (!ok0[i] || !ok1[i]) continue;
            double r = FrequencyTruthMath.AperfMperfRatio(m0[i], m1[i], a0[i], a1[i]);
            if (r <= 0) continue;
            sumRatio += r;
            counted++;
            double awake = c.TscHz > 0 && sec > 0 ? (m1[i] - m0[i]) / (c.TscHz * sec) : 0;
            if (awake >= AwakeThreshold) active++;
        }
        double meanRatio = counted > 0 ? sumRatio / counted : 0;

        int maxCore = 0;
        bool coreTempKnown = false;
        for (int i = 0; i < n; i++)
        {
            var tr = CeilingDecoder.DecodeThermReadout(thermEnd[i], c.TccC);
            if (!tr.TempKnown) continue;
            coreTempKnown = true;
            if (tr.TempC > maxCore) maxCore = tr.TempC;
        }

        // 黏滯紀錄位的「這段期間才新亮起的」＝末值 & ~起始值，逐核取聯集。不需要任何寫入就能得到。
        ulong newly = 0;
        for (int i = 0; i < n; i++) newly |= reasonEnd[i] & ~reasonStart[i];
        newly &= 0xFFFF0000UL;

        var pkgEnd = CeilingDecoder.DecodeThermReadout(pkg1, c.TccC);
        int pkgTemp = pkgKnown ? pkgMax : pkgEnd.TempKnown ? pkgEnd.TempC : 0;

        return new CeilingWindow
        {
            Label = label,
            Seconds = sec,
            MeanRatio = meanRatio,
            MeanMhz = meanRatio * c.TscHz / 1e6,
            MaxCoreTempC = maxCore,
            PkgTempC = pkgTemp,
            TempKnown = coreTempKnown || pkgKnown || pkgEnd.TempKnown,
            EnergyCounts = CeilingDecoder.Delta32(e0, e1),
            ThrottleCounts = CeilingDecoder.Delta32(t0, t1),
            ReasonStatusUnion = union,
            ReasonNewlyLogged = newly,
            PkgThermNewlyLogged = pkg1 & ~pkg0,
            Samples = samples,
            ActiveCores = active,
            CoresMeasured = counted,
            LoadNote = loadNote,
        };
    }

    // ── 主流程 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 一次完整量測。<paramref name="withLoad"/> 為 false 時只做靜態讀取（不製造負載、也不給判決）。
    /// </summary>
    /// <remarks>
    /// 窗的順序是<b>基線 → 整數 → AVX2 → AVX-512</b>，中間都插冷卻，理由各不相同：
    /// 基線給能量計一個靜止對照（否則沒辦法判斷 0x611 到底有沒有在反映功耗）；
    /// 整數窗是向量降頻的對照組（同樣壓滿核心，但完全不碰向量單元）；
    /// 越寬的向量放越後面，因為它最熱，讓它去撞溫度牆。
    /// 硬體不支援的指令集<b>整個窗跳過</b>，而不是跑一個假的然後標記「不支援」。
    /// </remarks>
    private Result Measure(bool withLoad, int durationSec, IProgress<(double, string)> report, CancellationToken ct)
    {
        if (!X86Base.IsSupported)
            return new Result { Error = "本頁需要 x86 的 MSR 與 APERF／MPERF 計數器，此平台不提供。" };

        using var bridge = WinRing0Bridge.Create();
        if (bridge is null || !bridge.Available)
            return new Result { Error = "MSR 橋接不可用，無法讀取硬性上限：" + (bridge?.Error ?? "未知原因") };

        var lps = CpuAffinity.AllLogicalProcessors();
        bool multiGroup = CpuAffinity.IsMultiGroup;
        ulong mask;
        try { mask = (ulong)Process.GetCurrentProcess().ProcessorAffinity.ToInt64(); }
        catch (Exception ex)
        {
            Diag.Swallow("讀取行程親和性", ex, "改以韌體回報的完整遮罩列舉實體核心");
            mask = ulong.MaxValue;
        }

        var cores = CpuAffinity.PhysicalCores(multiGroup, mask)
                               .Select(t => new Core(t.Core, t.First, t.LpText))
                               .ToList();
        if (cores.Count == 0)
            return new Result
            {
                Error = "取不到實體核心拓撲（GetLogicalProcessorInformationEx 沒有可用資料）。"
                      + "本頁不拿邏輯處理器數硬湊核心數——SMT 會讓每一顆核心被算兩次，倍頻表也就查錯格。",
            };

        report.Report((0.04, "讀取硬性上限：溫度目標、RAPL 單位與功耗上限、倍頻表、限制原因暫存器…"));
        var s = ReadStatics(bridge, cores, lps.Count);
        var limits = BuildLimitRows(s);
        var history = BuildHistoryRows(s);
        string scale = $"{cores.Count} 顆實體核心／{lps.Count} 個邏輯處理器"
                     + (multiGroup ? $"（{CpuAffinity.GroupCount} 個處理器群組，此路徑未在實機驗證過）" : "");

        if (!withLoad)
            return new Result
            {
                Limits = limits,
                History = history,
                ReasonRegText = s.ReasonRegText,
                StatusText = $"靜態讀取完成 ・ {scale} ・ 全程唯讀，未寫入任何位元。"
                           + "按「開始撞牆量測」才會自己製造負載，去看實際撞到哪一面牆。",
            };
        // 留兩個邏輯處理器給取樣執行緒與作業系統：實測壓滿全部邏輯處理器時，
        // 9.08 秒的窗只取到 3 個樣本——取樣執行緒自己被排程餓死，量到的溫度曲線就是假的。
        int loadThreads = Math.Max(1, lps.Count - 2);
        var ctx = new Ctx
        {
            Bridge = bridge,
            Cores = cores,
            ReasonMsr = s.ReasonMsr,
            TccC = s.Target.TccC,
            TscHz = s.TscHz,
            LoadThreads = loadThreads,
            DurationSec = durationSec,
        };

        var plan = new List<(string Label, CeilingLoad Kind)>
        {
            ("基線（不製造負載）", CeilingLoad.Baseline),
            ("整數負載（全核）", CeilingLoad.Integer),
        };
        if (Avx2.IsSupported) plan.Add(("AVX2 256 位元浮點（全核）", CeilingLoad.Avx2));
        if (Avx512F.IsSupported) plan.Add(("AVX-512 512 位元浮點（全核）", CeilingLoad.Avx512));

        var windows = new List<CeilingWindow>(plan.Count);
        for (int i = 0; i < plan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            double from = 0.08 + 0.88 * i / plan.Count;
            double to = 0.08 + 0.88 * (i + 1) / plan.Count;
            if (i > 0)
            {
                report.Report((from, $"冷卻 {CooldownMs} ms，讓溫度與功耗回落，下一窗才不是接著餘熱在跑…"));
                Sleep(CooldownMs, ct);
            }
            windows.Add(RunWindow(ctx, plan[i].Label, plan[i].Kind, report, from, to, ct));
        }

        // 分辨三種角色的窗：基線（能量計對照）、整數（向量降頻對照）、最寬向量（最熱、最可能撞牆）。
        CeilingWindow baseline = windows[0], intWin = windows[0], heavy = windows[^1];
        for (int i = 0; i < plan.Count; i++)
            if (plan[i].Kind == CeilingLoad.Integer) intWin = windows[i];

        var (trust, energyText) = CeilingDecoder.ValidateEnergyCounter(
            baseline.EnergyRateCps, heavy.EnergyRateCps, baseline.PkgTempC, heavy.PkgTempC,
            baseline.TempKnown && heavy.TempKnown);
        if (trust && s.Units.Valid)
            energyText += " 各窗換算：" + string.Join("、", windows.Select(
                w => $"{w.Label} {CeilingDecoder.Watts(w.EnergyCounts, s.Units.EnergyJ, w.Seconds):0.0} W"));
        // 判決的證據：全部來自上面幾個窗的實測，沒有一項來自規格書。
        // 目標倍頻用「實測到的作用中核心數」去查倍頻表——用執行緒數去查會查錯格。
        ulong newlyOr = 0;
        for (int i = 1; i < windows.Count; i++) newlyOr |= windows[i].ReasonNewlyLogged;

        double bclk = s.BclkMhz;
        var evidence = new CeilingDecoder.CeilingEvidence
        {
            TargetRatio = CeilingDecoder.ApplicableTurboRatio(s.TurboGroups, heavy.ActiveCores),
            AchievedRatio = bclk > 0 ? heavy.MeanMhz / bclk : 0,
            BclkMhz = bclk,
            ActiveCores = heavy.ActiveCores,
            MaxTempC = heavy.MaxCoreTempC,
            ThrottleAtC = s.Target.ThrottleAtC,
            TempKnown = heavy.TempKnown,
            ThrottledSec = CeilingDecoder.ThrottledSeconds(heavy.ThrottleCounts, s.Units.TimeS),
            WindowSec = heavy.Seconds,
            NewReasons = CeilingDecoder.LoggedNames(newlyOr),
            PowerWallDisabled = CeilingDecoder.PowerWallAbsent(s.PkgLimitRaw, s.Units, s.Info),
            AvxRatioDrop = ReferenceEquals(intWin, heavy) || bclk <= 0
                ? 0
                : (intWin.MeanMhz - heavy.MeanMhz) / bclk,
            WidestVectorLabel = ReferenceEquals(intWin, heavy) ? "" : heavy.Label,
        };
        var (sev, headline, detail) = CeilingDecoder.Verdict(evidence);

        return new Result
        {
            Limits = limits,
            History = history,
            Windows = windows,
            ReasonRegText = s.ReasonRegText,
            EnergyText = energyText,
            HasVerdict = true,
            VerdictSeverity = sev,
            VerdictHeadline = headline,
            VerdictDetail = detail,
            StatusText = $"量測完成 ・ {scale} ・ {windows.Count} 個窗 × {durationSec} 秒"
                       + $"（每窗另有 {SettleMs} ms 沉降、窗間 {CooldownMs} ms 冷卻）・ "
                       + $"負載執行緒 {loadThreads} 條已全部結束 ・ 全程唯讀，黏滯紀錄位一個都沒清掉。",
        };
    }
}
