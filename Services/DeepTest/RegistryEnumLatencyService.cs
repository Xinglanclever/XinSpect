using System.Diagnostics;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// U10-TS-0218「登錄檔硬體樹讀取延遲（ENUM 樹規模代理）」：<c>ts.reg.enum.latency</c>。
/// </summary>
/// <remarks>
/// <para>
/// 來源：<c>HKLM\SYSTEM\CurrentControlSet\Enum</c>（PnP 裝置安裝資料庫；usermode 唯讀、零特權）。
/// 量法：逐節點量 <c>OpenSubKey</c>＋<c>GetSubKeyNames</c> 的耗時，取 p50/p95，
/// 同時回報走訪的節點數（ENUM 樹規模的代理）——兩個數字放一起看才有意義。
/// </para>
/// <para>
/// 誠實界線：①計時含本程式自己的 API 呼叫成本，不扣除（與 FsMetadataLatencyService 同一立場：
/// 量級差夠遠，扣了反而變成估的）；②走訪有節點預算（預設 2000），超過即停並標註「未走完」，
/// 不把截斷的樣本冒充全樹；③這是延遲代理，不是登錄檔效能判決。
/// </para>
/// </remarks>
public static class RegistryEnumLatencyService
{
    private const string Category = "系統";
    public const string Key = "ts.reg.enum.latency";
    public const string Source = "HKLM\\SYSTEM\\CurrentControlSet\\Enum（Microsoft.Win32.Registry；唯讀）";

    /// <summary>一次走訪的原始樣本：走訪節點數、逐節點耗時（Stopwatch 刻）。</summary>
    public sealed record EnumLatencySample(int VisitedNodes, IReadOnlyList<long> PerNodeTicks, bool Truncated);

    /// <summary>收集 ENUM 樹走訪延遲。測試以注入取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<EnumLatencySample>? probe = null, int nodeBudget = 2000)
    {
        EnumLatencySample sample;
        try { sample = probe is { } p ? p() : RealProbe(nodeBudget); }
        catch (Exception ex)
        {
            return [new HardwareFact(Key, Category, "ENUM 樹讀取延遲", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "登錄檔走訪失敗：" + ex.Message)];
        }
        return [BuildFact(at, sample)];
    }

    /// <summary>純函式：走訪樣本 → 事實。</summary>
    public static HardwareFact BuildFact(DateTimeOffset at, EnumLatencySample sample)
    {
        if (sample.VisitedNodes == 0)
            return new HardwareFact(Key, Category, "ENUM 樹讀取延遲", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "ENUM 樹走訪 0 個節點（裝置安裝資料庫不存在或為空）——不以 0 µs 冒充量到");
        if (sample.PerNodeTicks.Count == 0)
            return new HardwareFact(Key, Category, "ENUM 樹讀取延遲", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "有節點數但沒有計時樣本——量測未執行");
        double ticksPerUs = Stopwatch.Frequency / 1_000_000.0;
        var sorted = sample.PerNodeTicks.OrderBy(t => t).ToList();
        double p50 = Percentile(sorted, 0.50) / ticksPerUs;
        double p95 = Percentile(sorted, 0.95) / ticksPerUs;
        string truncation = sample.Truncated ? $"（節點預算 {sample.VisitedNodes} 即停——未走完全樹，樣本為截斷集）" : "";
        return new HardwareFact(Key, Category, "ENUM 樹讀取延遲",
            $"走訪 {sample.VisitedNodes} 個節點{truncation}；逐節點開啟+列舉延遲 p50={p50:0.##} µs、p95={p95:0.##} µs"
            + "（含本程式 API 成本，不扣除——與 FS metadata 量測同一立場）",
            "µs", Source, FactTrustLevel.Measured, false, at, p50, FactAvailability.Present, null);
    }

    /// <summary>純函式：有序樣本的百分位（最近鄰取法）。</summary>
    public static long Percentile(IReadOnlyList<long> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        int idx = (int)Math.Ceiling(q * sorted.Count) - 1;
        if (idx < 0) idx = 0;
        if (idx >= sorted.Count) idx = sorted.Count - 1;
        return sorted[idx];
    }

    // ── 真實登錄檔走訪（薄；測試以注入取代）──

    private static EnumLatencySample RealProbe(int nodeBudget)
    {
        const string enumPath = @"SYSTEM\CurrentControlSet\Enum";
        using var root = Registry.LocalMachine.OpenSubKey(enumPath)
            ?? throw new InvalidOperationException($"找不到 {enumPath}（本機沒有 PnP 裝置安裝資料庫）");
        var ticks = new List<long>();
        bool truncated = false;
        int visited = 0;
        var enumerators = root.GetSubKeyNames();
        foreach (var enumerator in enumerators)
        {
            if (visited >= nodeBudget) { truncated = true; break; }
            long start = Stopwatch.GetTimestamp();
            using var devKey = root.OpenSubKey(enumerator);
            var devices = devKey?.GetSubKeyNames() ?? [];
            long mid = Stopwatch.GetTimestamp();
            ticks.Add(mid - start);
            visited++;
            foreach (var device in devices)
            {
                if (visited >= nodeBudget) { truncated = true; break; }
                long s2 = Stopwatch.GetTimestamp();
                using var instanceKey = devKey!.OpenSubKey(device);
                _ = instanceKey?.GetSubKeyNames();
                ticks.Add(Stopwatch.GetTimestamp() - s2);
                visited++;
            }
            if (truncated) break;
        }
        return new EnumLatencySample(visited, ticks, truncated);
    }
}
