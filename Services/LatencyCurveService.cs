using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>一條「實測階梯 ↔ CPUID 宣稱」的對照列。</summary>
public sealed class LatencyBoundaryRow
{
    public LatencyBoundaryRow(string derived, string claimed) { Derived = derived; Claimed = claimed; }
    public string Derived { get; }
    public string Claimed { get; }
}

/// <summary>邊界可信度：推導值與 CPUID 宣稱值的偏差評估。</summary>
public sealed class LatencyDeviationRow
{
    public LatencyDeviationRow(string levelName, string derivedSize, string claimedSize, string deviation, string confidence)
    { LevelName = levelName; DerivedSize = derivedSize; ClaimedSize = claimedSize; Deviation = deviation; Confidence = confidence; }
    public string LevelName { get; }
    public string DerivedSize { get; }
    public string ClaimedSize { get; }
    public string Deviation { get; }
    public string Confidence { get; }
    public string ConfidenceClass => Confidence.Contains('高') ? "Good" : Confidence.Contains('低') ? "Critical" : "Warning";
}

/// <summary>邊界偏移的可信度等級。</summary>
public enum DeviationConfidence { High, Medium, Low }

/// <summary>一個推導邊界與對應宣稱值的偏差資訊。</summary>
public sealed record DeviationInfo(double BoundaryBytes, double ClaimedBytes, double DeviationPct, DeviationConfidence Confidence);

/// <summary>
/// 記憶體延遲曲線：以指標追逐法在 1 KB → 1 GB（半倍頻步進）的工作集上量測平均存取延遲，
/// 由曲線的階梯<b>推導</b>出 L1／L2／L3／DRAM 的邊界，再與 CPUID leaf 0x04 <b>宣稱</b>的快取容量並列對照。
/// 「實測階梯落在哪裡」與「晶片宣稱多少」是兩件事——並列才誠實。
/// </summary>
/// <remarks>
/// 誠實界線：量測的是隨機化指標追逐的平均存取延遲（含 TLB／分頁效應），不是純 SRAM/DRAM 物理延遲；
/// 邊界是由曲線階梯推導的，不是硬體回報的，所以呈現時一律與 CPUID 宣稱值並排、附偏差百分比。
/// 工作集上限受「可用記憶體 − 1 GB 保留」約束（吃乾會測到分頁檔）。
/// </remarks>
public sealed class LatencyCurveService : ObservableObject
{
    private static int _sink;   // 防止 JIT 消除追逐迴圈
    private CancellationTokenSource? _cts;

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _phase = "尚未測試";
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }

    private double _progress;
    public double ProgressFraction { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => _progress * 100;

    private string _status = "按「開始量測」掃描 1 KB → 1 GB 的工作集延遲曲線（每點自適應取樣，全程約 10 秒）。";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

    private long[] _sizes = [];
    /// <summary>各點的工作集大小（位元組）。</summary>
    public long[] Sizes { get => _sizes; private set => SetProperty(ref _sizes, value); }

    private double[] _latencies = [];
    /// <summary>各點的平均存取延遲（ns）。</summary>
    public double[] Latencies { get => _latencies; private set => SetProperty(ref _latencies, value); }

    private double[] _boundaries = [];
    /// <summary>由曲線階梯推導的邊界（位元組，幾何平均點）。</summary>
    public double[] Boundaries { get => _boundaries; private set => SetProperty(ref _boundaries, value); }

    private string[] _boundaryLabels = [];
    /// <summary>各邊界旁要顯示的偏差標籤（與 Boundaries 一一對應），如 "+8%"、"-22%"。</summary>
    public string[] BoundaryLabels { get => _boundaryLabels; private set => SetProperty(ref _boundaryLabels, value); }

    private string[] _boundaryConfidence = [];
    /// <summary>各邊界的可信度（"high"/"medium"/"low"），決定圖表上邊界線顏色。</summary>
    public string[] BoundaryConfidence { get => _boundaryConfidence; private set => SetProperty(ref _boundaryConfidence, value); }

    public ObservableCollection<LatencyBoundaryRow> BoundaryRows { get; } = [];

    public ObservableCollection<LatencyDeviationRow> DeviationRows { get; } = [];

    private string _claimedNote = "CPUID 宣稱：尚未量測。";
    public string ClaimedNote { get => _claimedNote; private set => SetProperty(ref _claimedNote, value); }

    private string _minLatText = "—";
    public string MinLatText { get => _minLatText; private set => SetProperty(ref _minLatText, value); }

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
        Phase = "量測中";
        ProgressFraction = 0;
        BoundaryRows.Clear();
        MinLatText = "—";

        var prog = new Progress<(double Frac, string Status)>(t => { ProgressFraction = t.Frac; StatusLine = t.Status; });
        var report = (IProgress<(double, string)>)prog;

        try
        {
            var claimed = ReadClaimedCaches();
            ClaimedNote = claimed.Count > 0
                ? "CPUID 宣稱：" + string.Join("・", claimed.Select(c => $"{c.Name} {FormatBytes(c.Bytes)}"))
                : "CPUID 不可用，僅呈現由曲線推導的邊界。";

            long maxBytes = MaxFootprintBytes();
            var (sizes, lats) = await Task.Run(() =>
            {
                var ss = LatencyCurveMath.BuildSizes(maxBytes);
                var ll = new double[ss.Length];
                for (int i = 0; i < ss.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    ll[i] = MeasureLatencyNs(ss[i], ct);
                    report.Report(((i + 1) / (double)ss.Length, $"量測 {FormatBytes(ss[i])} … {ll[i]:0.0} ns"));
                }
                return (ss, ll);
            }, ct);

            Sizes = sizes;
            Latencies = lats;
            var bl = LatencyCurveMath.DeriveBoundaries(sizes, lats);
            Boundaries = [.. bl];
            var pairs = LatencyCurveMath.PairNearest(bl.ToArray(), claimed.Select(c => (double)c.Bytes).ToArray());

            BoundaryRows.Clear();
            int minIdx = 0;
            for (int i = 1; i < lats.Length; i++) if (lats[i] < lats[minIdx]) minIdx = i;
            MinLatText = $"{lats[minIdx]:0.0} ns（@ {FormatBytes(sizes[minIdx])}）";

            for (int i = 0; i < bl.Count; i++)
            {
                string derived = $"≈ {FormatBytes((long)bl[i])}";
                string claimedText = pairs[i] >= 0
                    ? $"對照 {claimed[pairs[i]].Name} 宣稱 {FormatBytes(claimed[pairs[i]].Bytes)}（{(bl[i] / claimed[pairs[i]].Bytes - 1.0):+0%;-0%}）"
                    : "附近無 CPUID 宣稱值可對照";
                BoundaryRows.Add(new LatencyBoundaryRow(derived, claimedText));
            }

            // 偏差可信度評估（也用來填 BoundaryLabels / BoundaryConfidence 給圖表）
            DeviationRows.Clear();
            var deviations = LatencyCurveMath.AssessDeviation(bl.ToArray(), pairs, claimed.Select(c => (double)c.Bytes).ToArray());
            string[] levelNames = ["L1", "L2", "L3", "L4", "L5"];
            var labels = new string[bl.Count];
            var confs = new string[bl.Count];
            for (int i = 0; i < bl.Count; i++)
            {
                if (pairs[i] < 0) continue;   // 未配對的邊界不顯示偏差
                var dev = deviations.FirstOrDefault(d => Math.Abs(d.BoundaryBytes - bl[i]) < 1.0);
                if (dev is null) continue;
                labels[i] = dev.DeviationPct == 0 ? "—" : $"{dev.DeviationPct:+0.0;-0.0}%";
                confs[i] = dev.Confidence switch
                {
                    DeviationConfidence.High => "high",
                    DeviationConfidence.Medium => "medium",
                    _ => "low",
                };
            }
            BoundaryLabels = labels;
            BoundaryConfidence = confs;

            for (int i = 0; i < deviations.Count; i++)
            {
                var d = deviations[i];
                string name = i < levelNames.Length ? levelNames[i] : $"邊界{i + 1}";
                string conf = d.Confidence switch
                {
                    DeviationConfidence.High => "高可信",
                    DeviationConfidence.Medium => "中可信",
                    _ => "低可信",
                };
                DeviationRows.Add(new LatencyDeviationRow(name, FormatBytes((long)d.BoundaryBytes), FormatBytes((long)d.ClaimedBytes), $"{d.DeviationPct:+0.0;-0.0}%", conf));
            }

            Phase = "完成";
            ProgressFraction = 1;
            StatusLine = $"完成 ・ {sizes.Length} 個量測點、推導 {bl.Count} 個邊界。邊界由曲線階梯推導（量到的），宣稱值來自 CPUID（另一回事），兩者並列。";
        }
        catch (OperationCanceledException)
        {
            Phase = "已停止";
            StatusLine = "量測已停止。";
        }
        catch (Exception ex)
        {
            Phase = "錯誤";
            StatusLine = "量測失敗：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>工作集上限：1 GB 上限，且不得吃乾可用記憶體（保留 1 GB 給系統與本程式）。</summary>
    private static long MaxFootprintBytes()
    {
        const long Gib = 1024 * 1024 * 1024;
        long avail = (long)(new MemoryService().ReadStats().AvailGB * Gib);
        long cap = Math.Min(1 * Gib, Math.Max(64 * 1024 * 1024, avail - Gib));
        return cap;
    }

    /// <summary>CPUID leaf 0x04 的宣稱快取（層級名＋容量）。</summary>
    private static List<(string Name, long Bytes)> ReadClaimedCaches()
    {
        var list = new List<(string, long)>();
        if (!X86Base.IsSupported) return list;
        try
        {
            for (uint sub = 0; sub < 64; sub++)
            {
                var r = X86Base.CpuId(0x04, (int)sub);
                var raw = CpuIdService.Decoder.DecodeCacheRaw((uint)r.Eax, (uint)r.Ebx, (uint)r.Ecx, (uint)r.Edx);
                if (raw is null) break;
                list.Add((raw.Value.LevelName, raw.Value.CapacityBytes));
            }
        }
        catch { /* CPUID 不可用時退回空清單，曲線照跑 */ }
        return list;
    }

    /// <summary>對指定工作集以指標追逐量測平均存取延遲（ns）。取樣數自適應（目標每點約 0.12 秒）。</summary>
    private static double MeasureLatencyNs(long sizeBytes, CancellationToken ct)
    {
        const int stride = 16;                 // 64 位元組快取行 / 4 位元組 int
        int count = (int)Math.Max(sizeBytes / 4, stride * 2);
        int slots = Math.Max(2, count / stride);
        var arr = new int[slots * stride];

        // 涵蓋所有快取行的單一亂序循環（Fisher–Yates 洗牌後串成環），擊敗硬體預取
        var order = new int[slots];
        for (int i = 0; i < slots; i++) order[i] = i;
        var rng = new Random(unchecked((int)(0x9E3779B1u ^ sizeBytes)));
        for (int i = slots - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        for (int i = 0; i < slots; i++)
            arr[order[i] * stride] = order[(i + 1) % slots] * stride;

        int start = 0;
        Chase(arr, 500_000, ref start);          // 暖機：載入 TLB／分頁表
        var cal = Stopwatch.StartNew();
        Chase(arr, 200_000, ref start);
        cal.Stop();
        ct.ThrowIfCancellationRequested();

        double perHopNs = Math.Max(cal.Elapsed.TotalMilliseconds * 1e6 / 200_000, 0.1);
        long hops = Math.Clamp((long)(0.12 * 1e9 / perHopNs), 200_000, 4_000_000);
        var sw = Stopwatch.StartNew();
        Chase(arr, hops, ref start);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds * 1e6 / hops;
    }

    private static void Chase(int[] arr, long hops, ref int index)
    {
        for (long i = 0; i < hops; i++) index = arr[index];
        _sink = index;
    }

    internal static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.##} MB",
            >= 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes} B",
        };
    }
}

/// <summary>延遲曲線的純函式數學（點位規劃、邊界推導、與 CPUID 宣稱配對）。</summary>
public static class LatencyCurveMath
{
    /// <summary>由 1 KB 起、半倍頻（×√2）步進到 maxBytes 的量測點。</summary>
    public static long[] BuildSizes(long maxBytes)
    {
        var list = new List<long>();
        double ratio = Math.Sqrt(2.0);
        for (double s = 1024; s <= maxBytes; s *= ratio)
        {
            long v = (long)Math.Round(s / 64) * 64;   // 對齊快取行
            if (list.Count == 0 || v > list[^1]) list.Add(Math.Max(v, 1024));
        }
        long cap = (long)Math.Round(maxBytes / 64.0) * 64;
        if (list.Count == 0 || list[^1] < cap) list.Add(cap);
        return list.ToArray();
    }

    /// <summary>
    /// 由曲線階梯推導邊界：某點延遲相對「目前平台最低值」跳升 ≥55% 即視為越過一層快取，
    /// 隨即把平台基準換到該點（同一平台內的後續點不再重複觸發）；
    /// 相鄰推導點在 ×2.2 內者視為同一事件，只留第一個。
    /// </summary>
    public static List<double> DeriveBoundaries(long[] sizes, double[] latencies)
    {
        var result = new List<double>();
        if (sizes.Length < 2 || latencies.Length < 2) return result;
        double minSoFar = latencies[0];
        for (int i = 1; i < latencies.Length; i++)
        {
            double ratio = latencies[i] / Math.Max(minSoFar, 1e-9);
            if (ratio >= 1.55)
            {
                double boundary = Math.Sqrt(sizes[i - 1] * (double)sizes[i]);
                if (result.Count == 0 || boundary > result[^1] * 2.2)
                    result.Add(boundary);
                minSoFar = latencies[i];     // 平台基準換軌：這一層的延遲從這裡算起
            }
            else if (latencies[i] < minSoFar)
            {
                minSoFar = latencies[i];
            }
        }
        return result;
    }

    /// <summary>
    /// 把推導邊界與 CPUID 宣稱容量配對：每個邊界找「比值最接近 1」且在 ×2.5 內的未配對宣稱值；
    /// 找不到回 -1。回傳陣列長度＝邊界數，元素為宣稱值索引。
    /// </summary>
    public static int[] PairNearest(double[] boundaries, double[] claimed)
    {
        var map = new int[boundaries.Length];
        var used = new bool[claimed.Length];
        // 依「比值距離」全域由小到大配對，避免近邊界搶走遠宣稱
        var candidates = new List<(int B, int C, double Dist)>();
        for (int b = 0; b < boundaries.Length; b++)
            for (int c = 0; c < claimed.Length; c++)
            {
                if (claimed[c] <= 0) continue;
                double ratio = boundaries[b] / claimed[c];
                if (ratio < 1 / 2.5 || ratio > 2.5) continue;
                candidates.Add((b, c, Math.Log(ratio) * Math.Log(ratio)));
            }
        foreach (var (b, c, _) in candidates.OrderBy(x => x.Dist))
        {
            if (map[b] != 0 || used[c]) continue;
            map[b] = c + 1;    // 以 1 起始存，0 代表未配對
            used[c] = true;
        }
        for (int b = 0; b < map.Length; b++) map[b] -= 1;
        return map;
    }

    /// <summary>
    /// 評估推導邊界與宣稱值的偏移可信度：
    /// 偏差 &lt; 20% → 高可信；20–50% → 中可信；&gt; 50% → 低可信。
    /// 未配對的邊界不列入評估。
    /// </summary>
    public static List<DeviationInfo> AssessDeviation(double[] boundaries, int[] pairMap, double[] claimed)
    {
        var result = new List<DeviationInfo>();
        if (boundaries.Length == 0 || claimed.Length == 0) return result;
        for (int b = 0; b < boundaries.Length; b++)
        {
            int c = pairMap[b];
            if (c < 0 || c >= claimed.Length || claimed[c] <= 0) continue;
            double devPct = (boundaries[b] / claimed[c] - 1.0) * 100;
            var conf = Math.Abs(devPct) < 20 ? DeviationConfidence.High
                     : Math.Abs(devPct) < 50 ? DeviationConfidence.Medium
                     : DeviationConfidence.Low;
            result.Add(new DeviationInfo(boundaries[b], claimed[c], devPct, conf));
        }
        return result;
    }
}
