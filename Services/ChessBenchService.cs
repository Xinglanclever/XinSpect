using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>
/// 原生象棋節點吞吐跑分：以 perft（西洋棋合法走法產生 + 走子/還原）作為確定性運算負載，
/// 量測「節點/秒（kN/s）」。每執行緒各持獨立引擎，無鎖無競爭，
/// 可選 1 / 全部核心 / 64 / 128 / 256 / 自訂執行緒（含超額訂閱）。
/// 另提供一鍵啟動內建原版 Fritz 作 16 執行緒對照。
/// </summary>
/// <remarks>
/// 本測試不提供跨機器的參考分數。曾經內建的「約當 Pentium III 1 GHz = 1.0×」基準與各機型對照表
/// 都不是量出來的，只是把猜測寫得像量測；且本測試的 perft 負載與原版 Fritz 的引擎不同，
/// 兩者的「×」不可互換。可信的對照有兩條路：一是本機歷次成績（<see cref="BenchLog"/>），
/// 二是在同一台機器上實跑原版 Fritz（下方按鈕）自行比對。
/// </remarks>
public sealed class ChessBenchService : ObservableObject
{
    private const int PerftDepth = 4;         // 每回合 perft 深度（節點適中、時間檢查靈敏）
    private const double SingleSeconds = 3.0; // 單執行緒相位固定秒數

    /// <summary>紀錄簿中的項目代號。</summary>
    private const string KindSingle = "chess.single", KindMulti = "chess.multi";

    private static long _sink;   // 防最佳化消除

    private CancellationTokenSource? _cts;
    private readonly BenchLog _log;

    /// <summary>量測期間的實機條件（由每秒脈動餵入）。</summary>
    public BenchConditions Conditions { get; } = new();

    public ChessBenchService(BenchLog? log = null) => _log = log ?? new BenchLog();

    public int LogicalCores => Environment.ProcessorCount;


    private int _threads = Environment.ProcessorCount;
    /// <summary>多執行緒相位採用的執行緒數（1–4096，可超額訂閱實體核心）。</summary>
    public int ThreadCount
    {
        get => _threads;
        set { if (SetProperty(ref _threads, Math.Clamp(value, 1, 4096))) OnPropertyChanged(nameof(ThreadText)); }
    }
    public string ThreadText => $"{_threads} 執行緒" + (_threads > LogicalCores ? $"（超額，本機 {LogicalCores} 邏輯核心）" : "");

    private int _duration = 10;
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

    private string _status = "選擇執行緒數與時間後按「開始跑分」。原生 perft 節點運算，無執行緒上限。";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

    private double? _singleKNps;
    public double? SingleKNps { get => _singleKNps; private set { if (SetProperty(ref _singleKNps, value)) OnPropertyChanged(nameof(SingleText)); } }
    public string SingleText => _singleKNps is double v ? $"{v:#,0} kN/s" : "—";

    private double? _multiKNps;
    public double? MultiKNps { get => _multiKNps; private set { if (SetProperty(ref _multiKNps, value)) OnPropertyChanged(nameof(MultiText)); } }
    public string MultiText => _multiKNps is double v ? $"{v:#,0} kN/s" : "—";

    private double? _speedup;
    public double? Speedup { get => _speedup; private set { if (SetProperty(ref _speedup, value)) { OnPropertyChanged(nameof(SpeedupText)); OnPropertyChanged(nameof(EfficiencyText)); } } }
    public string SpeedupText => _speedup is double v ? $"{v:0.0}×（多／單）" : "—";

    /// <summary>平行效率＝加速比／執行緒數；超額訂閱時本就不可能接近 100%，如實呈現。</summary>
    public string EfficiencyText => _speedup is double v && _threadsUsed > 0
        ? $"平行效率 {v / _threadsUsed * 100:0}%（{_threadsUsed} 執行緒）" : "—";

    private int _threadsUsed;

    // ── 與本機歷次成績的對照（唯一誠實的基準）────────────────────────────────
    private string _singleDelta = "", _multiDelta = "", _repeat = "", _conditionText = "";

    /// <summary>單執行緒成績與本機上次同設定的比較。</summary>
    public string SingleDeltaText { get => _singleDelta; private set => SetProperty(ref _singleDelta, value); }
    /// <summary>多執行緒成績與本機上次同設定的比較。</summary>
    public string MultiDeltaText { get => _multiDelta; private set => SetProperty(ref _multiDelta, value); }
    /// <summary>本機同設定的重複量測統計（次數／範圍／離散度）。</summary>
    public string RepeatText { get => _repeat; private set => SetProperty(ref _repeat, value); }
    /// <summary>本次量測期間的溫度／頻率條件；沒取到感測值時為空字串。</summary>
    public string ConditionText { get => _conditionText; private set => SetProperty(ref _conditionText, value); }

    private string _fritzStatus = "可啟動內建原版 Fritz Chess Benchmark 作 16 執行緒對照。";
    public string FritzStatus { get => _fritzStatus; private set => SetProperty(ref _fritzStatus, value); }


    public void SetThreads(int t) => ThreadCount = t;
    public void SetDuration(int s) => DurationSeconds = s;

    public void Start()
    {
        if (IsRunning) return;
        _ = RunAsync();
    }

    public void Cancel() => _cts?.Cancel();

    public void LaunchOriginalFritz()
    {
        try { FritzStatus = FritzLauncher.Launch(); }
        catch (Exception ex) { FritzStatus = "啟動原版 Fritz 失敗：" + ex.Message; }
    }

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        int threads = ThreadCount;
        _threadsUsed = threads;
        string config = $"{threads} 執行緒 ・ {DurationSeconds} 秒 ・ 深度 {PerftDepth}";

        IsRunning = true;
        Phase = "準備中";
        ProgressFraction = 0;
        StatusLine = "跑分進行中，請避免其他高負載程式以取得穩定結果…";
        SingleKNps = MultiKNps = Speedup = null;
        SingleDeltaText = MultiDeltaText = RepeatText = ConditionText = "";
        Conditions.Reset();

        var prog = new Progress<double>(p => ProgressFraction = Math.Clamp(p, 0, 1));
        var ip = (IProgress<double>)prog;
        Action<double> Band(double lo, double hi) => p => ip.Report(lo + (hi - lo) * Math.Clamp(p, 0, 1));

        try
        {
            Phase = "暖機";
            await Task.Run(() => { var e = new ChessEngine(); Volatile.Write(ref _sink, e.PerftNodes(PerftDepth)); }, ct);
            ct.ThrowIfCancellationRequested();

            Phase = "單執行緒 perft";
            double single = await Task.Run(() => RunChess(1, SingleSeconds, ct, Band(0.02, 0.30)), ct);
            SingleKNps = single / 1000.0;
            ct.ThrowIfCancellationRequested();

            Phase = $"多執行緒 perft（{threads} 執行緒）";
            double multi = await Task.Run(() => RunChess(threads, DurationSeconds, ct, Band(0.30, 1.0)), ct);
            MultiKNps = multi / 1000.0;

            Speedup = single > 0 ? multi / single : 0;

            // 記入本機紀錄簿，並據此給出「與上次相比」「重複性」——比較對象只有這台機器自己
            string cond = Conditions.Text();
            string singleConfig = $"單執行緒 ・ 深度 {PerftDepth}";
            ConditionText = cond;
            Record(KindSingle, "象棋 單執行緒", singleConfig, single / 1000.0, cond);
            Record(KindMulti, "象棋 多執行緒", config, multi / 1000.0, cond);
            SingleDeltaText = _log.DeltaText(KindSingle, singleConfig);
            MultiDeltaText = _log.DeltaText(KindMulti, config);
            RepeatText = _log.Stats(KindMulti, config).Text;

            Phase = "完成";
            ProgressFraction = 1;
            StatusLine = $"跑分完成 ・ 多執行緒 {multi / 1000.0:#,0} kN/s ・ {RepeatText}";
        }
        catch (OperationCanceledException)
        {
            Phase = "已取消";
            StatusLine = "跑分已取消（未完成的量測不列入紀錄）。";
        }
        catch (Exception ex)
        {
            Phase = "錯誤";
            StatusLine = "跑分失敗：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Record(string kind, string title, string config, double kNps, string conditions)
    {
        try
        {
            _log.Add(new BenchRun
            {
                Kind = kind, Title = title, Config = config, Score = kNps, Unit = "kN/s",
                HigherIsBetter = true, Format = "#,0", UtcTime = DateTime.UtcNow, Conditions = conditions,
            });
        }
        catch { /* 紀錄失敗不影響已量到的成績 */ }
    }

    /// <summary>以指定執行緒數在固定時間內反覆 perft，回傳合計節點/秒。</summary>
    private static double RunChess(int threads, double seconds, CancellationToken ct, Action<double> report)
    {
        var sw = Stopwatch.StartNew();
        var counts = new long[threads];
        var workers = new Thread[threads];

        for (int t = 0; t < threads; t++)
        {
            int id = t;
            workers[t] = new Thread(() =>
            {
                var engine = new ChessEngine();
                long local = 0;
                double lastReport = 0;
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    if (ct.IsCancellationRequested) break;
                    engine.Reset();
                    local += engine.PerftNodes(PerftDepth);
                    if (id == 0)
                    {
                        double frac = sw.Elapsed.TotalSeconds / seconds;
                        if (frac - lastReport >= 0.03) { report(frac); lastReport = frac; }
                    }
                }
                counts[id] = local;
            })
            { IsBackground = true, Priority = ThreadPriority.Highest, Name = $"XinChess#{id}" };
        }

        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();
        ct.ThrowIfCancellationRequested();

        double secs = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        long total = 0;
        foreach (var c in counts) total += c;
        return total / secs;
    }
}
