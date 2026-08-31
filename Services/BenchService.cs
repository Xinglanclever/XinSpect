using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>
/// 綜合效能測試：單執行緒 / 多執行緒運算吞吐（MOPS）與記憶體頻寬（GB/s），
/// 加權為綜合分數。時間可設定、可取消，並即時回報階段與進度。
/// 測試期間 CPU 溫度 / 頻率的變化由主計時器照常擷取，於畫面上以走勢圖呈現。
/// </summary>
/// <remarks>
/// 綜合分數的權重（單 × 2 + 多 + 頻寬 × 20）是本程式自訂的，沒有跨機器的意義，
/// 因此不與任何外部分數對照。可信的比較對象是本機歷次成績（<see cref="BenchLog"/>）：
/// 同一測試時間才算同設定，並一併記下量測期間的溫度與頻率，
/// 好讓「這次比上次低」能分清是硬體變了還是當時比較熱。
/// </remarks>
public sealed class BenchService : ObservableObject
{
    // 防止 JIT 將運算視為無用而消除
    private static double _sink;

    /// <summary>紀錄簿中的項目代號。</summary>
    private const string KindComposite = "bench.composite";

    private CancellationTokenSource? _cts;
    private readonly BenchLog _log;

    /// <summary>量測期間的實機條件（由每秒脈動餵入）。</summary>
    public BenchConditions Conditions { get; } = new();

    public BenchService(BenchLog? log = null) => _log = log ?? new BenchLog();

    public int Threads => Environment.ProcessorCount;


    private int _duration = 30;
    /// <summary>總測試秒數（三個階段平分）。</summary>
    public int DurationSeconds { get => _duration; set { if (SetProperty(ref _duration, value)) OnPropertyChanged(nameof(DurationText)); } }
    public string DurationText => $"{_duration} 秒";

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _phase = "尚未測試";
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }

    private double _progress;
    public double ProgressFraction { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => _progress * 100;

    private string _status = "設定測試時間後按「開始測試」。";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

    private double? _single;
    public double? SingleScore { get => _single; private set { if (SetProperty(ref _single, value)) OnPropertyChanged(nameof(SingleText)); } }
    public string SingleText => _single is double v ? $"{v:0}" : "—";

    private double? _multi;
    public double? MultiScore { get => _multi; private set { if (SetProperty(ref _multi, value)) OnPropertyChanged(nameof(MultiText)); } }
    public string MultiText => _multi is double v ? $"{v:0}" : "—";

    private double? _mem;
    public double? MemBandwidth { get => _mem; private set { if (SetProperty(ref _mem, value)) OnPropertyChanged(nameof(MemText)); } }
    public string MemText => _mem is double v ? $"{v:0.0}" : "—";

    private double? _composite;
    public double? Composite { get => _composite; private set { if (SetProperty(ref _composite, value)) OnPropertyChanged(nameof(CompositeText)); } }
    public string CompositeText => _composite is double v ? $"{v:0}" : "—";

    // ── 與本機歷次成績的對照（唯一誠實的基準）────────────────────────────────
    private string _delta = "", _repeat = "", _conditionText = "";

    /// <summary>綜合分數與本機上次同設定的比較。</summary>
    public string DeltaText { get => _delta; private set => SetProperty(ref _delta, value); }
    /// <summary>本機同設定的重複量測統計（次數／範圍／離散度）。</summary>
    public string RepeatText { get => _repeat; private set => SetProperty(ref _repeat, value); }
    /// <summary>本次量測期間的溫度／頻率條件；沒取到感測值時為空字串。</summary>
    public string ConditionText { get => _conditionText; private set => SetProperty(ref _conditionText, value); }

    /// <summary>由 UI 執行緒呼叫（Progress&lt;T&gt; 需在此擷取同步內容以回送 UI）。</summary>
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
        // 跑分期間停掉全站動畫：重繪要花 GPU 與封裝功耗，而那會進到成績裡。
        // 走 Suspend() 而不是改設定，跑完自動恢復，使用者的偏好不受影響。
        using var quiet = Motion.Suspend();
        Phase = "準備中";
        ProgressFraction = 0;
        StatusLine = "測試進行中，請避免其他高負載程式以取得穩定結果…";
        SingleScore = MultiScore = MemBandwidth = Composite = null;
        DeltaText = RepeatText = ConditionText = "";
        Conditions.Reset();

        var prog = new Progress<double>(p => ProgressFraction = Math.Clamp(p, 0, 1));
        var ip = (IProgress<double>)prog;
        Action<double> Band(double lo, double hi) => p => ip.Report(lo + (hi - lo) * Math.Clamp(p, 0, 1));

        double each = Math.Max(2.0, DurationSeconds / 3.0);

        try
        {
            // 暖機（讓時脈升頻、快取就緒），不計分
            Phase = "暖機";
            await Task.Run(() => Volatile.Write(ref _sink, Kernel(8_000_000)), ct);
            ct.ThrowIfCancellationRequested();

            Phase = "單執行緒運算";
            double single = await Task.Run(() => RunCpu(1, each, ct, Band(0.05, 0.40)), ct);
            SingleScore = single;
            ct.ThrowIfCancellationRequested();

            Phase = $"多執行緒運算（{Threads} 執行緒）";
            double multi = await Task.Run(() => RunCpu(Threads, each, ct, Band(0.40, 0.78)), ct);
            MultiScore = multi;
            ct.ThrowIfCancellationRequested();

            Phase = "記憶體頻寬";
            double gb = await Task.Run(() => RunMem(each, ct, Band(0.78, 1.0)), ct);
            MemBandwidth = gb;

            double composite = Math.Round(single * 2 + multi + gb * 20);
            Composite = composite;

            // 記入本機紀錄簿：同一測試時間才算同設定，比較對象只有這台機器自己
            string config = $"{DurationSeconds} 秒 ・ {Threads} 執行緒";
            string cond = Conditions.Text();
            ConditionText = cond;
            Record(config, composite, cond);
            DeltaText = _log.DeltaText(KindComposite, config);
            RepeatText = _log.Stats(KindComposite, config).Text;

            Phase = "完成";
            ProgressFraction = 1;
            StatusLine = $"測試完成 ・ 綜合分數 {composite:0} 分 ・ {RepeatText}";
        }
        catch (OperationCanceledException)
        {
            Phase = "已取消";
            StatusLine = "測試已取消（未完成的量測不列入紀錄）。";
        }
        catch (Exception ex)
        {
            Phase = "錯誤";
            StatusLine = "測試失敗：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---- 運算核心與各階段 --------------------------------------------------

    private void Record(string config, double composite, string conditions)
    {
        try
        {
            _log.Add(new BenchRun
            {
                Kind = KindComposite, Title = "綜合跑分", Config = config, Score = composite, Unit = "分",
                HigherIsBetter = true, Format = "#,0", UtcTime = DateTime.UtcNow, Conditions = conditions,
            });
        }
        catch { /* 紀錄失敗不影響已量到的成績 */ }
    }

    /// <summary>混合整數 / 浮點的運算核心；回傳累加值以避免被最佳化消除。</summary>
    private static double Kernel(int iters)
    {
        double acc = 0;
        for (int i = 0; i < iters; i++)
        {
            long x = (i + 1) * 2654435761L;
            x ^= x >> 13; x *= 1274126177L; x ^= x >> 16;
            double f = (x & 0xFFFF) * 1.52587890625e-5;   // /65536
            acc += f * f - f + 1.0 / (f + 1.0);
        }
        return acc;
    }

    /// <summary>以指定執行緒數在固定時間內全速運算，回傳每秒百萬次運算（MOPS）。</summary>
    private static double RunCpu(int threads, double seconds, CancellationToken ct, Action<double> report)
    {
        const int Batch = 1_000_000;
        var sw = Stopwatch.StartNew();
        var counts = new long[threads];
        var workers = new Thread[threads];

        for (int t = 0; t < threads; t++)
        {
            int id = t;
            workers[t] = new Thread(() =>
            {
                double sink = 0;
                long local = 0;
                double lastReport = 0;
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    if (ct.IsCancellationRequested) break;
                    sink += Kernel(Batch);
                    local += Batch;
                    if (id == 0)
                    {
                        double frac = sw.Elapsed.TotalSeconds / seconds;
                        if (frac - lastReport >= 0.03) { report(frac); lastReport = frac; }
                    }
                }
                counts[id] = local;
                Volatile.Write(ref _sink, sink);
            })
            { IsBackground = true, Priority = ThreadPriority.Highest, Name = $"XinSpectBench#{id}" };
        }

        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();
        ct.ThrowIfCancellationRequested();

        double secs = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        long total = 0;
        foreach (var c in counts) total += c;
        return Math.Round(total / secs / 1_000_000.0);
    }

    /// <summary>大陣列複製 + 讀取（超出快取容量），回傳有效記憶體頻寬（GB/s）。</summary>
    private static double RunMem(double seconds, CancellationToken ct, Action<double> report)
    {
        const int N = 8 * 1024 * 1024;   // 8M doubles = 64 MB（遠超 L3，量測到主記憶體）
        var a = new double[N];
        var b = new double[N];
        for (int i = 0; i < N; i++) a[i] = i * 0.5;

        var sw = Stopwatch.StartNew();
        long bytes = 0;
        double lastReport = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (ct.IsCancellationRequested) break;
            Array.Copy(a, b, N);                       // 讀 N*8 + 寫 N*8
            double s = 0;
            for (int i = 0; i < N; i += 8) s += b[i];   // 再次讀取，避免複製被省略
            Volatile.Write(ref _sink, s);
            bytes += (long)N * 8 * 2;
            double frac = sw.Elapsed.TotalSeconds / seconds;
            if (frac - lastReport >= 0.03) { report(frac); lastReport = frac; }
        }
        ct.ThrowIfCancellationRequested();

        double secs = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        return Math.Round(bytes / secs / 1_073_741_824.0, 1);
    }
}
