using System.Diagnostics;
using System.Numerics;

namespace XinSpect;

/// <summary>
/// U10-TS-0015／0016／0017「快取帶寬階梯」：在 L1／L2／L3 尺寸的工作集上量讀取帶寬（GB/s），
/// 看帶寬隨層級下降的階梯。判別碼家族前綴：<see cref="LevelPrefix"/>。
/// </summary>
/// <remarks>
/// <para>
/// 量的是：工作集小到住進某級快取時，本程式讀取迴圈可達的 GB/s。工作集逐級放大，
/// 帶寬階梯的「斷階」位置對照快取幾何。純 managed、零特權、只碰本行程自己配置的緩衝。
/// </para>
/// <para>
/// 誠實界線：①<b>使用者明示觸發才跑</b>（掛在 DeepBench 入口模式），不自動跑；
/// ②量的是本行程 managed 讀取迴圈達成的帶寬，不是快取的硬體上限；
/// ③工作集尺寸是<b>啟發式</b>（預設 32 KB／512 KB／16 MB），不是從 CPUID 快取葉解出的
/// 每核精確幾何——值裡如實帶著工作集大小，讀者自行對照該機的快取規格；
/// ④<b>主記憶體層級不重測</b>——DRAM 帶寬已由 memory.stream-bandwidth（MemBandwidthService，
/// 工作集遠大於 L3 的 STREAM 四型態）收口，本服務只補 L1／L2／三層快取內的缺失層級；
/// ⑤延遲階梯（ns）另有 CacheBenchService 的指標追逐——帶寬與延遲是兩個形狀，不互冒；
/// ⑥量測預算寫進常數（<see cref="PerLevelBudgetMs"/>），每層超過即停並標註截斷。
/// </para>
/// </remarks>
public static class CacheBandwidthLadderService
{
    private const string Category = "處理器";
    public const string LevelPrefix = "ts.cache.bw.";
    public const string Source = "managed Vector<float> 讀取累加迴圈（本行程自己的記憶體與執行緒；零特權）";

    /// <summary>量測預算：單一層級的時長上限（毫秒）。超過即停並標註截斷。</summary>
    public const int PerLevelBudgetMs = 300;

    /// <summary>預設的工作集階梯（啟發式）：L1 尺寸內／L2 尺寸內／L3 尺寸內。</summary>
    public static readonly (string Level, int Kilobytes)[] DefaultLevels =
    {
        ("l1", 32),
        ("l2", 512),
        ("l3", 16 * 1024),
    };

    /// <summary>一次取樣：層級、工作集大小、讀過的總位元組數、耗時（Stopwatch 刻）。</summary>
    public sealed record LevelSample(string Level, long WorkingSetBytes, long BytesRead, long Ticks, bool Truncated);

    /// <summary>收集快取帶寬階梯。測試以注入的樣本取代真實負載，測試不跑長負載。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<LevelSample>>? probe = null,
        IReadOnlyList<(string Level, int Kilobytes)>? levels = null)
    {
        if (probe is { } p)
        {
            try { return [.. p().Select(s => BuildFact(at, s))]; }
            catch (Exception ex)
            {
                return [new HardwareFact(LevelPrefix + "probe", Category, "快取帶寬階梯", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    "量測失敗：" + ex.Message)];
            }
        }
        try
        {
            return [.. (levels ?? DefaultLevels).Select(l => BuildFact(at, Measure(l.Level, l.Kilobytes)))];
        }
        catch (Exception ex)
        {
            return [new HardwareFact(LevelPrefix + "probe", Category, "快取帶寬階梯", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "量測失敗：" + ex.Message)];
        }
    }

    /// <summary>純函式：一次樣本 → 一把事實。鍵形 <c>ts.cache.bw.{l1|l2|l3}</c>。</summary>
    public static HardwareFact BuildFact(DateTimeOffset at, LevelSample sample)
    {
        string key = $"{LevelPrefix}{sample.Level.ToLowerInvariant()}";
        string name = $"快取讀取帶寬 {sample.Level.ToUpperInvariant()}";
        if (sample.WorkingSetBytes <= 0 || sample.BytesRead <= 0 || sample.Ticks <= 0)
            return new HardwareFact(key, Category, name, "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "工作集、讀取量或耗時非正值——樣本不可信，不推算");
        double seconds = sample.Ticks / (double)Stopwatch.Frequency;
        double gbps = sample.BytesRead / seconds / 1e9;
        string truncation = sample.Truncated ? $"（達 {PerLevelBudgetMs} ms 預算即停——樣本為截斷窗）" : "";
        return new HardwareFact(key, Category, name,
            $"{gbps:0.##} GB/s（工作集 {sample.WorkingSetBytes / 1024.0:0.#} KB）{truncation}" +
            "；主記憶體層級由 memory.stream-bandwidth 收口，本測試不重測 DRAM",
            "GB/s", Source, FactTrustLevel.Measured, false, at, gbps, FactAvailability.Present, null);
    }

    // ── 真實負載（使用者明示觸發才會走到）────────────────────────────────────

    private static LevelSample Measure(string level, int kilobytes)
    {
        long bytes = (long)kilobytes * 1024;
        int floats = (int)Math.Max(bytes / sizeof(float), Vector<float>.Count * 4);
        var buffer = new float[floats];
        for (int i = 0; i < floats; i++) buffer[i] = i & 0xFF;   // 初始化，避免全零頁共用
        int vecEnd = floats - (floats % Vector<float>.Count);

        ReadBlock(buffer, vecEnd);   // 暖機（讓 JIT 先編譯，不計時）
        var sw = Stopwatch.StartNew();
        long bytesRead = 0;
        bool truncated = false;
        while (sw.ElapsedMilliseconds < PerLevelBudgetMs)
        {
            bytesRead += ReadBlock(buffer, vecEnd);
            truncated = true;
        }
        sw.Stop();
        return new LevelSample(level, bytes, bytesRead, sw.ElapsedTicks, truncated);
    }

    /// <summary>讀過整個緩衝一次，回傳位元組數。累加值讀回防止 JIT 消除。</summary>
    private static long ReadBlock(float[] buffer, int vecEnd)
    {
        int w = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        for (int i = 0; i < vecEnd; i += w) acc += new Vector<float>(buffer, i);
        float sink = Vector.Dot(acc, Vector<float>.One);
        long bytes = (long)vecEnd * sizeof(float);
        return bytes + (sink == -1f ? 1 : 0);
    }
}
