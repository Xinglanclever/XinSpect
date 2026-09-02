using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace XinSpect;

/// <summary>單一核心模組的 DPC／ISR 統計列（頻次＋單次執行時長）。</summary>
public sealed class DpcRow
{
    public DpcRow(string module, string kind, long count, double maxUs, double meanUs, double busyPercent, double barFraction)
    {
        Module = module; Kind = kind; Count = count;
        MaxUs = maxUs; MeanUs = meanUs; BusyPercent = busyPercent; BarFraction = barFraction;
    }
    public string Module { get; }
    public string Kind { get; }
    public long Count { get; }
    /// <summary>這段量測裡最久的一次（微秒）。爆音與停頓看的是這個，不是平均。</summary>
    public double MaxUs { get; }
    public double MeanUs { get; }
    /// <summary>佔整段量測時間的比例（％）。同一支驅動在多顆核心上跑時可以超過 100 %。</summary>
    public double BusyPercent { get; }
    public double BarFraction { get; }

    public string CountText => $"{Count:N0}";
    public string MaxText => MaxUs > 0 ? $"{MaxUs:N0} µs" : "—";
    public string AvgText => MeanUs > 0 ? $"{MeanUs:N1} µs" : "—";
    public string BusyText => BusyPercent > 0 ? $"{BusyPercent:0.##} %" : "—";
}

/// <summary>一次 DPC／ISR 事件裡我們用得到的東西（供彙整器測試）。</summary>
public readonly record struct DpcSample(string Module, string Kind, double DurationMs);

/// <summary>單一（模組，類別）的累計量：次數、總時長、最久一次。</summary>
public sealed class DpcStat
{
    public long Count { get; private set; }
    public double SumMs { get; private set; }
    public double MaxMs { get; private set; }

    public void Add(double ms)
    {
        Count++;
        // 極短的常式 ETW 會回 0（時間戳解析度不足），照實累加，最後顯示成「—」而不是假的 0.0 µs
        if (ms > 0) SumMs += ms;
        if (ms > MaxMs) MaxMs = ms;
    }
}

/// <summary>
/// DPC／ISR 統計的彙整與判讀（純函式，單元測試涵蓋）。
/// 排序以<b>單次最長時長</b>為主鍵、次數為次鍵——會造成爆音與停頓的是「某一次跑太久」，
/// 不是「跑很多次但每次都很短」。時長全為 0 的平台會自然退化成純頻次排行。
/// </summary>
public static class DpcAggregator
{
    /// <summary>值得注意的門檻（微秒）。音訊緩衝常在 1–3 ms，單次 500 µs 已吃掉可觀的餘裕。</summary>
    public const double AttentionUs = 500;
    /// <summary>經驗上的問題門檻（微秒）。LatencyMon 一類工具也用 1 ms 附近當警示線。</summary>
    public const double ProblemUs = 1000;

    /// <summary>把事件序列累成統計表（測試與小量資料用；量測時服務是逐事件即時累加的）。</summary>
    public static Dictionary<(string Module, string Kind), DpcStat> Accumulate(IEnumerable<DpcSample> samples)
    {
        var stats = new Dictionary<(string Module, string Kind), DpcStat>();
        foreach (var s in samples)
        {
            var key = (s.Module, s.Kind);
            if (!stats.TryGetValue(key, out var st)) stats[key] = st = new DpcStat();
            st.Add(s.DurationMs);
        }
        return stats;
    }

    /// <summary>統計表 → 排行列。<paramref name="windowSeconds"/> 用來換算佔用比例，≤ 0 時佔用一律為 0。</summary>
    public static List<DpcRow> Rank(IReadOnlyDictionary<(string Module, string Kind), DpcStat> stats, double windowSeconds, int top = 20)
    {
        double windowMs = windowSeconds > 0 ? windowSeconds * 1000 : 0;
        var rows = stats.Where(kv => kv.Value.Count > 0).Select(kv =>
        {
            var st = kv.Value;
            double maxUs = st.MaxMs * 1000;
            double meanUs = st.SumMs > 0 ? st.SumMs * 1000 / st.Count : 0;
            double busy = windowMs > 0 ? st.SumMs / windowMs * 100 : 0;
            return new DpcRow(kv.Key.Module, kv.Key.Kind, st.Count, maxUs, meanUs, busy, 0);
        }).ToList();

        double maxOfMax = rows.Count > 0 ? rows.Max(r => r.MaxUs) : 0;
        long maxOfCount = rows.Count > 0 ? rows.Max(r => r.Count) : 0;

        return rows
            .OrderByDescending(r => r.MaxUs)
            .ThenByDescending(r => r.Count)
            .ThenBy(r => r.Module, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            // 條長以時長為準；平台完全沒給時長時退回以次數為準，長條才不會全部一樣長
            .Select(r => new DpcRow(r.Module, r.Kind, r.Count, r.MaxUs, r.MeanUs, r.BusyPercent,
                        Math.Clamp(maxOfMax > 0 ? r.MaxUs / maxOfMax
                                 : maxOfCount > 0 ? r.Count / (double)maxOfCount : 0, 0.02, 1)))
            .ToList();
    }

    /// <summary>0＝沒問題、1＝值得注意、2＝已達經驗上的問題門檻。</summary>
    public static int Judge(double maxUs) => maxUs >= ProblemUs ? 2 : maxUs >= AttentionUs ? 1 : 0;

    /// <summary>依排行寫出一句結論。沒量到事件、或平台不給時長，都要如實說出來。</summary>
    public static string Verdict(IReadOnlyList<DpcRow> rows)
    {
        if (rows.Count == 0) return "完成 ・ 這段時間沒有量到 DPC／ISR 事件（系統非常安靜）。";

        var top = rows[0];
        if (top.MaxUs <= 0)
            return $"完成 ・ 本機的 ETW 事件沒有帶回執行時長（全部為 0），只能給頻次排行：榜首 {top.Module}（{top.Kind}，{top.CountText} 次）。";

        string head = $"完成 ・ 單次最久是 {top.Module}（{top.Kind}）的 {top.MaxText}，共 {top.CountText} 次、平均 {top.AvgText}";
        return Judge(top.MaxUs) switch
        {
            2 => $"⚠ {head}——超過 {ProblemUs:0} µs 的經驗門檻，音訊爆音／輸入停頓多半出自這裡。這是統計不是判決：先查它的驅動版本與電源設定。",
            1 => $"{head}——落在 {AttentionUs:0}–{ProblemUs:0} µs 之間，尚可但已吃掉不少餘裕；若你聽得到爆音，從這支查起。",
            _ => $"{head}——都在 {AttentionUs:0} µs 以下，沒有哪支驅動吃住 CPU。",
        };
    }
}

/// <summary>
/// DPC／ISR 延遲排行（純 ETW kernel tracer，零驅動安裝、零注入）：訂閱核心的 DPC／ISR 事件，
/// 依<b>單次執行時長</b>排出肇事驅動模組，並畫出每秒總量的時間分佈。
/// DPC 風暴與音訊爆音、輸入停頓、串流掉幀高度相關；哪支驅動吃掉數百微秒，排行榜直接指出。
/// </summary>
/// <remarks>
/// 誠實界線：時長來自經典 ETW 的 <c>DPCTraceData.ElapsedTimeMSec</c>／<c>ISRTraceData.ElapsedTimeMSec</c>
/// （1.7.0 起使用；在此之前本頁只做頻次，並誤以為時長非得自帶核心驅動才拿得到——那是錯的）。
/// 極短的常式時長會回 0，這種列顯示「—」而不是 0.0 µs。模組歸屬用
/// <c>EnumDeviceDrivers</c> 的基底位址近似，相鄰模組的極端情況可能錯置。
/// 門檻（500／1000 µs）是經驗值，不是規格，本頁只給統計不下判決。
/// </remarks>
public sealed class DpcLatencyService : ObservableObject, IDisposable
{
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumDeviceDrivers(IntPtr[]? drivers, uint bufSize, out uint needed);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDeviceDriverBaseNameW(IntPtr imageBase, StringBuilder name, int len);

    private TraceEventSession? _session;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private readonly Dictionary<(string Module, string Kind), DpcStat> _stats = new();
    private readonly List<double> _rateSamples = [];

    /// <summary>ISR 事件的（驅動、核心）配對。只收 ISR：DPC 可能被排到別的核上執行，混在一起會失去意義。</summary>
    private readonly List<IsrSample> _isr = [];
    private DateTime _startedAt;
    private Dictionary<ulong, string> _kernelModules = [];
    private ulong[] _sortedBases = [];
    private string[] _sortedNames = [];

    private int _durationSec = 15;
    /// <summary>量測時長（秒），5–60。</summary>
    public int DurationSec { get => _durationSec; set => SetProperty(ref _durationSec, Math.Clamp(value, 5, 60)); }

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _phase = "尚未量測";
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }

    private double _progress;
    public double ProgressFraction { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => _progress * 100;

    private string _status = "按「開始量測」訂閱核心 DPC／ISR 事件（ETW，零驅動安裝）。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>每秒的 DPC＋ISR 總量時間分佈（供圖表呈現系統被打斷的節奏）。</summary>
    public double[] RatePerSecond { get; private set; } = [];

    public ObservableCollection<DpcRow> Rows { get; } = [];

    /// <summary>中斷落在哪顆核（僅 ISR 事件；DPC 不算，那是另一段執行）。</summary>
    public ObservableCollection<CpuInterruptRow> ByCpu { get; } = [];

    /// <summary>每一支驅動的中斷集中在哪顆核。</summary>
    public ObservableCollection<ModuleAffinityRow> ByModule { get; } = [];

    private string _affinityVerdict = "量測後才知道中斷落在哪幾顆核上。";
    /// <summary>一句話：有沒有哪顆核被中斷壓住。</summary>
    public string AffinityVerdict { get => _affinityVerdict; private set => SetProperty(ref _affinityVerdict, value); }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        try { _session?.Dispose(); } catch { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        IsRunning = true;
        Phase = "量測中";
        ProgressFraction = 0;
        Rows.Clear();
        RatePerSecond = [];
        lock (_lock) { _stats.Clear(); _rateSamples.Clear(); _isr.Clear(); }
        _startedAt = DateTime.Now;

        try
        {
            _kernelModules = await Task.Run(LoadKernelModules);
            var ordered = _kernelModules.OrderBy(kv => kv.Key).ToList();
            _sortedBases = ordered.Select(kv => kv.Key).ToArray();
            _sortedNames = ordered.Select(kv => kv.Value).ToArray();

            _session = new TraceEventSession("XinSpect-Dpc", TraceEventSessionOptions.Create);
            try
            {
                _session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.Interrupt);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("核心 DPC／ISR 追蹤無法啟用：" + ex.Message);
            }

            var pump = Task.Run(() =>
            {
                var source = _session!.Source;
                // 三種 DPC 事件都要收：一般 DPC、計時器 DPC、執行緒化 DPC。
                // 只收 PerfInfoDPC 會漏掉計時器 DPC，而那往往正是量最大的一群。
                source.Kernel.PerfInfoDPC += e => Record("DPC", e.Routine, e.ElapsedTimeMSec);
                source.Kernel.PerfInfoTimerDPC += e => Record("計時器 DPC", e.Routine, e.ElapsedTimeMSec);
                source.Kernel.PerfInfoThreadedDPC += e => Record("執行緒 DPC", e.Routine, e.ElapsedTimeMSec);
                source.Kernel.PerfInfoISR += e =>
                {
                    Record("ISR", e.Routine, e.ElapsedTimeMSec);
                    // 中斷是在哪顆核上被服務的——這一欄讓「哪顆核被打爆」變成「是誰打爆它」
                    lock (_lock) _isr.Add(new IsrSample(ResolveModule(e.Routine), e.ProcessorNumber));
                };
                source.Process();
            }, CancellationToken.None);

            while (DateTime.Now - _startedAt < TimeSpan.FromSeconds(DurationSec) && !ct.IsCancellationRequested)
            {
                await Task.Delay(500, CancellationToken.None);
                ProgressFraction = (DateTime.Now - _startedAt).TotalSeconds / DurationSec;
                Status = $"量測中…（{Math.Min((DateTime.Now - _startedAt).TotalSeconds, DurationSec):0} / {DurationSec} 秒）";
            }

            Phase = "彙整";
            try { _session.Dispose(); } catch { }
            try { await pump.WaitAsync(TimeSpan.FromSeconds(2000)); } catch { }

            var rows = await Task.Run(BuildRows);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);

            List<IsrSample> isr;
            lock (_lock) isr = [.. _isr];
            var byCpu = InterruptAffinityAggregator.ByCpu(isr);
            var byModule = InterruptAffinityAggregator.ByModule(isr);
            ByCpu.Clear();
            foreach (var r in byCpu) ByCpu.Add(r);
            ByModule.Clear();
            foreach (var r in byModule) ByModule.Add(r);
            AffinityVerdict = InterruptAffinityAggregator.Verdict(byCpu, byModule);
            Phase = "完成";
            ProgressFraction = 1;
            Status = DpcAggregator.Verdict(rows);
        }
        catch (OperationCanceledException)
        {
            Phase = "已停止";
            Status = "已停止量測。";
        }
        catch (Exception ex)
        {
            Phase = "錯誤";
            Status = "量測失敗：" + ex.Message;
        }
        finally
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            IsRunning = false;
        }
    }

    private void Record(string kind, ulong routine, double elapsedMs)
    {
        lock (_lock)
        {
            var key = (ResolveModule(routine), kind);
            if (!_stats.TryGetValue(key, out var st)) _stats[key] = st = new DpcStat();
            st.Add(elapsedMs);

            double sec = (DateTime.Now - _startedAt).TotalSeconds;
            if (sec >= 0)
            {
                // 每秒一格：把目前秒數的格子補滿
                while (_rateSamples.Count < (int)sec + 1) _rateSamples.Add(0);
                _rateSamples[(int)sec]++;
            }
        }
    }

    private string ResolveModule(ulong routine)
    {
        // 模組實際長度拿不到（EnumDeviceDrivers 只給基底），以「最高不超過常式位址的基底」近似歸屬；
        // 相鄰模組的極端情況可能錯置，但排行層級通常仍正確。
        // 位址表事先排好序並用二分搜尋：一秒可能進來上萬個事件，這裡不能每次都排序一遍。
        if (_sortedBases.Length == 0) return $"0x{routine:X}";
        int i = Array.BinarySearch(_sortedBases, routine);
        if (i < 0) i = ~i - 1;                       // 取插入點的前一個＝不超過它的最大基底
        return i >= 0 ? _sortedNames[i] : $"0x{routine:X}";
    }

    /// <summary>列舉核心驅動模組（基底位址→名稱）。管理員權限下位址準確。</summary>
    private static Dictionary<ulong, string> LoadKernelModules()
    {
        var map = new Dictionary<ulong, string>();
        try
        {
            uint needed = 0;
            if (!EnumDeviceDrivers(null, 0, out needed) || needed == 0) return map;
            int count = (int)(needed / (uint)IntPtr.Size);
            var drivers = new IntPtr[count + 1];
            if (!EnumDeviceDrivers(drivers, needed, out _)) return map;
            for (int i = 0; i < count; i++)
            {
                if (drivers[i] == IntPtr.Zero) continue;
                var sb = new StringBuilder(256);
                GetDeviceDriverBaseNameW(drivers[i], sb, 256);
                map[(ulong)drivers[i]] = sb.ToString();
            }
        }
        catch { /* 模組對映失敗時以位址呈現 */ }
        return map;
    }

    private List<DpcRow> BuildRows()
    {
        Dictionary<(string Module, string Kind), DpcStat> snapshot;
        double window;
        lock (_lock)
        {
            snapshot = new Dictionary<(string Module, string Kind), DpcStat>(_stats);
            RatePerSecond = _rateSamples.ToArray();
            // 實際跑了多久：使用者按停止時可能短於設定值，用實際時間算佔用比例才不會低估
            window = Math.Max(0.001, Math.Min((DateTime.Now - _startedAt).TotalSeconds, DurationSec));
        }
        return DpcAggregator.Rank(snapshot, window);
    }

    public void Dispose() { try { _session?.Dispose(); } catch { } }
}
