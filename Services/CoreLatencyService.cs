using System.Diagnostics;

namespace XinSpect;

/// <summary>
/// 核心到核心延遲矩陣：兩個執行緒各自釘在指定的邏輯處理器上，對同一條快取線做原子交換
/// （一方寫入、另一方偵測到後回寫），量測往返延遲；跑滿 N×N 組合後呈現為熱圖。
/// SMT 兄弟（共用同一實體核）延遲極低、同 mesh 較低、跨 mesh／跨 tile 較高——拓樸因此浮現。
/// </summary>
/// <remarks>
/// 誠實界線：量的是「快取線經由原子交換往返」的時間（中位數，ns），不是記憶體延遲、
/// 也不是任何廠商定義的指標。釘選走 <see cref="CpuAffinity"/>，跨處理器群組（＞64 邏輯處理器）
/// 的機器也會全部列入——但多群組路徑<b>未在真實硬體上驗證過</b>，狀態列會如實聲明。
/// 量測期間參與的兩顆邏輯處理器會滿載（忙等），屬測試本質，狀態列會先告知。
/// </remarks>
public sealed class CoreLatencyService : ObservableObject
{
    private CancellationTokenSource? _cts;

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running && IsSupported;

    /// <summary>是否可執行：需要至少兩個邏輯處理器（跨群組的機器亦可，見類別備註的誠實界線）。</summary>
    public static bool IsSupported => CpuAffinity.TotalLogicalProcessors >= 2;

    private string _phase = "尚未量測";
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }

    private double _progress;
    public double ProgressFraction { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => _progress * 100;

    private string _status = "按「開始量測」跑滿全部邏輯處理器兩兩組合（全程約數秒，期間對應核心滿載）。";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

    private int[] _lps = [];
    /// <summary>
    /// 實際參與量測的邏輯處理器編號（全機序號，跨群組時為連續編號）；熱圖的行列順序與此一致。
    /// </summary>
    /// <remarks>必須走 SetProperty：熱圖的 Lps 是獨立繫結，不通知就永遠是空陣列，
    /// 而 Data 已經是 N×N——標籤索引會越界（1.4.0 的 IndexOutOfRangeException 即出於此）。</remarks>
    public int[] Lps { get => _lps; private set => SetProperty(ref _lps, value); }

    private double[,]? _matrixNs;
    /// <summary>延遲矩陣（ns，往返）；對角線為 NaN（自己對自己不量）。</summary>
    public double[,]? MatrixNs { get => _matrixNs; private set { if (SetProperty(ref _matrixNs, value)) OnPropertyChanged(nameof(HasData)); } }
    public bool HasData => _matrixNs is not null;

    private string _minText = "—", _medianText = "—", _maxText = "—";
    /// <summary>全矩陣（不含對角線）的最小／中位／最大值。最小值通常落在 SMT 兄弟之間。</summary>
    public string MinText { get => _minText; private set => SetProperty(ref _minText, value); }
    public string MedianText { get => _medianText; private set => SetProperty(ref _medianText, value); }
    public string MaxText { get => _maxText; private set => SetProperty(ref _maxText, value); }

    public void Start()
    {
        if (IsRunning) return;
        _ = RunAsync();
    }

    public void Cancel() => _cts?.Cancel();

    private async Task RunAsync()
    {
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            Phase = "量測中";
            StatusLine = "量測中…（期間對應核心滿載）";
            var (matrix, lps) = await Task.Run(() => MeasureAll(ct));
            Lps = lps;          // 先給行列標籤，再給矩陣：Data 的變更會立刻觸發一次繪製
            MatrixNs = matrix;
            var (min, med, max) = Stats(matrix);
            MinText = $"{min:0} ns";
            MedianText = $"{med:0} ns";
            MaxText = $"{max:0} ns";
            Phase = "完成";
            StatusLine = $"完成 ・ {lps.Length} 個邏輯處理器、{lps.Length * (lps.Length - 1)} 組兩兩量測。"
                       + (CpuAffinity.IsMultiGroup
                            ? $" 本機有 {CpuAffinity.GroupCount} 個處理器群組，全部群組皆已列入；多群組路徑未在實機驗證過。"
                            : "");
        }
        catch (OperationCanceledException)
        {
            Phase = "已取消";
            StatusLine = "已取消量測。";
        }
        catch (Exception ex)
        {
            Phase = "失敗";
            StatusLine = "量測失敗：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts = null;
        }
    }

    private (double[,] Matrix, int[] Lps) MeasureAll(CancellationToken ct)
    {
        var refs = CpuAffinity.AllLogicalProcessors();
        if (refs.Count < 2)
            throw new InvalidOperationException("可用的邏輯處理器少於兩個，無法量測。");

        // 先在本執行緒逐一試釘一遍：釘不上的核心必須在開工前就剔除。
        // 若留到量測執行緒裡才發現，對方會永遠等不到旗標翻轉——Join 卡死，整頁凍結。
        var usable = new List<ProcessorRef>(refs.Count);
        foreach (var p in refs)
        {
            using var probe = CpuAffinity.Pinned(p);
            if (probe.Ok) usable.Add(p);
            else Diag.Swallow("核心延遲釘選試探", null, $"{p.Label(true)} 釘不上，已從矩陣剔除");
        }
        if (usable.Count < 2)
            throw new InvalidOperationException("能成功釘選的邏輯處理器少於兩個，無法量測。");

        var sizes = CpuAffinity.GroupSizes();
        var labels = new int[usable.Count];
        for (int i = 0; i < usable.Count; i++)
            labels[i] = CpuAffinity.Global(usable[i], sizes) ?? i;

        int n = usable.Count;
        var m = new double[n, n];
        int total = n * (n - 1), done = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i == j) { m[i, j] = double.NaN; continue; }
                ct.ThrowIfCancellationRequested();
                m[i, j] = MeasurePair(usable[i], usable[j], ct);
                done++;
                ProgressFraction = done / (double)total;
                if (done % 32 == 0)
                    StatusLine = $"量測中… {done} / {total} 組（目前最小 {Stats(m).min:0} ns）";
            }
        }
        return (m, labels);
    }

    /// <summary>
    /// 單一組合的往返延遲（ns，中位數）。執行緒各自把自己釘在目標邏輯處理器上（GetCurrentThread
    /// 是偽控制代碼，必須在執行緒自己身上呼叫才釘得到），拉高優先權降低排程雜訊；
    /// 先跑一輪暖機不計，其後取多輪批次平均值的中位數。
    /// </summary>
    /// <remarks>
    /// 釘選在此為<b>盡力而為</b>：呼叫端已在 <see cref="MeasureAll"/> 試釘過一遍，此處再失敗也不丟例外——
    /// 在忙等執行緒裡丟例外會讓對方永遠等不到旗標，Join 直接卡死。釘不上就照量，並如實記進診斷。
    /// </remarks>
    private static double MeasurePair(ProcessorRef lpA, ProcessorRef lpB, CancellationToken ct)
    {
        const long Iters = 512;
        const int Batches = 3;   // 另有一輪暖機不計
        long flag = 0;
        var avgs = new List<double>(Batches);

        for (int b = 0; b <= Batches; b++)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();

            var ta = new Thread(() =>
            {
                using var pin = Pin(lpA);
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                for (long i = 1; i <= Iters; i++)
                {
                    Volatile.Write(ref flag, i);
                    long release = -i;
                    while (Volatile.Read(ref flag) != release) { }
                }
            });
            var tb = new Thread(() =>
            {
                using var pin = Pin(lpB);
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                for (long i = 1; i <= Iters; i++)
                {
                    long v;
                    while ((v = Volatile.Read(ref flag)) != i) { }
                    Volatile.Write(ref flag, -i);
                }
            });
            ta.Start(); tb.Start();
            ta.Join(); tb.Join();
            sw.Stop();

            if (b > 0) avgs.Add(sw.Elapsed.TotalMilliseconds * 1e6 / Iters);   // ms → ns／次
        }
        return Median(avgs);
    }

    private static CpuAffinity.Pin Pin(ProcessorRef p)
    {
        var pin = CpuAffinity.Pinned(p);
        if (!pin.Ok) Diag.Swallow("核心延遲釘選", null, $"{p.Label(true)} 釘不上，該列數值不代表該核心");
        return pin;
    }

    // ── 純函式（單元測試涵蓋）──────────────────────────────────────────────

    /// <summary>由親和性遮罩展開出邏輯處理器編號（由低到高）。</summary>
    /// <remarks>
    /// 單一群組的遮罩語意；跨群組的列舉走 <see cref="CpuAffinity.AllLogicalProcessors"/>。
    /// 實作轉呼 <see cref="CpuAffinity.IndicesFromMask"/>，避免同一段位元展開存在兩份。
    /// </remarks>
    public static List<int> LogicalProcessorsFromMask(ulong mask) => CpuAffinity.IndicesFromMask(mask);

    /// <summary>中位數（偶數取兩中間值平均）。輸入須非空。</summary>
    public static double Median(IEnumerable<double> xs)
    {
        var a = xs.ToArray();
        if (a.Length == 0) throw new ArgumentException("中位數需要至少一個樣本。", nameof(xs));
        Array.Sort(a);
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2.0;
    }

    /// <summary>矩陣統計：不含對角線（NaN）的最小／中位／最大。</summary>
    public static (double min, double median, double max) Stats(double[,] m)
    {
        var vals = new List<double>();
        for (int i = 0; i < m.GetLength(0); i++)
            for (int j = 0; j < m.GetLength(1); j++)
                if (i != j && double.IsFinite(m[i, j])) vals.Add(m[i, j]);
        if (vals.Count == 0) return (double.NaN, double.NaN, double.NaN);
        vals.Sort();
        double median = vals.Count % 2 == 1
            ? vals[vals.Count / 2]
            : (vals[vals.Count / 2 - 1] + vals[vals.Count / 2]) / 2.0;
        return (vals[0], median, vals[^1]);
    }
}
