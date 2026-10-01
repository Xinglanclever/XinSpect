using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>一條算力維度的量測結果（含來源與正規化過程，供頁面展示計算 pipeline）。</summary>
public sealed class ComputeMetric
{
    public ComputeMetric(string name, double score, string category, string unit, double rawValue, string rawText,
                         string source, double refMax, bool isEstimated, string note)
    {
        Name = name; Score = score; Category = category; Unit = unit; RawValue = rawValue; RawText = rawText;
        Source = source; RefMax = refMax; IsEstimated = isEstimated; Note = note;
    }
    public string Name { get; }
    /// <summary>0–100 正規化後的分數。</summary>
    public double Score { get; }
    /// <summary>CPU / 記憶體 / 顯示卡 / 儲存 / NPU。</summary>
    public string Category { get; }
    public string Unit { get; }
    public double RawValue { get; }
    /// <summary>額外顯示的全文（包含單位）。</summary>
    public string RawText { get; }
    /// <summary>0–1 比例，用於長條寬度。</summary>
    public double BarFraction => Score / 100.0;

    /// <summary>數字從哪個頁面／測試來的。</summary>
    public string Source { get; }
    /// <summary>滿分參考值（正規化分母）。</summary>
    public double RefMax { get; }
    /// <summary>true＝查表／估算值而非本機實測；畫面上必須誠實標出。</summary>
    public bool IsEstimated { get; }
    /// <summary>量測性質補充說明（估算邏輯、量測限制）。</summary>
    public string Note { get; }
    public bool HasNote => Note.Length > 0;

    /// <summary>正規化過程的展示文字：原始值 ÷ 滿分參考值 × 100 ≈ 分數。</summary>
    public string FormulaText
    {
        get
        {
            string raw = RawValue >= 1000 ? RawValue.ToString("#,0") : RawValue.ToString("0.#");
            string rf = RefMax >= 1000 ? RefMax.ToString("#,0") : RefMax.ToString("0.#");
            return LanguageService.T($"{raw} {Unit} ÷ {rf} {Unit} × 100 ≈ {Score:0} 分");
        }
    }
    /// <summary>來源＋性質的一行展示文字。</summary>
    public string SourceText => LanguageService.T((IsEstimated ? "［估算值・非本機實測］" : "") + "來源：" + Source);
}

/// <summary>
/// 算力圖服務：從 MainViewModel 已有的各項跑分結果讀出各維度原始值，
/// 統一正規化為 0–100 分，給可視化長條用。
/// </summary>
public sealed class ComputeChartService : ObservableObject
{
    // ── 正規化參考上限 ──────────────────────────────────────────
    private const double RefCpuSingle   = 2000;   // MOPS
    private const double RefCpuMulti    = 40000;  // MOPS
    private const double RefMemBw       = 100;    // GB/s
    private const double RefGpuCompute  = 100;    // TFLOPS
    private const double RefStorageRead = 7000;   // MB/s
    private const double RefStorageWrite= 5000;   // MB/s
    private const double RefNpu         = 50;     // TOPS

    public ObservableCollection<ComputeMetric> Metrics { get; } = [];

    private bool _running;
    public bool IsRunning { get => _running; private set => SetProperty(ref _running, value); }

    private string _status = "";
    public string StatusLine { get => LanguageService.T(_status); private set => SetProperty(ref _status, value); }

    /// <summary>從 MainViewModel 各服務讀取已有數字，組裝算力維度。</summary>
    public void Refresh(MainViewModel? vm)
    {
        if (vm is null) { StatusLine = "無法取得主視窗資料。"; return; }
        IsRunning = true;
        StatusLine = "正在讀取…";
        Metrics.Clear();

        int added = 0;

        // ── CPU 單執行緒 ──
        if (vm.Bench.SingleScore is { } cpuS and > 0)
        {
            Metrics.Add(Make("處理器單核", cpuS, RefCpuSingle, "CPU", "MOPS", "「效能 › 綜合跑分」"));
            added++;
        }

        // ── CPU 多執行緒 ──
        if (vm.Bench.MultiScore is { } cpuM and > 0)
        {
            Metrics.Add(Make("處理器多核", cpuM, RefCpuMulti, "CPU", "MOPS", "「效能 › 綜合跑分」"));
            added++;
        }

        // ── 記憶體頻寬（綜合測試的 MemBandwidth） ──
        if (vm.Bench.MemBandwidth is { } memBw and > 0)
        {
            Metrics.Add(Make("記憶體頻寬", memBw, RefMemBw, "記憶體", "GB/s", "「效能 › 綜合跑分」"));
            added++;
        }

        // ── 記憶體實測峰值（MemBandwidthService，取 Rows 中最大 Gbps） ──
        if (vm.MemBandwidth.Rows.Count > 0)
        {
            double peakGbps = 0;
            foreach (var row in vm.MemBandwidth.Rows)
                if (row.Gbps > peakGbps) peakGbps = row.Gbps;
            if (peakGbps > 0)
            {
                Metrics.Add(Make("記憶體實測峰值", peakGbps, RefMemBw, "記憶體", "GB/s", "「效能 › 記憶體頻寬」"));
                added++;
            }
        }

        // ── 顯示卡算力：由型號查表估算，必須誠實標示不是實測 ──
        double gpuTflops = EstimateGpuTflops(vm.GpuOc.GpuName, out string gpuNote);
        if (gpuTflops > 0)
        {
            Metrics.Add(Make("顯示卡算力", gpuTflops, RefGpuCompute, "顯示卡", "TFLOPS",
                             "型號規格查表（" + vm.GpuOc.GpuName + "）", isEstimated: true, note: gpuNote));
            added++;
        }

        // ── 儲存循序讀取 ──
        if (TryParseMBps(vm.DiskBench.SeqReadText, out double seqR))
        {
            Metrics.Add(Make("儲存循序讀取", seqR, RefStorageRead, "儲存", "MB/s", "「效能 › 磁碟測試」"));
            added++;
        }

        // ── 儲存循序寫入 ──
        if (TryParseMBps(vm.DiskBench.SeqWriteText, out double seqW))
        {
            Metrics.Add(Make("儲存循序寫入", seqW, RefStorageWrite, "儲存", "MB/s", "「效能 › 磁碟測試」"));
            added++;
        }

        // ── NPU ──
        if (vm.NpuDetection.NpuPresent && TryParseTops(vm.NpuDetection.EstimatedTops, out double tops))
        {
            Metrics.Add(Make("NPU", tops, RefNpu, "NPU", "TOPS", "「系統 › NPU 偵測」", isEstimated: true,
                             note: "TOPS 為依裝置型號的估算值，非本機推論實測。"));
            added++;
        }

        StatusLine = added > 0
            ? $"讀到 {added} 個維度。未顯示的維度請先去對應頁面執行測試。"
            : "尚無跑分數據——請先到「綜合跑分」、「磁碟測試」等頁面執行至少一項測試。";

        BuildAnalysis(added);
        IsRunning = false;
    }

    /// <summary>總結分析：最強／最弱維度、均衡判讀、缺哪些資料與去哪補測。</summary>
    private void BuildAnalysis(int added)
    {
        var sb = new System.Text.StringBuilder();
        var present = Metrics.ToList();
        if (added == 0 || present.Count == 0)
        {
            AnalysisText = "還沒有任何測試結果可分析。先去下面列出的頁面跑任一項測試，再回來按「重新整理」。";
            MissingText = MissingGuide(present);
            return;
        }

        if (present.Count == 1)
        {
            var only = present[0];
            sb.Append($"目前只有「{only.Name}」一個維度有數據（{only.Score:0} 分，{only.RawText}）——"
                    + "多跑幾項測試後，強弱對比與均衡判讀才會有意義。");
            AnalysisText = sb.ToString();
            MissingText = MissingGuide(present);
            return;
        }

        var best = present.OrderByDescending(m => m.Score).First();
        var worst = present.OrderBy(m => m.Score).First();
        sb.AppendLine($"最強：{best.Name}（{best.Score:0} 分，{best.RawText}）。");
        sb.AppendLine($"最弱：{worst.Name}（{worst.Score:0} 分，{worst.RawText}）。");
        double spread = best.Score - worst.Score;
        if (present.Count >= 3 && spread <= 15)
            sb.Append("各維度分數接近，配置相當均衡，沒有明顯短板。");
        else if (best.Score >= worst.Score * 2.5)
            sb.Append($"「{best.Name}」明顯強於「{worst.Name}」——若使用情境剛好壓在弱項上，升級時優先補那一塊才划算。");
        else
            sb.Append($"強弱差距約 {spread:0} 分，屬常見範圍；瓶頸判讀可再參考「健康 › 瓶頸診斷」的即時負載歸因。");
        sb.AppendLine();
        int estimated = present.Count(m => m.IsEstimated);
        if (estimated > 0)
            sb.Append($"其中 {estimated} 項為查表／推算值（長條上已標「估算」），要更準請以對應頁面的實測為準。");
        AnalysisText = sb.ToString();
        MissingText = MissingGuide(present);
    }

    private static string MissingGuide(List<ComputeMetric> present)
    {
        var missing = new List<string>();
        bool Has(string cat) => present.Any(m => m.Category == cat);
        if (!Has("CPU")) missing.Add("處理器單核／多核 → 「效能 › 綜合跑分」");
        if (!Has("記憶體")) missing.Add("記憶體頻寬 → 「效能 › 綜合跑分」或「記憶體頻寬」");
        if (!Has("顯示卡")) missing.Add("顯示卡算力為型號查表，只要有偵測到顯示卡就會顯示；未顯示代表沒抓到顯示卡名稱");
        if (!Has("儲存")) missing.Add("儲存循序讀寫 → 「效能 › 磁碟測試」");
        if (!Has("NPU")) missing.Add("NPU → 「系統 › NPU 偵測」（沒有 NPU 的機器不會出現此維度）");
        return missing.Count == 0 ? "各維度資料都齊了。" : string.Join("\n", missing);
    }

    private string _analysis = "";
    /// <summary>讀完數字後的總結分析（最強／最弱／均衡判讀）。</summary>
    public string AnalysisText { get => LanguageService.T(_analysis); private set => SetProperty(ref _analysis, value); }

    private string _missing = "";
    /// <summary>缺哪些維度資料、去哪個頁面補測。</summary>
    public string MissingText { get => LanguageService.T(_missing); private set => SetProperty(ref _missing, value); }

    // ── 內部輔助 ─────────────────────────────────────────────

    private static ComputeMetric Make(string name, double raw, double refMax, string category, string unit,
                                      string source, bool isEstimated = false, string note = "")
    {
        double score = Math.Clamp(raw / refMax * 100.0, 0, 100);
        string rawText = raw >= 1000 ? $"{raw:#,0} {unit}" : $"{raw:0.#} {unit}";
        // 維度名／來源／補充說明會直接上版面（繫結顯示，不經視覺樹掃描），在產出點過 T。
        return new ComputeMetric(LanguageService.T(name), score, category, unit, raw, rawText,
                                 LanguageService.T(source), refMax, isEstimated, LanguageService.T(note));
    }

    /// <summary>
    /// 從 "1234 MB/s" 或 "1234.5" 擷取數值。
    /// </summary>
    /// <remarks>
    /// 已知脆弱：這裡解析的是 <c>DiskBenchService</c> 的「顯示文字」（SeqReadText／SeqWriteText），
    /// 不是底層數值——該服務沒有公開數值屬性（內部量測值只是 RunAsync 的區域變數）。
    /// 風險：若顯示格式改變（加千位分隔、改單位、改文化）解析就跟著失效或算錯。
    /// 特別是 <c>Replace(",", "")</c>：在以「.」作千位分隔、「,」作小數點的文化（de-DE）下，
    /// "1.234,5 MB/s" 逗號被刪掉後會把 "1.2345" 靜默解析成 1.2345——差了三個數量級。
    /// 目前 `{value:0}` 格式不產生分隔符所以勉強安全，但這依賴呼叫端的格式寫法，不是保證。
    /// 根治之道是讓 DiskBenchService 公開原始數值屬性（本次不在可修改檔案清單內，故保留現狀）。
    /// </remarks>
    private static bool TryParseMBps(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || text == "—" || text == "--" || text.Contains('…'))
            return false;
        // 取第一段數字；解析固定 invariant（見上方 remarks 的文化風險說明）
        var m = Regex.Match(text, @"([\d,.]+)");
        return m.Success && double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    /// <summary>從 "~48 TOPS（…）" 擷取數值。</summary>
    private static bool TryParseTops(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || text == "—" || text.StartsWith("—"))
            return false;
        var m = Regex.Match(text, @"~?([\d,.]+)");
        return m.Success && double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    /// <summary>
    /// 根據 GPU 名稱粗估 FP32 TFLOPS。僅供相對比較，不是精確值。
    /// 優先查 CUDA 是否可用（代表 NVIDIA），再按名稱匹配。
    /// </summary>
    private static double EstimateGpuTflops(string? gpuName, out string note)
    {
        note = "依顯示卡型號對照 FP32 規格表的估算值，非本機實測；同型號因功耗設定與散熱不同會有差距。";
        if (string.IsNullOrWhiteSpace(gpuName) || gpuName == "—") return 0;

        bool hasCuda = CudaService.DetectVersion() is not null;
        string upper = gpuName.ToUpperInvariant();

        // NVIDIA 卡粗估表（FP32 TFLOPS）
        if (hasCuda || upper.Contains("NVIDIA") || upper.Contains("GEFORCE") || upper.Contains("TITAN"))
        {
            if (upper.Contains("5090"))  return 105;
            if (upper.Contains("5080"))  return 69;
            if (upper.Contains("5070 TI")) return 48;
            if (upper.Contains("5070"))  return 36;
            if (upper.Contains("4090"))  return 83;
            if (upper.Contains("4080 SUPER")) return 52;
            if (upper.Contains("4080"))  return 49;
            if (upper.Contains("4070 TI SUPER")) return 44;
            if (upper.Contains("4070 TI")) return 40;
            if (upper.Contains("4070 SUPER")) return 36;
            if (upper.Contains("4070"))  return 29;
            if (upper.Contains("4060 TI")) return 22;
            if (upper.Contains("4060"))  return 15;
            if (upper.Contains("3090 TI")) return 40;
            if (upper.Contains("3090"))  return 36;
            if (upper.Contains("3080 TI")) return 34;
            if (upper.Contains("3080"))  return 30;
            if (upper.Contains("3070 TI")) return 22;
            if (upper.Contains("3070"))  return 20;
            if (upper.Contains("3060 TI")) return 16;
            if (upper.Contains("3060"))  return 13;
            if (upper.Contains("TITAN") && upper.Contains("XP")) return 12;
            if (upper.Contains("1080 TI")) return 11.3;
            if (upper.Contains("1080"))  return 9;
            if (upper.Contains("1070 TI")) return 8.1;
            if (upper.Contains("1070"))  return 6.5;
            if (upper.Contains("1060"))  return 4.4;
            if (upper.Contains("1050 TI")) return 2.1;
            if (upper.Contains("1050"))  return 1.9;
            if (hasCuda) return 5; // 有 CUDA 但不在表內，給保守值
        }

        // AMD 粗估
        if (upper.Contains("AMD") || upper.Contains("RADEON"))
        {
            if (upper.Contains("7900 XTX")) return 61;
            if (upper.Contains("7900 XT"))  return 52;
            if (upper.Contains("7800 XT"))  return 37;
            if (upper.Contains("7700 XT"))  return 35;
            if (upper.Contains("7600"))     return 22;
            if (upper.Contains("6900 XT"))  return 23;
            if (upper.Contains("6800 XT"))  return 21;
            if (upper.Contains("6700 XT"))  return 13;
            return 8; // 有 AMD 但不在表內
        }

        // Intel Arc
        if (upper.Contains("ARC") || upper.Contains("INTEL"))
        {
            if (upper.Contains("A770")) return 20;
            if (upper.Contains("A750")) return 17;
            if (upper.Contains("A580")) return 13;
            return 5;
        }

        return 0; // 不認識的顯示卡
    }
}
