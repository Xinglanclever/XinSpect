using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>
/// SuperPI 圓周率運算：以 Chudnovsky 級數搭配二分裂解（binary splitting）計算圓周率至指定位數，
/// 為單執行緒純運算負載。「完成所需時間」即為分數（越短越好），常用於比較單核運算效能。
/// 計時範圍為實際算術（級數二分裂解＋√10005＋最終大除法）；不含把上億位結果轉為十進位字串輸出
/// （該步驟為超線性字串轉換、與圓周率運算無關，會遠比運算本身耗時且無意義）。
/// 前 40 位預覽直接由實際計算結果擷取，兼作演算法正確性核對。
/// </summary>
/// <remarks>
/// 耗時只有在同一位數之間才可相比，故紀錄簿以位數作設定簽章；比較對象是本機歷次成績
/// （<see cref="BenchLog"/>），不與任何外部分數對照。
/// </remarks>
public sealed class SuperPiService : ObservableObject
{
    // Chudnovsky 常數：π = 426880·√10005·Q / T
    private const int A = 13591409;
    private const int B = 545140134;

    /// <summary>紀錄簿中的項目代號。</summary>
    private const string KindPi = "superpi";

    private CancellationTokenSource? _cts;
    private readonly BenchLog _log;

    /// <summary>量測期間的實機條件（由每秒脈動餵入）。</summary>
    public BenchConditions Conditions { get; } = new();

    public SuperPiService(BenchLog? log = null) => _log = log ?? new BenchLog();

    // 可選位數：10萬 / 50萬 / 100萬 / 1000萬 / 5000萬 / 1億
    private int _digits = 100_000;
    public int Digits { get => _digits; set { if (SetProperty(ref _digits, value)) OnPropertyChanged(nameof(DigitsText)); } }
    public string DigitsText => $"{_digits:#,0} 位";

    public void SetDigits(int d) => Digits = d;

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _phase = "尚未計算";
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }

    private double _progress;
    public double ProgressFraction { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => _progress * 100;

    private string _elapsed = "—";
    public string ElapsedText { get => _elapsed; private set => SetProperty(ref _elapsed, value); }

    // ── 與本機歷次成績的對照（唯一誠實的基準）────────────────────────────────
    // 原先此處是「本次工作階段最佳」：關掉程式就沒了，也看不出當時的溫度與頻率。
    // 改為落地的紀錄簿之後，同位數的歷次耗時、離散度與量測條件都留得住。
    private string _delta = "", _repeat = "", _conditionText = "";

    /// <summary>耗時與本機上次同位數的比較。</summary>
    public string DeltaText { get => _delta; private set => SetProperty(ref _delta, value); }
    /// <summary>本機同位數的重複量測統計（次數／範圍／離散度）。</summary>
    public string RepeatText { get => _repeat; private set => SetProperty(ref _repeat, value); }
    /// <summary>本次量測期間的溫度／頻率條件；沒取到感測值時為空字串。</summary>
    public string ConditionText { get => _conditionText; private set => SetProperty(ref _conditionText, value); }

    private string _preview = "—";
    /// <summary>計算結果前數十位（供核對正確性：3.14159265358979…）。</summary>
    public string Preview { get => _preview; private set => SetProperty(ref _preview, value); }

    private string _status = "選擇位數後按「開始計算」。完成所需時間即為分數（越短越好）。5000萬／1億位極耗時且佔用大量記憶體。";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

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
        int digits = _digits;

        IsRunning = true;
        Phase = "計算中";
        ProgressFraction = 0;
        ElapsedText = "—";
        Preview = "計算中…";
        DeltaText = RepeatText = ConditionText = "";
        Conditions.Reset();
        StatusLine = $"正在計算圓周率至 {digits:#,0} 位…";

        var prog = new Progress<(double Frac, string Status)>(t => { ProgressFraction = t.Frac; StatusLine = t.Status; });
        var report = (IProgress<(double, string)>)prog;

        try
        {
            var sw = Stopwatch.StartNew();
            string pi = await Task.Run(() => ComputePi(digits, ct, report), ct);
            sw.Stop();

            double secs = sw.Elapsed.TotalSeconds;
            ElapsedText = $"{secs:0.000} 秒";
            Preview = pi.Length > 42 ? pi[..42] + "…" : pi;

            // 記入本機紀錄簿：耗時只有同位數之間才可相比，故以位數作設定簽章
            string config = $"{digits:#,0} 位";
            string cond = Conditions.Text();
            ConditionText = cond;
            Record(config, secs, cond);
            DeltaText = _log.DeltaText(KindPi, config);
            RepeatText = _log.Stats(KindPi, config).Text;

            Phase = "完成";
            ProgressFraction = 1;
            StatusLine = $"完成 ・ {digits:#,0} 位 ・ 歷時 {secs:0.000} 秒（僅計運算，不含十進位輸出）";
        }
        catch (OperationCanceledException)
        {
            Phase = "已停止";
            Preview = "—";
            StatusLine = "計算已停止（未完成的量測不列入紀錄）。";
        }
        catch (Exception ex)
        {
            Phase = "錯誤";
            Preview = "—";
            StatusLine = "計算失敗：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Record(string config, double seconds, string conditions)
    {
        try
        {
            _log.Add(new BenchRun
            {
                Kind = KindPi, Title = "SuperPI 圓周率", Config = config, Score = seconds, Unit = "秒",
                HigherIsBetter = false, Format = "0.000", UtcTime = DateTime.UtcNow, Conditions = conditions,
            });
        }
        catch { /* 紀錄失敗不影響已量到的成績 */ }
    }

    /// <summary>
    /// 以 Chudnovsky 級數＋二分裂解計算圓周率至 <paramref name="digits"/> 位，
    /// 回傳前 41 位的「3.1415…」字串（由實際結果擷取，非另行計算）。
    /// </summary>
    private static string ComputePi(int digits, CancellationToken ct, IProgress<(double, string)> report)
    {
        // 每項約貢獻 14.18 位有效數字；取 digits/14 略為超估項數（多算只增精度、不損正確性）。
        long terms = digits / 14 + 2;
        BigInteger c3over24 = BigInteger.Pow(640320, 3) / 24;   // = 10939058860032000
        long done = 0;

        report.Report((0.02, "計算級數（二分裂解）…"));

        // 二分裂解：回傳區間 [a,b) 的 (P, Q, T)，使部分和 = T / Q。
        (BigInteger P, BigInteger Q, BigInteger T) Split(long a, long b)
        {
            if (b - a == 1)
            {
                BigInteger pab, qab;
                if (a == 0) { pab = BigInteger.One; qab = BigInteger.One; }
                else
                {
                    pab = (BigInteger)(6 * a - 5) * (2 * a - 1) * (6 * a - 1);
                    qab = (BigInteger)a * a * a * c3over24;
                }
                BigInteger tab = pab * (A + (BigInteger)B * a);
                if ((a & 1) == 1) tab = -tab;

                done++;
                if ((done & 2047) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    report.Report((0.02 + 0.76 * (done / (double)terms), $"計算級數… {done:#,0}/{terms:#,0} 項"));
                }
                return (pab, qab, tab);
            }

            long m = (a + b) / 2;
            var (pl, ql, tl) = Split(a, m);
            var (pr, qr, tr) = Split(m, b);
            return (pl * pr, ql * qr, qr * tl + pl * tr);
        }

        // 最外層只需 Q 與 T；跳過根節點的 P 乘法（一次上億位乘法），節省時間與記憶體。
        BigInteger q, t;
        if (terms == 1)
        {
            var (_, q0, t0) = Split(0, 1);
            q = q0; t = t0;
        }
        else
        {
            long m = terms / 2;
            var (pl, ql, tl) = Split(0, m);
            var (_, qr, tr) = Split(m, terms);
            q = ql * qr;
            t = qr * tl + pl * tr;
        }

        ct.ThrowIfCancellationRequested();
        report.Report((0.80, "計算 √10005 …"));
        BigInteger one = BigInteger.Pow(10, digits);          // 定點單位 10^digits
        BigInteger sqrtC = ISqrt(10005 * one * one);          // = floor(√10005 · 10^digits)

        ct.ThrowIfCancellationRequested();
        report.Report((0.92, "最終大除法…"));
        BigInteger piScaled = q * 426880 * sqrtC / t;         // = floor(π · 10^digits)

        // 由實際結果擷取前 41 位（不全量轉字串）：floor(π·10^40) = piScaled / 10^(digits-40)
        report.Report((0.98, "整理輸出…"));
        BigInteger top = piScaled / (one / BigInteger.Pow(10, 40));
        string s = BigInteger.Abs(top).ToString();
        return s.Length <= 1 ? s : s[0] + "." + s[1..];
    }

    /// <summary>
    /// 整數平方根：回傳 floor(√n)。採用精度倍增遞迴（即 CPython <c>math.isqrt</c> 所用、已證明正確的演算法）：
    /// 每輪工作精度約加倍，早期除法僅在低位元寬進行，故總成本由最後一輪主導，
    /// 遠優於每輪皆全精度的樸素牛頓法（於上億位級別可快近一個數量級）。
    /// </summary>
    private static BigInteger ISqrt(BigInteger n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (n.IsZero) return BigInteger.Zero;

        long c = ((long)n.GetBitLength() - 1) / 2;
        BigInteger a = BigInteger.One;
        long d = 0;
        int cbl = c == 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)c);
        for (int s = cbl - 1; s >= 0; s--)
        {
            long e = d;
            d = c >> s;
            int shiftA = (int)(d - e - 1);
            int shiftN = (int)(2 * c - e - d + 1);
            a = (a << shiftA) + (n >> shiftN) / a;
        }
        // 收斂後 a 可能超估 1，最後修正為嚴格 floor。
        return a - (a * a > n ? BigInteger.One : BigInteger.Zero);
    }
}
