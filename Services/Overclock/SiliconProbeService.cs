using System.Diagnostics;
using System.Threading;

namespace XinSpect;

/// <summary>取樣進度：0–1 的完成比例與目前階段的說明。</summary>
public sealed record SiliconProbeProgress(double Fraction, string Phase);

/// <summary>
/// 體質特徵化的實機取樣：用負載階梯讓處理器自己走過睿頻表的各段，逐段記下一個 V/F 工作點。
/// </summary>
/// <remarks>
/// <b>為什麼不寫入硬體。</b>正式的 VF 特徵化是逐階寫倍頻、逐階找最低穩定電壓；那要花數十分鐘，
/// 而且每一步都可能當機。這裡走另一條路：<b>同時活躍的核心數本身就是睿頻表的索引</b>
/// （MSR 0x1AD 對 1、2、3…核各給一個倍頻上限），所以只要控制負載的核心數，處理器就會自己
/// 沿著原廠 VF 曲線移動，而我們只要讀。全程唯讀，不寫任何倍頻或電壓。
///
/// <b>幾個刻意的方法選擇。</b>
/// <list type="bullet">
/// <item><b>階梯順序刻意交錯</b>（1、全核、2、次多…）而不是由小到大。由小到大時溫度會隨核心數
/// 單調上升，而頻率隨核心數單調下降——兩者完全相關，事後根本分不出斜率裡有多少是漏電隨溫度
/// 變化貢獻的。交錯下去就把溫度這個共變數和核心數這個因子解相關了。</item>
/// <item><b>負載核心只挑實體核心的第一個邏輯處理器</b>。直接開 N 條執行緒讓 Windows 自己排，
/// SMT 兄弟會被算成兩顆核，睿頻表的索引就對不上了。</item>
/// <item><b>負載核心一律用同一支純量浮點核心</b>。AVX-256／512 會觸發授權降頻與大得多的電流，
/// 各階用不同指令集等於每一階量的是不同條曲線。</item>
/// <item><b>取樣執行緒釘在第一顆實體核心</b>——那一顆在每一階都滿載，讀到的 VID 與 APERF/MPERF
/// 才對得上該階的頻率。釘在閒置核上讀，量到的是那顆核在睡覺。</item>
/// </list>
/// </remarks>
public static class SiliconProbeService
{
    private const uint MsrTsc = 0x10;
    private const uint MsrPlatformInfo = 0xCE;          // 位 15:8 ＝ 原廠非睿頻倍頻（P1）
    private const uint MsrMperf = 0xE7;
    private const uint MsrAperf = 0xE8;
    private const uint MsrPerfStatus = 0x198;           // 位 47:32 ÷ 8192 ＝ 目前核心電壓
    private const uint MsrTempTarget = 0x1A2;           // 位 23:16 ＝ TjMax
    private const uint MsrTurboRatioLimit = 0x1AD;      // 1～8 核各自的倍頻上限（每 8 位一格）
    private const uint MsrPkgThermStatus = 0x1B1;       // 位 22:16 ＝ 距 TjMax 幾度
    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrPkgEnergyStatus = 0x611;

    private const double ThermalAbortC = 95;            // 超過就收工：量到的已經是溫度牆下的曲線
    private const double VoltFloorV = 0.30;             // MSR 0x198 的電壓欄位健全性下界
    private const double VoltCeilV = 2.20;              // 上界；落在區間外視為此平台未實作該欄位

    /// <summary>感測器補位讀值（MSR 不可用時用）。由呼叫端提供，本服務因此不依賴 UI 層。</summary>
    public sealed record SensorFallback(
        Func<double?> Volt, Func<double> ClockMhz, Func<double?> TempC, Func<double?> PowerW);

    public sealed record Options
    {
        public SensorFallback? Sensors { get; init; }
        /// <summary>使用者是否已套用電壓覆寫／偏移（由 OverclockService 判斷後傳入）。</summary>
        public bool ManualVoltage { get; init; }
        public string ManualVoltageNote { get; init; } = "";
    }

    /// <summary>整趟取樣共用的環境（唯讀素材 ＋ 可用的讀值來源）。</summary>
    private sealed class Ctx
    {
        public required WinRing0Bridge Bridge { get; init; }
        public required List<(int Core, ProcessorRef First, string LpText)> Cores { get; init; }
        public ProcessorRef ProbeLp { get; init; }
        public SensorFallback? Sensors { get; init; }
        public double TscHz { get; init; }
        public int TjMax { get; init; }
        public RaplUnits Rapl { get; init; }
        public bool VoltFromMsr { get; init; }
        public bool FreqFromMsr { get; init; }
        public bool TempFromMsr { get; init; }
        public bool PowerFromMsr { get; init; }
        /// <summary>感測器補位時階梯要拉長：感測器約一秒才換一次值，短窗會拿到同一筆重複值。</summary>
        public int SettleMs => VoltFromMsr ? 900 : 1300;
        public int MeasureMs => VoltFromMsr ? 1500 : 2700;
        public int SampleMs => VoltFromMsr ? 40 : 200;
    }

    private sealed record StepResult(double FreqGhz, double? VoltV, double? TempC, double? PowerW, int Samples);

    // ═══════════════════════════════════════════════════════════════════════
    // 主流程
    // ═══════════════════════════════════════════════════════════════════════

    public static SiliconAssessment Run(Options opt, IProgress<SiliconProbeProgress>? progress, CancellationToken ct)
    {
        // 量測期間讓動畫停下來：重繪要花 GPU 與封裝功耗，而封裝功耗會回頭壓低頻率、
        // 抬高溫度，直接進到 V/F 取樣裡。跑分那幾支早就這樣做了，這裡沿用同一個機制。
        using var quiet = Motion.Suspend();

        void Report(double f, string p) => progress?.Report(new SiliconProbeProgress(f, p));

        var cores = EnumerateCores();
        if (cores.Count == 0)
            return SiliconQuality.Evaluate(new SiliconInput
            {
                Aborted = true,
                AbortReason = "列不出實體核心拓撲，無法建立負載階梯，因此不量。",
            });

        // 混合架構下要先釘到第一顆實體核心才問 CPUID：核型（大核／小核）決定該用哪一條參考線。
        var kind = CoreKind.Unknown;
        var uarch = IdentifyUarch(cores[0].First, ref kind);

        using var bridge = WinRing0Bridge.Create();
        var ctx = BuildContext(bridge, cores, opt.Sensors);

        if (!ctx.VoltFromMsr && opt.Sensors?.Volt() is not > 0)
            return SiliconQuality.Evaluate(new SiliconInput
            {
                Uarch = uarch, Kind = kind, Aborted = true,
                VoltSource = "無可用來源",
                AbortReason = "讀不到核心電壓：MSR 0x198 不可用（需要以系統管理員身分執行）"
                    + (bridge.Available ? "" : $"——{bridge.Error}")
                    + "，感測器也沒有 Vcore。沒有電壓就沒有 V/F 曲線可談，故不量。",
            });

        var plan = BuildPlan(cores.Count, ctx.VoltFromMsr);
        var pts = new List<VfPoint>();
        double tMin = double.MaxValue, tMax = double.MinValue;
        string abort = "";

        // 閒置基線：把靜態功耗（漏電 ＋ Uncore）跟動態功耗分開，才有辦法反解有效切換電容。
        // 這一階刻意把取樣間隔拉長：每 40 ms 醒一次自己就會把封裝叫著，量到的會是「有活動的閒置」。
        Report(0.02, "量閒置基線（讓機器靜下來）");
        var idleStep = RunStep(ctx, 0, ctx.SettleMs, ctx.MeasureMs, ctx.SampleMs * 5, ct);
        Track(idleStep.TempC);

        int total = plan.Count + 1;
        for (int i = 0; i < plan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            int n = plan[i];
            Report((i + 1.0) / total, $"負載階梯 {i + 1}／{plan.Count}：{n} 顆實體核心滿載");

            var s = RunStep(ctx, n, ctx.SettleMs, ctx.MeasureMs, ctx.SampleMs, ct);
            Track(s.TempC);
            if (s.VoltV is double v && s.FreqGhz > 0)
                pts.Add(new VfPoint(n, s.FreqGhz, v, s.TempC, s.PowerW, s.Samples));

            if (s.TempC is double tc && tc >= ThermalAbortC)
            {
                abort = $"第 {i + 1} 階時封裝溫度達 {tc:0} °C（安全上限 {ThermalAbortC:0} °C），"
                      + "已提前收工。溫度牆下量到的是被壓低的曲線，剩下的階梯不會更可信。";
                break;
            }
        }

        Report(1, "擬合與評定");
        double drift = tMax > tMin ? tMax - tMin : 0;
        var stock = ReadStockTurbo(ctx);

        return SiliconQuality.Evaluate(new SiliconInput
        {
            Points = pts,
            Uarch = uarch,
            Kind = kind,
            IdlePowerW = idleStep.PowerW,
            IdleVoltV = idleStep.VoltV,
            IdleTempC = idleStep.TempC,
            TempDriftC = drift,
            MaxTempC = tMax > double.MinValue ? tMax : null,
            VoltSource = ctx.VoltFromMsr
                ? "MSR 0x198 位 47:32 ÷ 8192（IA32_PERF_STATUS，核心自己要求的電壓）"
                : "感測器 Vcore（LibreHardwareMonitor）",
            FreqSource = ctx.FreqFromMsr
                ? $"MSR 0xE7／0xE8（APERF÷MPERF）× 實測 TSC {ctx.TscHz / 1e9:0.000} GHz"
                : "感測器逐核最高時脈（LibreHardwareMonitor）",
            PowerSource = ctx.PowerFromMsr
                ? "MSR 0x611 封裝能量計 × MSR 0x606 單位（RAPL）"
                : "感測器封裝功耗",
            TempSource = ctx.TempFromMsr
                ? $"MSR 0x1B1 數位讀數 ＋ MSR 0x1A2（TjMax {ctx.TjMax} °C）"
                : "感測器封裝溫度",
            VoltFromMsr = ctx.VoltFromMsr,
            ManualVoltage = opt.ManualVoltage,
            ManualVoltageNote = opt.ManualVoltageNote,
            StockAllCoreGhz = stock.Ghz,
            StockTurboLabel = stock.Label,
            BaseClockMhz = stock.BaseClockMhz,
            Aborted = abort.Length > 0,
            AbortReason = abort,
        });

        void Track(double? t)
        {
            if (t is not double v) return;
            if (v < tMin) tMin = v;
            if (v > tMax) tMax = v;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 一階負載 ＋ 一個取樣窗
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 跑一階：起 <paramref name="activeCores"/> 條釘選的滿載執行緒，沉澱後在同一顆邏輯處理器上
    /// 取一個窗。頻率由窗頭窗尾的 APERF／MPERF 差值算，不是取瞬時讀值的平均——瞬時值抓不到
    /// 窗內的降頻與停頓，差值天生就是「這段時間的平均有效頻率」。
    /// </summary>
    private static StepResult RunStep(Ctx c, int activeCores, int settleMs, int measureMs, int sampleMs,
                                      CancellationToken ct)
    {
        using var load = new LoadBank(c.Cores, activeCores);
        Sleep(settleMs, ct);

        // 起訖計數器必須在同一顆邏輯處理器上讀，否則兩端來自不同核心，差值毫無意義。
        using var pin = CpuAffinity.Pinned(c.ProbeLp);

        ulong m0 = 0, a0 = 0, e0 = 0;
        bool counters = false;
        if (pin.Ok && c.FreqFromMsr)
        {
            var m = Read(c.Bridge, MsrMperf);
            var a = Read(c.Bridge, MsrAperf);
            if (m is not null && a is not null) { m0 = m.Value; a0 = a.Value; counters = true; }
        }
        if (c.PowerFromMsr) e0 = Read(c.Bridge, MsrPkgEnergyStatus) ?? 0;
        long q0 = Stopwatch.GetTimestamp();

        double vMin = double.MaxValue, tSum = 0;
        int tN = 0, samples = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < measureMs)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadVolt(c) is double v && v > 0)
            {
                if (v < vMin) vMin = v;
                samples++;
            }
            if (ReadTemp(c) is double t) { tSum += t; tN++; }
            int left = measureMs - (int)sw.ElapsedMilliseconds;
            if (left <= 0) break;
            Thread.Sleep(Math.Max(1, Math.Min(sampleMs, left)));
        }

        long q1 = Stopwatch.GetTimestamp();
        double secs = (q1 - q0) / (double)Stopwatch.Frequency;

        double ghz = 0;
        if (counters)
        {
            var m1 = Read(c.Bridge, MsrMperf);
            var a1 = Read(c.Bridge, MsrAperf);
            if (m1 is not null && a1 is not null && m1.Value > m0 && a1.Value > a0)
                ghz = (a1.Value - a0) / (double)(m1.Value - m0) * c.TscHz / 1e9;
        }
        if (ghz <= 0) ghz = (c.Sensors?.ClockMhz() ?? 0) / 1000.0;

        double? watts = null;
        if (c.PowerFromMsr && secs > 0)
        {
            // 0x611 是 32 位元計數器、會回繞；取無號差值就自然處理了繞回。
            uint d = (uint)((Read(c.Bridge, MsrPkgEnergyStatus) ?? 0) - e0);
            if (d > 0) watts = d * c.Rapl.EnergyJ / secs;
        }
        watts ??= c.Sensors?.PowerW();

        return new StepResult(ghz, vMin < double.MaxValue ? vMin : null,
                              tN > 0 ? tSum / tN : c.Sensors?.TempC(), watts, samples);
    }

    /// <summary>
    /// 一組釘在指定實體核心上的滿載執行緒。刻意用純量浮點：AVX-256／512 會觸發授權降頻與大得多
    /// 的電流，各階若用到不同指令集，量到的就不是同一條 VF 曲線，斜率會出現階梯狀跳動。
    /// </summary>
    private sealed class LoadBank : IDisposable
    {
        private static double _sink;                    // 防止 JIT 把整段運算最佳化掉
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Thread> _threads = [];

        public LoadBank(List<(int Core, ProcessorRef First, string LpText)> cores, int count)
        {
            var token = _cts.Token;
            for (int i = 0; i < count && i < cores.Count; i++)
            {
                var lp = cores[i].First;
                var t = new Thread(() =>
                {
                    using var pin = CpuAffinity.Pinned(lp);
                    double acc = 0;
                    while (!token.IsCancellationRequested) acc += Kernel(200_000);
                    Volatile.Write(ref _sink, acc);
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Normal,   // 一般優先權：取樣執行緒與介面仍搶得到時間
                    Name = $"XinVf#{i}",
                };
                _threads.Add(t);
                t.Start();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            foreach (var t in _threads) t.Join(2000);
            _cts.Dispose();
        }

        private static double Kernel(int iters)
        {
            double a = 1.0000001, acc = 0;
            for (int i = 0; i < iters; i++)
            {
                acc += a * a - a + 1.0 / (a + 1.0);
                a += 1e-7;
            }
            return acc;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 前置與素材
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 建出負載階梯的順序。低核心數逐一取（1、2、3、4），之後按倍數跳——睿頻表在低核心數那一段
    /// 每格都不同，高核心數那一段通常早就一路平到全核，多取只是多花時間與多發熱。
    ///
    /// 順序刻意<b>由兩端往中間交錯</b>（1、全核、2、次多…）。若由小到大跑，溫度會隨核心數單調上升
    /// 而頻率單調下降，兩者完全相關，事後就分不出擬合斜率裡有多少其實是漏電隨溫度變化貢獻的。
    /// 交錯之後溫度這個共變數就跟核心數這個因子解相關了。
    /// </summary>
    private static List<int> BuildPlan(int coreCount, bool msrFast)
    {
        int cap = msrFast ? 8 : 6;                  // 走感測器時每階要 4 秒，階數得少一點
        var asc = new List<int>();
        foreach (int n in new[] { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64 })
            if (n <= coreCount) asc.Add(n);
        if (coreCount > 0 && !asc.Contains(coreCount)) asc.Add(coreCount);
        // 超出上限時從中段抽掉：兩端（單核與全核）一定留著，那是頻率跨距的兩個端點。
        while (asc.Count > cap) asc.RemoveAt(asc.Count / 2);

        var order = new List<int>();
        for (int lo = 0, hi = asc.Count - 1; lo <= hi; lo++, hi--)
        {
            order.Add(asc[lo]);
            if (hi != lo) order.Add(asc[hi]);
        }
        return order;
    }

    private static List<(int Core, ProcessorRef First, string LpText)> EnumerateCores()
    {
        ulong mask;
        try { mask = (ulong)Process.GetCurrentProcess().ProcessorAffinity.ToInt64(); }
        catch (Exception ex)
        {
            Diag.Swallow("讀取行程親和性", ex, "改以韌體回報的完整遮罩列舉實體核心");
            mask = ulong.MaxValue;
        }
        return CpuAffinity.PhysicalCores(CpuAffinity.IsMultiGroup, mask);
    }

    /// <summary>問 CPUID 判斷微架構與核型。非 Intel Family 6 一律回未知，之後就不會給百分位。</summary>
    private static MicroarchInfo IdentifyUarch(ProcessorRef lp, ref CoreKind kind)
    {
        try
        {
            if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported) return MicroarchProfile.Unknown;
            using var pin = CpuAffinity.Pinned(lp);

            var v = System.Runtime.Intrinsics.X86.X86Base.CpuId(0, 0);
            string vendor = string.Concat(
                System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(v.Ebx)),
                System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(v.Edx)),
                System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(v.Ecx)));
            if (vendor != "GenuineIntel") return MicroarchProfile.Unknown;

            var one = System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0);
            var (family, model) = MicroarchProfile.DecodeSignature((uint)one.Eax);
            kind = (uint)v.Eax >= 0x1A
                ? MicroarchProfile.CoreKindFromCpuid1A((uint)System.Runtime.Intrinsics.X86.X86Base.CpuId(0x1A, 0).Eax)
                : CoreKind.Unknown;
            return MicroarchProfile.Identify(family, model, kind);
        }
        catch (Exception ex)
        {
            Diag.Swallow("CPUID 判斷微架構", ex, "體質特徵化只給實測值、不給百分位");
            return MicroarchProfile.Unknown;
        }
    }

    /// <summary>逐項試讀，確定每個量到底走 MSR 還是走感測器補位。每一項都會如實回報出處。</summary>
    private static Ctx BuildContext(WinRing0Bridge bridge,
                                    List<(int Core, ProcessorRef First, string LpText)> cores,
                                    SensorFallback? sensors)
    {
        var probe = cores[0].First;
        double tsc = 0;
        int tj = 0;
        RaplUnits rapl = default;
        bool volt = false, freq = false, temp = false, power = false;

        if (bridge.Available)
        {
            using (var pin = CpuAffinity.Pinned(probe))
            {
                if (pin.Ok)
                {
                    tsc = MeasureTscHz(bridge);
                    freq = tsc > 0 && Read(bridge, MsrMperf) is not null && Read(bridge, MsrAperf) is not null;
                    volt = DecodeVolt(Read(bridge, MsrPerfStatus)) is not null;
                }
            }
            tj = DecodeTjMax(Read(bridge, MsrTempTarget));
            temp = tj > 0 && DecodeTemp(Read(bridge, MsrPkgThermStatus), tj) is not null;
            rapl = CeilingDecoder.DecodeRaplUnits(Read(bridge, MsrRaplPowerUnit) ?? 0);
            power = rapl.Valid && Read(bridge, MsrPkgEnergyStatus) is > 0;
        }

        return new Ctx
        {
            Bridge = bridge, Cores = cores, ProbeLp = probe, Sensors = sensors,
            TscHz = tsc, TjMax = tj, Rapl = rapl,
            VoltFromMsr = volt, FreqFromMsr = freq, TempFromMsr = temp, PowerFromMsr = power,
        };
    }

    /// <summary>
    /// 原廠倍頻表與實測外頻。外頻由「TSC 頻率 ÷ 原廠非睿頻倍頻」推得——Nehalem 之後 TSC 固定跑在
    /// P1（非睿頻）頻率上，這個等式因此成立，也順便把外頻超頻一起算進去，比寫死 100 MHz 誠實。
    /// </summary>
    private static (double Ghz, double BaseClockMhz, string Label) ReadStockTurbo(Ctx c)
    {
        if (!c.Bridge.Available || c.TscHz <= 0) return (0, 0, "全核");
        int p1 = (int)(((Read(c.Bridge, MsrPlatformInfo) ?? 0) >> 8) & 0xFF);
        if (p1 <= 0) return (0, 0, "全核");
        double bclk = c.TscHz / 1e6 / p1;

        ulong trl = Read(c.Bridge, MsrTurboRatioLimit) ?? 0;
        if (trl == 0) return (0, bclk, "全核");

        // MSR 0x1AD 每 8 位一格，第 i 格 ＝「i+1 顆核同時活躍」的倍頻上限。取最後一個非零格：
        // 核心數 ≤ 8 時那就是全核倍頻；超過 8 顆的部件其全核值在 0x1AE，這裡只能誠實說是 8 核值。
        int ratio = 0, idx = 0;
        for (int i = 0; i < 8; i++)
        {
            int r = (int)((trl >> (i * 8)) & 0xFF);
            if (r > 0) { ratio = r; idx = i + 1; }
        }
        string label = c.Cores.Count > 8 && idx == 8 ? "8 核" : "全核";
        return (ratio > 0 ? ratio * bclk / 1000.0 : 0, bclk, label);
    }

    // ── MSR 讀取與解碼 ──────────────────────────────────────────────────────

    private static double? ReadVolt(Ctx c)
        => c.VoltFromMsr ? DecodeVolt(Read(c.Bridge, MsrPerfStatus)) : c.Sensors?.Volt();

    private static double? ReadTemp(Ctx c)
        => c.TempFromMsr ? DecodeTemp(Read(c.Bridge, MsrPkgThermStatus), c.TjMax) : c.Sensors?.TempC();

    /// <summary>
    /// IA32_PERF_STATUS 位 47:32 是目前核心電壓，單位 1/8192 V。並非每個平台都實作這個欄位，
    /// 所以解出來要做健全性檢查：落在 0.30–2.20 V 之外就當作「此平台沒有這個欄位」，改走感測器。
    /// </summary>
    private static double? DecodeVolt(ulong? raw)
    {
        if (raw is not ulong v) return null;
        double volt = ((v >> 32) & 0xFFFF) / 8192.0;
        return volt is >= VoltFloorV and <= VoltCeilV ? volt : null;
    }

    /// <summary>MSR_TEMPERATURE_TARGET 位 23:16 ＝ TjMax。範圍外視為讀不到。</summary>
    private static int DecodeTjMax(ulong? raw)
    {
        int tj = raw is ulong v ? (int)((v >> 16) & 0xFF) : 0;
        return tj is >= 60 and <= 130 ? tj : 0;
    }

    /// <summary>IA32_PACKAGE_THERM_STATUS：位 31 有效、位 22:16 ＝ 距 TjMax 幾度。</summary>
    private static double? DecodeTemp(ulong? raw, int tjMax)
    {
        if (raw is not ulong v || tjMax <= 0 || (v & (1UL << 31)) == 0) return null;
        double t = tjMax - (int)((v >> 16) & 0x7F);
        return t is >= 0 and <= 130 ? t : null;
    }

    /// <summary>實測 TSC 頻率（Hz）。QPC 夾在 MSR 讀取前後取中點，抵銷橋接呼叫本身的延遲。</summary>
    private static double MeasureTscHz(WinRing0Bridge bridge)
    {
        if (!Grab(out double q0, out ulong t0)) return 0;
        Thread.Sleep(200);
        if (!Grab(out double q1, out ulong t1)) return 0;
        double sec = (q1 - q0) / Stopwatch.Frequency;
        return sec > 0 && t1 > t0 ? (t1 - t0) / sec : 0;

        bool Grab(out double mid, out ulong tsc)
        {
            long a = Stopwatch.GetTimestamp();
            ulong? v = Read(bridge, MsrTsc);
            long b = Stopwatch.GetTimestamp();
            mid = (a + b) / 2.0;
            tsc = v ?? 0;
            return v is not null;
        }
    }

    private static ulong? Read(WinRing0Bridge bridge, uint msr)
    {
        try { return bridge.ReadMsrPair64(msr); }
        catch (Exception ex)
        {
            Diag.Swallow($"讀取 MSR 0x{msr:X}", ex, "該項改用感測器補位或標為無資料");
            return null;
        }
    }

    /// <summary>可中斷的等待。切小段是為了讓「停止」按下之後能在 25 ms 內真的停下來。</summary>
    private static void Sleep(int ms, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(Math.Max(1, Math.Min(25, ms - (int)sw.ElapsedMilliseconds)));
        }
    }
}
