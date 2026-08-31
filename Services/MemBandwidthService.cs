using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;

namespace XinSpect;

/// <summary>
/// 記憶體頻寬與負載延遲：STREAM 式的四種存取型態（讀取／複製／相加／三元運算）逐級加執行緒量到飽和，
/// 再做一次 MLC 式的<b>負載延遲</b>——一邊用其他核心把記憶體塞滿，一邊量單一存取要等多久。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要有這一頁：延遲曲線（<see cref="LatencyCurveService"/>）回答「一次存取多久」，
/// 這一頁回答「同時能搬多少」與「被搶的時候會慢多少」。前者看得出快取層級，
/// 後者才看得出通道插錯、單 rank、或「一跑編譯整台機器就鈍」的來源。
/// </para>
/// <para>
/// 誠實界線：①這是用一般記憶體存取寫出來的測試，不是硬體效能計數器，量到的是<b>本程式達成的</b>頻寬，
/// 不是控制器的絕對上限；②理論上限在「每支模組各佔一個通道」的假設下推算（WMI 說不出實際通道數），
/// 呈現時寫明是假設；③全程只讀寫本程式自己配置的記憶體，不碰任何硬體暫存器。
/// </para>
/// </remarks>
public sealed class MemBandwidthService : ObservableObject
{
    private const long Mib = 1024 * 1024;
    private static double _sink;      // 防止 JIT 消除累加迴圈
    private static int _chaseSink;
    private CancellationTokenSource? _cts;

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private double _progress;
    public double ProgressPercent { get => _progress; private set => SetProperty(ref _progress, value); }

    private string _status = "按「開始量測」逐級加執行緒量出頻寬飽和點，並在重壓下量一次延遲（約 30 秒）。";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

    private string _peakNote = "理論上限：尚未量測。";
    public string PeakNote { get => _peakNote; private set => SetProperty(ref _peakNote, value); }

    private string _verdict = "—";
    public string Verdict { get => _verdict; private set => SetProperty(ref _verdict, value); }

    private string _loadedVerdict = "—";
    public string LoadedVerdict { get => _loadedVerdict; private set => SetProperty(ref _loadedVerdict, value); }

    /// <summary>各存取型態在各執行緒數下的頻寬。</summary>
    public ObservableCollection<MemBandwidthRow> Rows { get; } = [];

    /// <summary>負載延遲：施壓執行緒數 → 當時達成的頻寬與量到的延遲。</summary>
    public ObservableCollection<LoadedLatencyRow> LoadedRows { get; } = [];

    public void Start()
    {
        if (IsRunning) return;
        _ = RunAsync();
    }

    public void Cancel() => _cts?.Cancel();

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        ProgressPercent = 0;
        Rows.Clear();
        LoadedRows.Clear();
        Verdict = "—";
        LoadedVerdict = "—";

        try
        {
            var (mtps, modules) = ReadModuleFacts();
            double perModule = MemBandwidthMath.PerModulePeakGbps(mtps);
            double peak = MemBandwidthMath.AssumedPeakGbps(mtps, modules);
            PeakNote = peak > 0
                ? $"SMBIOS：{modules} 支模組、實際運行 {mtps} MT/s ・ 每支上限 {MemBandwidthMath.FormatGbps(perModule)}"
                  + $" → 整機上限 {MemBandwidthMath.FormatGbps(peak)}（假設每支各佔一個通道；WMI 說不出實際通道數）"
                : "SMBIOS 沒回報記憶體實際運行速度，這一輪只給實測值，不做達成率對照。";

            var progress = new Progress<(double Frac, string Text)>(t =>
            {
                ProgressPercent = t.Frac * 100;
                StatusLine = t.Text;
            });

            var result = await Task.Run(() => Measure(peak, (IProgress<(double, string)>)progress, ct), ct);

            foreach (var r in result.Bandwidth) Rows.Add(r);
            foreach (var r in result.Loaded) LoadedRows.Add(r);

            var (text, _) = MemBandwidthMath.Judge(result.BestGbps, mtps, modules);
            Verdict = text;
            LoadedVerdict = MemBandwidthMath.SummarizeLoaded(result.Loaded);
            ProgressPercent = 100;
            StatusLine = $"完成 ・ 頻寬 {result.Bandwidth.Count} 組量測、負載延遲 {result.Loaded.Count} 個施壓等級"
                       + $"（工作集每組 {result.ArrayMib} MiB、追逐區 {result.ChaseMib} MiB）。";
        }
        catch (OperationCanceledException)
        {
            StatusLine = "量測已停止。";
        }
        catch (Exception ex)
        {
            StatusLine = "量測失敗：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }
    /// <summary>SMBIOS 的「插了幾支、跑在幾 MT/s」。取所有模組中最低的實際速度（混插時以慢的為準）。</summary>
    private static (int MtPerSecond, int Modules) ReadModuleFacts()
    {
        try
        {
            var mods = SystemInfoService.GetMemoryModules().Where(m => m.CapacityGB > 0).ToList();
            if (mods.Count == 0) return (0, 0);
            var speeds = mods.Select(m => m.ConfiguredSpeedMHz > 0 ? m.ConfiguredSpeedMHz : m.RatedSpeedMHz)
                             .Where(s => s > 0).ToList();
            return (speeds.Count > 0 ? speeds.Min() : 0, mods.Count);
        }
        catch (Exception ex)
        {
            Diag.Swallow("記憶體模組速度查詢", ex, "不做理論上限對照，只給實測值");
            return (0, 0);
        }
    }

    private sealed record MeasureResult(List<MemBandwidthRow> Bandwidth, List<LoadedLatencyRow> Loaded,
                                        double BestGbps, long ArrayMib, long ChaseMib);

    /// <summary>四種存取型態 × 執行緒階梯，再加負載延遲；全程可取消。</summary>
    private MeasureResult Measure(double peakGbps, IProgress<(double, string)> report, CancellationToken ct)
    {
        // 工作集要遠大於 L3，否則量到的是快取頻寬不是記憶體頻寬；同時不能吃乾可用記憶體
        long avail = (long)(new MemoryService().ReadStats().AvailGB * 1024 * Mib);
        long budget = Math.Clamp((long)(avail * 0.35), 256 * Mib, 768 * Mib);
        long arrayBytes = Math.Clamp(budget / 4, 32 * Mib, 192 * Mib) / 64 * 64;
        long chaseBytes = Math.Clamp(budget / 4, 32 * Mib, 128 * Mib) / 64 * 64;

        int n = (int)(arrayBytes / sizeof(double));
        var a = new double[n];
        var b = new double[n];
        var c = new double[n];
        for (int i = 0; i < n; i++) { a[i] = 1.0; b[i] = 2.0; c[i] = 0.5; }

        int lp = Math.Max(1, Environment.ProcessorCount);
        var ladder = MemBandwidthMath.ThreadLadder(lp);
        var kernels = new (string Name, int Streams, Action<double[], double[], double[], int, int> Body)[]
        {
            ("讀取", 1, static (x, y, z, lo, hi) => Read(x, lo, hi)),
            ("複製", 2, static (x, y, z, lo, hi) => Copy(x, z, lo, hi)),
            ("相加", 3, static (x, y, z, lo, hi) => Add(x, y, z, lo, hi)),
            ("三元運算", 3, static (x, y, z, lo, hi) => Triad(x, y, z, lo, hi)),
        };

        int totalSteps = kernels.Length * ladder.Length + MemBandwidthMath.LoadLadder(lp).Length;
        int step = 0;
        var raw = new List<(string Kernel, int Threads, double Gbps)>();

        foreach (var k in kernels)
        {
            foreach (int t in ladder)
            {
                ct.ThrowIfCancellationRequested();
                double best = 0;
                for (int pass = 0; pass < 3; pass++)
                {
                    double sec = RunParallel(a, b, c, n, t, k.Body, ct);
                    best = Math.Max(best, MemBandwidthMath.Gbps((double)n * sizeof(double) * k.Streams, sec));
                }
                raw.Add((k.Name, t, best));
                report.Report((++step / (double)totalSteps,
                               $"{k.Name} ・ {t} 執行緒 … {MemBandwidthMath.FormatGbps(best)}"));
            }
        }

        double top = raw.Count > 0 ? raw.Max(r => r.Gbps) : 0;
        var rows = raw.Select(r => new MemBandwidthRow(r.Kernel, r.Threads, r.Gbps,
                                                       top > 0 ? Math.Clamp(r.Gbps / top, 0.02, 1) : 0,
                                                       MemBandwidthMath.EfficiencyNote(r.Gbps, peakGbps)))
                      .ToList();

        var loaded = MeasureLoadedLatency(a, chaseBytes, lp, report, step, totalSteps, ct);
        return new MeasureResult(rows, loaded, top, arrayBytes / Mib, chaseBytes / Mib);
    }
    /// <summary>把 [0, n) 切成 threads 段（對齊向量寬度）同時跑一遍，回傳耗時（秒）。</summary>
    private static double RunParallel(double[] a, double[] b, double[] c, int n, int threads,
                                      Action<double[], double[], double[], int, int> body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int w = Vector<double>.Count;
        var tasks = new Task[threads];
        var sw = Stopwatch.StartNew();
        for (int t = 0; t < threads; t++)
        {
            var (lo, hi) = MemBandwidthMath.Slice(n, threads, t, w);
            tasks[t] = lo >= hi ? Task.CompletedTask : Task.Run(() => body(a, b, c, lo, hi), CancellationToken.None);
        }
        Task.WaitAll(tasks);
        sw.Stop();
        return sw.Elapsed.TotalSeconds;
    }

    // 四種存取型態。全部以 Vector<double> 走，單執行緒才有機會逼近通道上限；
    // 結果寫進 _sink 只為了讓 JIT 不能把整個迴圈當成無用程式碼刪掉。
    private static void Read(double[] x, int lo, int hi)
    {
        int w = Vector<double>.Count;
        var acc = Vector<double>.Zero;
        int i = lo;
        for (; i <= hi - w; i += w) acc += new Vector<double>(x, i);
        double s = Vector.Dot(acc, Vector<double>.One);
        for (; i < hi; i++) s += x[i];
        _sink = s;
    }

    private static void Copy(double[] x, double[] z, int lo, int hi)
    {
        int w = Vector<double>.Count;
        int i = lo;
        for (; i <= hi - w; i += w) new Vector<double>(x, i).CopyTo(z, i);
        for (; i < hi; i++) z[i] = x[i];
    }

    private static void Add(double[] x, double[] y, double[] z, int lo, int hi)
    {
        int w = Vector<double>.Count;
        int i = lo;
        for (; i <= hi - w; i += w) (new Vector<double>(x, i) + new Vector<double>(y, i)).CopyTo(z, i);
        for (; i < hi; i++) z[i] = x[i] + y[i];
    }

    private static void Triad(double[] x, double[] y, double[] z, int lo, int hi)
    {
        const double k = 3.0;
        var kv = new Vector<double>(k);
        int w = Vector<double>.Count;
        int i = lo;
        for (; i <= hi - w; i += w) (new Vector<double>(y, i) + new Vector<double>(z, i) * kv).CopyTo(x, i);
        for (; i < hi; i++) x[i] = y[i] + k * z[i];
    }
    /// <summary>
    /// 負載延遲：施壓執行緒在背景不斷讀取大陣列，同時在另一條執行緒上做指標追逐量延遲，
    /// 並記下同一個時間窗內施壓執行緒實際搬了多少位元組——延遲與達成頻寬是<b>同時</b>量的，才能配成一對。
    /// </summary>
    /// <remarks>
    /// 每條施壓執行緒只讀<b>自己那一段</b>（<see cref="MemBandwidthMath.Slice"/>），記帳也只記自己那一段。
    /// 早期版本讓每條都讀整個陣列卻各記一整份，於是第一條把快取行拉進 L3 之後其他都變成快取命中，
    /// 「達成頻寬」被算成 156 GB/s——比同一頁上方印出的理論上限 115 GB/s 還高，也是實測峰值的 2.6 倍。
    /// 那是記帳錯誤，不是這台機器真的搬得動那麼多。
    /// </remarks>
    private static List<LoadedLatencyRow> MeasureLoadedLatency(double[] load, long chaseBytes, int lp,
                                                              IProgress<(double, string)> report,
                                                              int stepBase, int totalSteps, CancellationToken ct)
    {
        var chase = BuildChase(chaseBytes);
        var ladder = MemBandwidthMath.LoadLadder(lp);
        var points = new List<(int Loaders, double Gbps, double Ns)>();
        int vw = Vector<double>.Count;

        for (int i = 0; i < ladder.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            int loaders = ladder[i];
            var moved = new long[1];
            using var stop = new CancellationTokenSource();
            var tasks = new Task[loaders];
            for (int t = 0; t < loaders; t++)
            {
                var token = stop.Token;
                var (lo, hi) = MemBandwidthMath.Slice(load.Length, loaders, t, vw);
                long myBytes = (long)(hi - lo) * sizeof(double);
                tasks[t] = lo >= hi ? Task.CompletedTask : Task.Run(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        Read(load, lo, hi);
                        Interlocked.Add(ref moved[0], myBytes);
                    }
                }, CancellationToken.None);
            }

            try
            {
                // 讓施壓執行緒先跑起來再開始計時，否則前幾十毫秒的空窗會把達成頻寬算低
                if (loaders > 0) Thread.Sleep(120);
                Interlocked.Exchange(ref moved[0], 0);
                var sw = Stopwatch.StartNew();
                double ns = ChaseLatencyNs(chase);
                sw.Stop();
                long bytes = Interlocked.Read(ref moved[0]);
                points.Add((loaders, MemBandwidthMath.Gbps(bytes, sw.Elapsed.TotalSeconds), ns));
            }
            finally
            {
                stop.Cancel();
                try { Task.WaitAll(tasks); } catch { /* 施壓執行緒只會因為取消而結束 */ }
            }

            report.Report(((stepBase + i + 1) / (double)totalSteps,
                           $"負載延遲 ・ {(loaders == 0 ? "無負載" : loaders + " 執行緒施壓")} … {points[^1].Ns:0.0} ns"));
        }

        double worst = points.Count > 0 ? points.Max(p => p.Ns) : 0;
        return points.Select(p => new LoadedLatencyRow(p.Loaders, p.Gbps, p.Ns,
                                                       worst > 0 ? Math.Clamp(p.Ns / worst, 0.02, 1) : 0))
                     .ToList();
    }
    /// <summary>
    /// 建一條涵蓋整個工作集的亂序指標環（每 64 位元組一站，洗牌後串成單一循環），
    /// 硬體預取器猜不到下一站，量到的才是真正的存取延遲。
    /// </summary>
    private static int[] BuildChase(long bytes)
    {
        const int stride = 16;                    // 64 位元組快取行 ÷ 4 位元組 int
        // 上限 128 MiB（呼叫端已夾好），所以 slots × stride 不會溢位
        int slots = (int)Math.Clamp(bytes / (stride * sizeof(int)), 2, 8 * 1024 * 1024);
        var arr = new int[slots * stride];

        var order = new int[slots];
        for (int i = 0; i < slots; i++) order[i] = i;
        var rng = new Random(0x5EED_1234);
        for (int i = slots - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        for (int i = 0; i < slots; i++)
            arr[order[i] * stride] = order[(i + 1) % slots] * stride;
        return arr;
    }

    /// <summary>在指標環上追逐固定站數，回傳平均每站延遲（ns）。先暖機把分頁表與 TLB 帶起來。</summary>
    private static double ChaseLatencyNs(int[] chase)
    {
        int index = 0;
        Chase(chase, 200_000, ref index);          // 暖機
        const long hops = 600_000;
        var sw = Stopwatch.StartNew();
        Chase(chase, hops, ref index);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds * 1e6 / hops;
    }

    private static void Chase(int[] arr, long hops, ref int index)
    {
        for (long i = 0; i < hops; i++) index = arr[index];
        _chaseSink = index;
    }
}
