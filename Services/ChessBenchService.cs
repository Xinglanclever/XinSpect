using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>參考對照表的一列（示意，非實測）。</summary>
public sealed class ChessRefRow
{
    public ChessRefRow(string machine, string rate, string factor)
    {
        Machine = machine; Rate = rate; Factor = factor;
    }
    public string Machine { get; }
    public string Rate { get; }     // 約略節點速率
    public string Factor { get; }   // 相對倍率
}

/// <summary>
/// 原生象棋節點吞吐跑分：以 perft（西洋棋合法走法產生 + 走子/還原）作為確定性運算負載，
/// 量測「節點/秒（kN/s）」與相對倍率（×，同 Fritz 之單位精神）。
/// 每執行緒各持獨立引擎，無鎖無競爭，可選 1 / 全部核心 / 64 / 128 / 256 / 自訂執行緒（含超額訂閱）。
/// 另提供一鍵啟動內建原版 Fritz 作 16 執行緒對照。
/// </summary>
public sealed class ChessBenchService : ObservableObject
{
    // 曦覽 perft 基準（示意）：約當 Pentium III 1GHz 等級之節點速率，作為 1.0× 參考點。
    private const double BaselineKNps = 480.0;
    private const int PerftDepth = 4;         // 每回合 perft 深度（節點適中、時間檢查靈敏）
    private const double SingleSeconds = 3.0; // 單執行緒相位固定秒數

    private static long _sink;   // 防最佳化消除

    private CancellationTokenSource? _cts;

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
    public double? SingleKNps { get => _singleKNps; private set { if (SetProperty(ref _singleKNps, value)) { OnPropertyChanged(nameof(SingleText)); OnPropertyChanged(nameof(SingleFactorText)); } } }
    public string SingleText => _singleKNps is double v ? $"{v:#,0} kN/s" : "—";
    public string SingleFactorText => _singleKNps is double v ? $"{v / BaselineKNps:0.0}×" : "—";

    private double? _multiKNps;
    public double? MultiKNps { get => _multiKNps; private set { if (SetProperty(ref _multiKNps, value)) { OnPropertyChanged(nameof(MultiText)); OnPropertyChanged(nameof(MultiFactorText)); } } }
    public string MultiText => _multiKNps is double v ? $"{v:#,0} kN/s" : "—";
    public string MultiFactorText => _multiKNps is double v ? $"{v / BaselineKNps:0.0}×" : "—";

    private double? _speedup;
    public double? Speedup { get => _speedup; private set { if (SetProperty(ref _speedup, value)) OnPropertyChanged(nameof(SpeedupText)); } }
    public string SpeedupText => _speedup is double v ? $"{v:0.0}×（多／單）" : "—";

    private double _bestFactor;
    public double BestFactor { get => _bestFactor; private set { if (SetProperty(ref _bestFactor, value)) OnPropertyChanged(nameof(BestText)); } }
    public string BestText => _bestFactor > 0 ? $"本次工作階段最佳：{_bestFactor:0.0}×" : "本次工作階段尚無紀錄";

    private string _fritzStatus = "可啟動內建原版 Fritz Chess Benchmark 作 16 執行緒對照。";
    public string FritzStatus { get => _fritzStatus; private set => SetProperty(ref _fritzStatus, value); }

    /// <summary>參考對照（示意，非實測）——僅供理解倍率量級，實際數值以本機實測為準。</summary>
    public IReadOnlyList<ChessRefRow> Reference { get; } = new List<ChessRefRow>
    {
        new("Pentium III 1.0 GHz（曦覽基準 1.0×，示意）", "≈ 480 kN/s", "1.0×"),
        new("雙核心筆電（示意）", "≈ 4,000 kN/s", "≈ 8×"),
        new("四核心桌機（示意）", "≈ 12,000 kN/s", "≈ 25×"),
        new("八核心桌機（示意）", "≈ 30,000 kN/s", "≈ 60×"),
        new("16C／32T 工作站（示意）", "≈ 80,000 kN/s", "≈ 165×"),
        new("32C／64T 伺服器（示意）", "≈ 160,000 kN/s", "≈ 330×"),
    };

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

        IsRunning = true;
        Phase = "準備中";
        ProgressFraction = 0;
        StatusLine = "跑分進行中，請避免其他高負載程式以取得穩定結果…";
        SingleKNps = MultiKNps = Speedup = null;

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
            double factor = multi / 1000.0 / BaselineKNps;
            if (factor > BestFactor) BestFactor = factor;

            Phase = "完成";
            ProgressFraction = 1;
            StatusLine = $"跑分完成 ・ 多執行緒 {multi / 1000.0:#,0} kN/s ・ {factor:0.0}×（相對曦覽 perft 基準，示意）";
        }
        catch (OperationCanceledException)
        {
            Phase = "已取消";
            StatusLine = "跑分已取消。";
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
