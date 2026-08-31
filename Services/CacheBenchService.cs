using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>單一工作集大小的延遲量測結果（供列表 / 長條視覺化）。</summary>
public sealed class CacheLatencyRow
{
    public CacheLatencyRow(string sizeText, double latencyNs, double barFraction)
    {
        SizeText = sizeText;
        LatencyNs = latencyNs;
        BarFraction = barFraction;
    }
    public string SizeText { get; }
    public double LatencyNs { get; }
    public string LatencyText => $"{LatencyNs:0.00} ns";
    public double BarFraction { get; }   // 0~1，相對於本次最大延遲
}

/// <summary>
/// 快取 / 記憶體延遲測試：以「指標追逐（pointer-chase）」在遞增的工作集大小上量測隨機存取延遲。
/// 工作集落在 L1/L2/L3 快取內時延遲低，超出後跳升，藉此推估各級快取與主記憶體的存取延遲。
/// 存取以快取行（64 位元組）為粒度並採亂序單一循環，用以擊敗硬體預取器。
/// </summary>
public sealed class CacheBenchService : ObservableObject
{
    private static int _sink;   // 防止 JIT 消除追逐迴圈

    // 量測的工作集大小（位元組）：涵蓋 L1 → L2 → L3 → 主記憶體
    private static readonly (int Bytes, string Text)[] Sizes =
    {
        (4 * 1024,         "4 KB"),
        (16 * 1024,        "16 KB"),
        (32 * 1024,        "32 KB"),
        (128 * 1024,       "128 KB"),
        (256 * 1024,       "256 KB"),
        (512 * 1024,       "512 KB"),
        (1 * 1024 * 1024,  "1 MB"),
        (4 * 1024 * 1024,  "4 MB"),
        (8 * 1024 * 1024,  "8 MB"),
        (16 * 1024 * 1024, "16 MB"),
        (32 * 1024 * 1024, "32 MB"),
        (64 * 1024 * 1024, "64 MB"),
    };

    private CancellationTokenSource? _cts;

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _phase = "尚未測試";
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }

    private double _progress;
    public double ProgressFraction { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => _progress * 100;

    private string _status = "按「開始測試」量測各級快取與記憶體的存取延遲（約需數秒）。";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

    public ObservableCollection<CacheLatencyRow> Rows { get; } = new();

    private string _l1 = "—", _l2 = "—", _l3 = "—", _ram = "—";
    public string L1Text { get => _l1; private set => SetProperty(ref _l1, value); }
    public string L2Text { get => _l2; private set => SetProperty(ref _l2, value); }
    public string L3Text { get => _l3; private set => SetProperty(ref _l3, value); }
    public string RamText { get => _ram; private set => SetProperty(ref _ram, value); }

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
        // 量延遲期間停掉全站動畫：動畫本身會踩快取、也會搶記憶體頻寬，直接污染這裡的讀值。
        using var quiet = Motion.Suspend();
        Phase = "測試中";
        ProgressFraction = 0;
        Rows.Clear();
        L1Text = L2Text = L3Text = RamText = "測試中…";
        StatusLine = "正在量測各工作集大小的存取延遲…";

        // 於 UI 執行緒建立 Progress，讓背景量測回報進度 / 狀態時自動切回 UI 執行緒
        var prog = new Progress<(double Frac, string Status)>(t => { ProgressFraction = t.Frac; StatusLine = t.Status; });
        var report = (IProgress<(double, string)>)prog;

        try
        {
            var raw = await Task.Run(() => Measure(ct, report), ct);

            double max = 0;
            foreach (var (_, ns) in raw) if (ns > max) max = ns;
            if (max <= 0) max = 1;

            foreach (var (i, ns) in raw)
                Rows.Add(new CacheLatencyRow(Sizes[i].Text, ns, Math.Clamp(ns / max, 0.02, 1)));

            Infer(raw);
            Phase = "完成";
            ProgressFraction = 1;
            StatusLine = $"完成 ・ L1≈{L1Text} ・ L2≈{L2Text} ・ L3≈{L3Text} ・ 記憶體≈{RamText}";
        }
        catch (OperationCanceledException)
        {
            Phase = "已停止";
            StatusLine = "測試已停止。";
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

    private static List<(int Index, double Ns)> Measure(CancellationToken ct, IProgress<(double, string)> report)
    {
        var result = new List<(int, double)>();
        for (int s = 0; s < Sizes.Length; s++)
        {
            ct.ThrowIfCancellationRequested();
            double ns = MeasureLatencyNs(Sizes[s].Bytes, ct);
            result.Add((s, ns));
            report.Report(((s + 1) / (double)Sizes.Length, $"量測 {Sizes[s].Text} … {ns:0.0} ns"));
        }
        return result;
    }

    /// <summary>對指定工作集大小以指標追逐量測平均存取延遲（ns）。時間預算自適應，兼顧精度與耗時。</summary>
    private static double MeasureLatencyNs(int sizeBytes, CancellationToken ct)
    {
        const int stride = 16;                 // 64 位元組 / 4 位元組（int），以快取行為粒度
        int count = Math.Max(sizeBytes / 4, stride * 2);
        int slots = count / stride;
        if (slots < 2) slots = 2;

        var arr = new int[count];

        // 建立涵蓋所有快取行的單一亂序循環（Fisher–Yates 洗牌後串成環）
        var order = new int[slots];
        for (int i = 0; i < slots; i++) order[i] = i;
        var rng = new Random(unchecked((int)0x9E3779B1 ^ sizeBytes));
        for (int i = slots - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        for (int i = 0; i < slots; i++)
            arr[order[i] * stride] = order[(i + 1) % slots] * stride;

        // 校準：先暖機（載入 TLB / 快取），再以少量存取估算單次延遲，據以決定正式存取數（目標約 0.35 秒）
        int start = 0;
        Chase(arr, 1_000_000, ref start);
        var cal = Stopwatch.StartNew();
        Chase(arr, 2_000_000, ref start);
        cal.Stop();
        double nsEst = cal.Elapsed.TotalMilliseconds * 1e6 / 2_000_000;
        if (nsEst <= 0) nsEst = 1;

        long accesses = (long)(0.35 * 1e9 / nsEst);
        accesses = Math.Clamp(accesses, 3_000_000, 300_000_000);

        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        int last = Chase(arr, accesses, ref start);
        sw.Stop();
        Volatile.Write(ref _sink, last);

        return sw.Elapsed.TotalMilliseconds * 1e6 / accesses;
    }

    private static int Chase(int[] arr, long accesses, ref int start)
    {
        int p = start;
        for (long i = 0; i < accesses; i++) p = arr[p];
        start = p;
        return p;
    }

    /// <summary>由延遲曲線推估各級快取 / 記憶體延遲：取代表性工作集的量測值。</summary>
    private void Infer(List<(int Index, double Ns)> raw)
    {
        double At(int bytes)
        {
            // 取不超過指定大小的最大工作集之延遲（該大小應完全落在對應層級內）
            double best = raw[0].Ns; int bestBytes = Sizes[0].Bytes;
            foreach (var (i, ns) in raw)
                if (Sizes[i].Bytes <= bytes && Sizes[i].Bytes >= bestBytes) { best = ns; bestBytes = Sizes[i].Bytes; }
            return best;
        }
        L1Text = $"{At(16 * 1024):0.00} ns";
        L2Text = $"{At(256 * 1024):0.00} ns";
        L3Text = $"{At(8 * 1024 * 1024):0.00} ns";
        RamText = $"{raw[^1].Ns:0.00} ns";
    }
}
