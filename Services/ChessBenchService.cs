using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>棋類跑分可選的引擎。兩者的分數<b>不可互比</b>：走子規則與每節點工作量都不同。</summary>
public enum BoardEngineKind
{
    /// <summary>西洋棋：滑行子直線掃描、升變分支、易位與過路兵。</summary>
    Chess,
    /// <summary>中國象棋：炮的砲架雙段掃描、蹩馬腿、象眼、九宮與白臉將。</summary>
    Xiangqi,
}

/// <summary>
/// 原生棋類節點吞吐跑分：以 perft（合法走法產生 + 走子/還原）作為確定性整數負載，
/// 量測「節點/秒（kN/s）」，並且<b>順便驗算這台機器有沒有算錯</b>。
/// 可選中國象棋或西洋棋兩種引擎；每執行緒各持獨立引擎，無鎖無競爭，
/// 執行緒數可選 1 / 全部核心 / 64 / 128 / 256 / 自訂（含超額訂閱）。
/// </summary>
/// <remarks>
/// <para><b>為什麼這個跑分能自我驗證。</b>perft 在固定局面、固定深度下的葉節點數是<b>數學常數</b>
/// （西洋棋起始局面第 4 層恆為 197,281；中國象棋第 3 層恆為 79,666），與 CPU 型號、時脈、
/// 執行緒數都無關。所以每一輪運算的結果都可以當成檢核碼：算出別的數字，代表這台機器<b>算錯了</b>，
/// 而不是它比較慢。這對一支同時內建超頻功能的程式特別有用——不穩的超頻多半先表現為
/// 極少數的靜默計算錯誤，而 perft 這種分支密集的整數負載，出錯的方式跟 Prime95 的 FFT 不一樣，
/// 兩者互補而非重複。</para>
/// <para><b>為什麼沒有跨機器參考分數。</b>曾經內建的「約當 Pentium III 1 GHz = 1.0×」基準與各機型
/// 對照表都不是量出來的，只是把猜測寫得像量測，已整批移除。可信的對照有兩條路：一是本機歷次成績
/// （<see cref="BenchLog"/>，同引擎同設定才比），二是在兩台機器上各跑一次同一個引擎與深度自行比對——
/// 後者之所以成立，正因為兩邊算的是同一個常數，工作量嚴格相等。</para>
/// </remarks>
public sealed class ChessBenchService : ObservableObject
{
    private const double SingleSeconds = 3.0; // 單執行緒相位固定秒數

    /// <summary>紀錄簿中的項目代號（沿用舊代號，本機歷史成績才不會斷掉）。</summary>
    private const string KindSingle = "chess.single", KindMulti = "chess.multi";

    /// <summary>西洋棋起始局面第 1–4 層的葉節點數（公認值）。</summary>
    internal static readonly long[] ChessLadder = { 20L, 400L, 8_902L, 197_281L };
    /// <summary>中國象棋起始局面第 1–3 層的葉節點數（公認值）。</summary>
    internal static readonly long[] XiangqiLadder = { 44L, 1_920L, 79_666L };

    /// <summary>各引擎跑分採用的深度與該深度應得的節點數。深度取「單輪數十毫秒」以兼顧時間檢查靈敏度。</summary>
    internal static (int Depth, long Leaves, long[] Ladder) Spec(BoardEngineKind kind) => kind == BoardEngineKind.Chess
        ? (4, ChessLadder[3], ChessLadder)
        : (3, XiangqiLadder[2], XiangqiLadder);

    private static IPerftEngine NewEngine(BoardEngineKind kind) => kind == BoardEngineKind.Chess
        ? new ChessEngine()
        : new XiangqiEngine();

    /// <summary>引擎的顯示名稱。</summary>
    internal static string NameOf(BoardEngineKind kind) => kind == BoardEngineKind.Chess ? "西洋棋" : "中國象棋";

    private static long _sink;   // 防最佳化消除

    private CancellationTokenSource? _cts;
    private readonly BenchLog _log;

    /// <summary>量測期間的實機條件（由每秒脈動餵入）。</summary>
    public BenchConditions Conditions { get; } = new();

    public ChessBenchService(BenchLog? log = null) => _log = log ?? new BenchLog();

    public int LogicalCores => Environment.ProcessorCount;

    // ── 引擎選擇 ──────────────────────────────────────────────────────────────

    private BoardEngineKind _engine = BoardEngineKind.Xiangqi;
    /// <summary>採用的棋種。預設中國象棋——這是一支繁體中文原生的程式，且它的分支型態更複雜。</summary>
    public BoardEngineKind Engine
    {
        get => _engine;
        set
        {
            if (IsRunning || !SetProperty(ref _engine, value)) return;
            OnPropertyChanged(nameof(EngineName));
            OnPropertyChanged(nameof(EngineNote));
            OnPropertyChanged(nameof(IsChess));
            OnPropertyChanged(nameof(IsXiangqi));
        }
    }
    public bool IsChess => _engine == BoardEngineKind.Chess;
    public bool IsXiangqi => _engine == BoardEngineKind.Xiangqi;
    public string EngineName => NameOf(_engine);

    /// <summary>這個引擎壓的是什麼，以及本次採用的深度與應得的節點數。</summary>
    public string EngineNote
    {
        get
        {
            var (depth, leaves, _) = Spec(_engine);
            string what = _engine == BoardEngineKind.Chess
                ? "滑行子直線掃描、兵升變分支、易位與吃過路兵"
                : "炮的砲架雙段掃描、蹩馬腿、象眼與不得過河、九宮限制、白臉將";
            return $"{EngineName}：{what}。本次每輪數第 {depth} 層，應得 {leaves:#,0} 個葉節點。";
        }
    }

    public void SetEngine(BoardEngineKind kind) => Engine = kind;


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

    private string _status = "選擇棋種、執行緒數與時間後按「開始跑分」。原生 perft 節點運算，無執行緒上限，且會逐輪驗算結果對不對。";
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

    // ── 運算正確性（取代舊的「原版對照」）──────────────────────────────────
    // perft 節點數是常數，所以跑分本身就是一次算術驗證。這裡把驗證結果與速度並列，
    // 因為一個「算錯的高分」毫無意義，而使用者有權在看到分數的同一眼看到它可不可信。

    private string _verifyText = "";
    /// <summary>開跑前的逐層自檢結果（第 1 層到本次深度全部核對一次）。</summary>
    public string VerifyText { get => _verifyText; private set => SetProperty(ref _verifyText, value); }

    private string _integrityText = "";
    /// <summary>計時期間逐輪核對的結果：核對幾次、其中幾次算錯。</summary>
    public string IntegrityText { get => _integrityText; private set => SetProperty(ref _integrityText, value); }

    private bool _hasFault;
    /// <summary>本次量測期間出現過節點數不符（＝這台機器算錯了）。</summary>
    public bool HasFault { get => _hasFault; private set => SetProperty(ref _hasFault, value); }

    public void SetThreads(int t) => ThreadCount = t;
    public void SetDuration(int s) => DurationSeconds = s;

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
        var kind = Engine;
        var (depth, leaves, _) = Spec(kind);
        int threads = ThreadCount;
        _threadsUsed = threads;
        string config = $"{threads} 執行緒 ・ {DurationSeconds} 秒 ・ {NameOf(kind)} ・ 深度 {depth}";

        IsRunning = true;
        Phase = "準備中";
        ProgressFraction = 0;
        StatusLine = "跑分進行中，請避免其他高負載程式以取得穩定結果…";
        SingleKNps = MultiKNps = Speedup = null;
        SingleDeltaText = MultiDeltaText = RepeatText = ConditionText = "";
        VerifyText = IntegrityText = "";
        HasFault = false;
        Conditions.Reset();

        var prog = new Progress<double>(p => ProgressFraction = Math.Clamp(p, 0, 1));
        var ip = (IProgress<double>)prog;
        Action<double> Band(double lo, double hi) => p => ip.Report(lo + (hi - lo) * Math.Clamp(p, 0, 1));

        try
        {
            // 先驗算再計時：若這台機器連第 1 層都算錯，後面量到的每一個數字都不值得看。
            Phase = "正確性自檢";
            var check = await Task.Run(() => SelfCheck(kind), ct);
            VerifyText = check.Text;
            HasFault = !check.Ok;
            ct.ThrowIfCancellationRequested();
            if (!check.Ok)
            {
                Phase = "運算錯誤";
                ProgressFraction = 0;
                StatusLine = "已中止：正確性自檢未通過。" + check.Text;
                return;
            }

            Phase = "暖機";
            await Task.Run(() => { var e = NewEngine(kind); Volatile.Write(ref _sink, e.PerftLeaves(depth)); }, ct);
            ct.ThrowIfCancellationRequested();

            Phase = "單執行緒 perft";
            var one = await Task.Run(() => RunBoard(kind, 1, SingleSeconds, ct, Band(0.02, 0.30)), ct);
            SingleKNps = one.NodesPerSec / 1000.0;
            ct.ThrowIfCancellationRequested();

            Phase = $"多執行緒 perft（{threads} 執行緒）";
            var all = await Task.Run(() => RunBoard(kind, threads, DurationSeconds, ct, Band(0.30, 1.0)), ct);
            MultiKNps = all.NodesPerSec / 1000.0;

            Speedup = one.NodesPerSec > 0 ? all.NodesPerSec / one.NodesPerSec : 0;

            long rounds = one.Rounds + all.Rounds, faults = one.Faults + all.Faults;
            HasFault = faults > 0;
            IntegrityText = faults == 0
                ? $"運算正確性 ✓ 逐輪核對 {rounds:#,0} 輪，節點數全部等於常數 {leaves:#,0}。"
                : $"⚠ {rounds:#,0} 輪中有 {faults:#,0} 輪節點數不等於 {leaves:#,0}。"
                  + "perft 節點數是數學常數，算出別的數就是這台機器算錯了——先查超頻穩定度、記憶體與散熱，分數暫時不必看。";

            // 記入本機紀錄簿；比較對象只有這台機器自己，且同引擎同深度才算同一件事
            string cond = Conditions.Text();
            string singleConfig = $"單執行緒 ・ {NameOf(kind)} ・ 深度 {depth}";
            ConditionText = cond;
            Record(KindSingle, $"{NameOf(kind)} 單執行緒", singleConfig, one.NodesPerSec / 1000.0, cond);
            Record(KindMulti, $"{NameOf(kind)} 多執行緒", config, all.NodesPerSec / 1000.0, cond);
            SingleDeltaText = _log.DeltaText(KindSingle, singleConfig);
            MultiDeltaText = _log.DeltaText(KindMulti, config);
            RepeatText = _log.Stats(KindMulti, config).Text;

            Phase = faults == 0 ? "完成" : "完成（但算錯）";
            ProgressFraction = 1;
            StatusLine = faults == 0
                ? $"跑分完成 ・ 多執行緒 {all.NodesPerSec / 1000.0:#,0} kN/s ・ {RepeatText}"
                : "跑分跑完了，但期間偵測到運算錯誤，這個分數不可信。詳見下方運算正確性。";
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

    /// <summary>一段計時量測的結果：節點速率，以及期間逐輪核對的輪數與算錯的輪數。</summary>
    private readonly record struct BoardResult(double NodesPerSec, long Rounds, long Faults);

    /// <summary>
    /// 開跑前的逐層自檢：第 1 層一路核對到本次採用的深度。
    /// 便宜（合計不到二十萬個節點）卻能在計時前就抓到「這台機器根本算錯」的情形。
    /// </summary>
    internal static (bool Ok, string Text) SelfCheck(BoardEngineKind kind)
    {
        var (depth, _, ladder) = Spec(kind);
        var engine = NewEngine(kind);
        for (int d = 1; d <= depth; d++)
        {
            engine.Reset();
            long got = engine.PerftLeaves(d);
            if (got != ladder[d - 1])
                return (false, $"⚠ {NameOf(kind)} 第 {d} 層應為 {ladder[d - 1]:#,0} 個節點，實得 {got:#,0}。"
                             + "這是運算錯誤而非效能問題：perft 節點數是數學常數，與 CPU 型號、時脈、執行緒數都無關。"
                             + "請先確認超頻／記憶體／散熱是否穩定。");
        }
        string list = string.Join(" / ", ladder.Take(depth).Select(v => v.ToString("#,0")));
        return (true, $"正確性自檢通過：{NameOf(kind)} 第 1–{depth} 層節點數 {list} 全部相符。");
    }

    /// <summary>
    /// 以指定執行緒數在固定時間內反覆 perft，回傳合計節點/秒；同時逐輪核對節點數。
    /// </summary>
    /// <remarks>
    /// 核對是免費的：每一輪本來就會算出一個節點數，只是以前把它加進總和就丟掉。
    /// 拿它跟常數比一下，這個跑分就同時是一支靜默計算錯誤偵測器。
    /// </remarks>
    private static BoardResult RunBoard(BoardEngineKind kind, int threads, double seconds,
                                       CancellationToken ct, Action<double> report)
    {
        var (depth, expect, _) = Spec(kind);
        var sw = Stopwatch.StartNew();
        var counts = new long[threads];
        var rounds = new long[threads];
        var faults = new long[threads];
        var workers = new Thread[threads];

        for (int t = 0; t < threads; t++)
        {
            int id = t;
            workers[t] = new Thread(() =>
            {
                var engine = NewEngine(kind);
                long local = 0, r = 0, bad = 0;
                double lastReport = 0;
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    if (ct.IsCancellationRequested) break;
                    engine.Reset();
                    long got = engine.PerftLeaves(depth);
                    local += got;
                    r++;
                    if (got != expect) bad++;
                    if (id == 0)
                    {
                        double frac = sw.Elapsed.TotalSeconds / seconds;
                        if (frac - lastReport >= 0.03) { report(frac); lastReport = frac; }
                    }
                }
                counts[id] = local; rounds[id] = r; faults[id] = bad;
            })
            { IsBackground = true, Priority = ThreadPriority.Highest, Name = $"XinBoard#{id}" };
        }

        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();
        ct.ThrowIfCancellationRequested();

        double secs = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        long total = 0, allRounds = 0, allFaults = 0;
        for (int i = 0; i < threads; i++) { total += counts[i]; allRounds += rounds[i]; allFaults += faults[i]; }
        return new BoardResult(total / secs, allRounds, allFaults);
    }
}
