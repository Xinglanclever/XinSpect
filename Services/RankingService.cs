using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Data;

namespace XinSpect;

/// <summary>天梯榜單列：一款處理器或顯示卡的排名與跑分。IsLocal 為本機所偵測到的硬體（高亮）。</summary>
public sealed class RankRow : ObservableObject
{
    public required int Rank { get; init; }
    public required string Name { get; init; }
    public required string Brand { get; init; }
    public required string Grade { get; init; }
    /// <summary>主要分數（CPU 多核跑分 / GPU 綜合評分）。</summary>
    public required string Score { get; init; }
    /// <summary>次要規格摘要（CPU：核心數・單核；GPU：TFLOPS・顯示記憶體）。</summary>
    public required string Detail { get; init; }
    /// <summary>是否為筆電型號（供桌機／筆電切換過濾）。</summary>
    public required bool IsLaptop { get; init; }

    private bool _isLocal;
    /// <summary>是否為本機硬體（供畫面高亮標示「本機」）。</summary>
    public bool IsLocal { get => _isLocal; set => SetProperty(ref _isLocal, value); }
}

/// <summary>
/// CPU / 顯示卡效能天梯榜：離線讀取內嵌的排行資料（資料來源 topcpu.net），提供搜尋、桌機／筆電切換，
/// 並自動標示本機所偵測到的處理器與顯示卡所在名次。所有資料為第三方公開跑分整理，僅供參考。
/// </summary>
public sealed class RankingService : ObservableObject
{
    private readonly List<RankRow> _cpu = new();
    private readonly List<RankRow> _gpu = new();

    public ICollectionView CpuList { get; }
    public ICollectionView GpuList { get; }

    public string CpuSource { get; private set; } = "資料來源：topcpu.net";
    public string GpuSource { get; private set; } = "資料來源：topcpu.net";

    public RankingService()
    {
        try { LoadCpu(); } catch { /* 天梯資料為附加功能，解析失敗則清單留空 */ }
        try { LoadGpu(); } catch { }
        CpuList = new ListCollectionView(_cpu) { Filter = CpuFilterFn };
        GpuList = new ListCollectionView(_gpu) { Filter = GpuFilterFn };
    }

    // ── 桌機 / 筆電切換（0 桌機、1 筆電）──────────────────────────────
    private int _cpuScope;
    public int CpuScope { get => _cpuScope; set { if (SetProperty(ref _cpuScope, value)) CpuList.Refresh(); } }

    private int _gpuScope;
    public int GpuScope { get => _gpuScope; set { if (SetProperty(ref _gpuScope, value)) GpuList.Refresh(); } }

    // ── 搜尋文字 ──────────────────────────────────────────────────────
    private string _cpuFilter = "";
    public string CpuFilter { get => _cpuFilter; set { if (SetProperty(ref _cpuFilter, value)) CpuList.Refresh(); } }

    private string _gpuFilter = "";
    public string GpuFilter { get => _gpuFilter; set { if (SetProperty(ref _gpuFilter, value)) GpuList.Refresh(); } }

    private bool CpuFilterFn(object o)
        => o is RankRow r && r.IsLaptop == (_cpuScope == 1)
           && (_cpuFilter.Length == 0 || r.Name.Contains(_cpuFilter, StringComparison.OrdinalIgnoreCase));

    private bool GpuFilterFn(object o)
        => o is RankRow r && r.IsLaptop == (_gpuScope == 1)
           && (_gpuFilter.Length == 0 || r.Name.Contains(_gpuFilter, StringComparison.OrdinalIgnoreCase));

    // __RANK_PLACEHOLDER__

    private static string? ReadResource(string logicalName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(logicalName);
        if (s is null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private void LoadCpu()
    {
        var json = ReadResource("XinSpect.data.cpu-ranking.json");
        if (json is null) return;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("source", out var src)) CpuSource = "資料來源：" + src.GetString();
        AddCpu(root, "desktop", false);
        AddCpu(root, "laptop", true);
    }

    private void AddCpu(JsonElement root, string prop, bool laptop)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var e in arr.EnumerateArray())
        {
            string name = Str(e, "name");
            if (name.Length == 0) continue;
            int multi = Num(e, "multiCore");
            int single = Num(e, "singleCore");
            string cores = Str(e, "cores");
            var detail = new List<string>();
            if (cores.Length > 0) detail.Add(cores);
            if (single > 0) detail.Add($"單核 {single}");
            _cpu.Add(new RankRow
            {
                Rank = Num(e, "rank"),
                Name = name,
                Brand = Str(e, "brand"),
                Grade = Str(e, "grade"),
                Score = multi > 0 ? multi.ToString("N0") : Num(e, "rating").ToString("N0"),
                Detail = string.Join(" ・ ", detail),
                IsLaptop = laptop,
            });
        }
    }

    private void LoadGpu()
    {
        var json = ReadResource("XinSpect.data.gpu-ranking.json");
        if (json is null) return;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("source", out var src)) GpuSource = "資料來源：" + src.GetString();
        AddGpu(root, "desktop", false);
        AddGpu(root, "laptop", true);
    }

    private void AddGpu(JsonElement root, string prop, bool laptop)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var e in arr.EnumerateArray())
        {
            string name = Str(e, "name");
            if (name.Length == 0) continue;
            string tflops = Str(e, "tflops");
            string mem = Str(e, "timeSpy");   // 此資料集的 timeSpy 欄位實際承載顯示記憶體規格
            var detail = new List<string>();
            if (tflops.Length > 0) detail.Add($"{tflops} TFLOPS");
            if (mem.Length > 0) detail.Add(mem);
            _gpu.Add(new RankRow
            {
                Rank = Num(e, "rank"),
                Name = name,
                Brand = Str(e, "brand"),
                Grade = Str(e, "grade"),
                Score = Num(e, "rating").ToString("N0"),
                Detail = string.Join(" ・ ", detail),
                IsLaptop = laptop,
            });
        }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static int Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    /// <summary>依本機偵測到的處理器 / 顯示卡名稱，於榜單中比對並高亮最相符的一列（近似比對，僅供參考）。</summary>
    public void Highlight(string? cpuName, string? gpuName)
    {
        MarkBest(_cpu, cpuName);
        MarkBest(_gpu, gpuName);
    }

    private static void MarkBest(List<RankRow> rows, string? hardwareName)
    {
        var key = Normalize(hardwareName);
        if (key.Length < 3) return;
        RankRow? best = null;
        int bestScore = 0;
        foreach (var r in rows)
        {
            int s = MatchScore(key, Normalize(r.Name));
            if (s > bestScore) { bestScore = s; best = r; }
        }
        // 需達足夠的重疊字元數才視為命中，避免誤標
        if (best is not null && bestScore >= 6) best.IsLocal = true;
    }

    // 移除品牌雜訊與符號，保留型號關鍵字（含數字），統一為小寫無空白
    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.ToLowerInvariant();
        foreach (var noise in new[] { "(r)", "(tm)", "®", "™", "intel", "amd", "nvidia", "geforce",
                                      "core", "processor", "cpu", "graphics", "gpu", "ryzen", "radeon", "with" })
            s = s.Replace(noise, " ");
        var chars = s.Where(c => char.IsLetterOrDigit(c)).ToArray();
        return new string(chars);
    }

    // 以最長共同數字/型號片段的長度近似評估相符程度
    private static int MatchScore(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Contains(b) || b.Contains(a)) return Math.Min(a.Length, b.Length);
        int best = 0;
        for (int i = 0; i < a.Length; i++)
            for (int j = i + 1; j <= a.Length; j++)
            {
                var sub = a.Substring(i, j - i);
                if (sub.Length > best && b.Contains(sub)) best = sub.Length;
            }
        return best;
    }
}
