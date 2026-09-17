using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>一條算力維度的量測結果。</summary>
public sealed class ComputeMetric
{
    public ComputeMetric(string name, double score, string category, string unit, double rawValue, string rawText)
    {
        Name = name; Score = score; Category = category; Unit = unit; RawValue = rawValue; RawText = rawText;
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
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

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
            Metrics.Add(Make("處理器單核", cpuS, RefCpuSingle, "CPU", "MOPS"));
            added++;
        }

        // ── CPU 多執行緒 ──
        if (vm.Bench.MultiScore is { } cpuM and > 0)
        {
            Metrics.Add(Make("處理器多核", cpuM, RefCpuMulti, "CPU", "MOPS"));
            added++;
        }

        // ── 記憶體頻寬（綜合測試的 MemBandwidth） ──
        if (vm.Bench.MemBandwidth is { } memBw and > 0)
        {
            Metrics.Add(Make("記憶體頻寬", memBw, RefMemBw, "記憶體", "GB/s"));
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
                Metrics.Add(Make("記憶體實測峰值", peakGbps, RefMemBw, "記憶體", "GB/s"));
                added++;
            }
        }

        // ── 顯示卡算力 ──
        double gpuTflops = EstimateGpuTflops(vm.GpuOc.GpuName);
        if (gpuTflops > 0)
        {
            Metrics.Add(Make("顯示卡算力", gpuTflops, RefGpuCompute, "顯示卡", "TFLOPS"));
            added++;
        }

        // ── 儲存循序讀取 ──
        if (TryParseMBps(vm.DiskBench.SeqReadText, out double seqR))
        {
            Metrics.Add(Make("儲存循序讀取", seqR, RefStorageRead, "儲存", "MB/s"));
            added++;
        }

        // ── 儲存循序寫入 ──
        if (TryParseMBps(vm.DiskBench.SeqWriteText, out double seqW))
        {
            Metrics.Add(Make("儲存循序寫入", seqW, RefStorageWrite, "儲存", "MB/s"));
            added++;
        }

        // ── NPU ──
        if (vm.NpuDetection.NpuPresent && TryParseTops(vm.NpuDetection.EstimatedTops, out double tops))
        {
            Metrics.Add(Make("NPU", tops, RefNpu, "NPU", "TOPS"));
            added++;
        }

        StatusLine = added > 0
            ? $"讀到 {added} 個維度。未顯示的維度請先去對應頁面執行測試。"
            : "尚無跑分數據——請先到「綜合跑分」、「磁碟測試」等頁面執行至少一項測試。";

        IsRunning = false;
    }

    // ── 內部輔助 ─────────────────────────────────────────────

    private static ComputeMetric Make(string name, double raw, double refMax, string category, string unit)
    {
        double score = Math.Clamp(raw / refMax * 100.0, 0, 100);
        string rawText = raw >= 1000 ? $"{raw:#,0} {unit}" : $"{raw:0.#} {unit}";
        return new ComputeMetric(name, score, category, unit, raw, rawText);
    }

    /// <summary>從 "1234 MB/s" 或 "1234.5" 擷取數值。</summary>
    private static bool TryParseMBps(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || text == "—" || text == "--" || text.Contains('…'))
            return false;
        // 取第一段數字
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
    private static double EstimateGpuTflops(string? gpuName)
    {
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
