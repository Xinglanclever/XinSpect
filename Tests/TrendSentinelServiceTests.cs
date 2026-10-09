using System.IO;
using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 趨勢哨兵服務層的驗證：把歷史倉的資料餵進 <see cref="TrendSentinelService"/>，
/// 檢查三態誠實是否真的成立——資料不足說不足、沒感測器說沒感測器、有變化才報。
/// </summary>
/// <remarks>
/// 用 <see cref="HistoryStore"/> 的內部取樣入口餵入合成資料，全程不碰真實硬體，
/// 因此結果可重現、可斷言到具體數字。
/// </remarks>
public class TrendSentinelServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "XinSpect-sentinel-" + Guid.NewGuid().ToString("N"));

    public TrendSentinelServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static double[] Noise(int n, double amplitude, ulong seed)
    {
        var v = new double[n];
        ulong s = seed;
        for (int i = 0; i < n; i++)
        {
            s ^= s << 13; s ^= s >> 7; s ^= s << 17;
            v[i] = ((s >> 11) / (double)(1UL << 53) - 0.5) * 2.0 * amplitude;
        }
        return v;
    }

    // 把一整段合成資料餵進歷史倉（每分鐘一點）
    private HistoryStore Fill(Func<int, double[]> metricAt)
    {
        var store = new HistoryStore(_dir);
        int count = metricAt(-1).Length;
        // 資料基準必須落在下方各測試用的查詢窗 [UtcNow−1 天, UtcNow+1 天] 之內。
        // 這裡原本寫死 new DateTime(2026, 10, 8, 0, 0, 0, Utc)：寫測試當天樣本確實在窗內，
        // 隔天 UTC 05:00 起窗的起點就越過樣本尾端——Query 回空、8 條斷言同時紅。
        // 服務本身沒有壞，是測試自己的定時炸彈；任何寫死的資料基準配上相對查詢窗都會這樣爛掉。
        var t0 = DateTime.UtcNow.AddMinutes(-(count + 1));
        for (int i = 0; i < count; i++)
        {
            var values = new double[HistoryMetrics.Count];
            for (int m = 0; m < HistoryMetrics.Count; m++) values[m] = metricAt(m)[i];
            store.Sample(ToFloat(values), t0.AddMinutes(i));
        }
        store.Flush();
        return store;
    }

    private static float[] ToFloat(double[] v)
    {
        var f = new float[v.Length];
        for (int i = 0; i < v.Length; i++) f[i] = (float)v[i];
        return f;
    }

    private static HistoryStore Empty() => new(Path.Combine(
        Path.GetTempPath(), "XinSpect-sentinel-empty-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void 空歷史倉_回報沒有資料而不是硬給趨勢()
    {
        var svc = new TrendSentinelService();
        svc.Analyze(Empty(), DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

        Assert.Empty(svc.Findings);
        Assert.False(svc.HasFindings);
        Assert.Contains("沒有資料", svc.StatusText);
    }

    [Fact]
    public void 資料點不足_不報趨勢也不報變化點()
    {
        // 只有 10 點：低於兩個方法的下限
        var store = Fill(m => Enumerable.Range(0, 10).Select(i => 50.0 + i).ToArray());
        var svc = new TrendSentinelService();
        svc.Analyze(store, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        Assert.Empty(svc.Findings);
        Assert.Equal("本區間沒有統計上站得住腳的變化——這是有意義的結果", svc.StatusText);
    }

    [Fact]
    public void 全部指標皆零_回報本機無此感測器()
    {
        // 整段 0＝全部通道都沒讀到（歷史倉的「沒讀到」表示法）
        var store = Fill(m => new double[120]);
        var svc = new TrendSentinelService();
        svc.Analyze(store, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        Assert.Empty(svc.Findings);
        Assert.Contains("讀不到", svc.StatusText);
        Assert.Contains("本機無此感測器", svc.CoverageText);
    }

    [Fact]
    public void 上升趨勢_報出趨勢且方向為上升()
    {
        // 處理器溫度單調上升 + 小雜訊；其他指標保持恆定（視為無讀值的 0 會被排除）
        var temps = new double[200];
        var noise = Noise(200, 0.3, 555);
        for (int i = 0; i < temps.Length; i++) temps[i] = 45 + 0.10 * i + noise[i];

        var store = Fill(m => m == HistoryMetrics.CpuTemp ? temps : new double[200]);
        var svc = new TrendSentinelService();
        svc.Analyze(store, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        var trend = svc.Findings.FirstOrDefault(f => f.MetricIndex == HistoryMetrics.CpuTemp
                                                     && f.Headline.Contains("趨勢"));
        Assert.NotNull(trend);
        Assert.Contains("上升", trend!.Headline);
        Assert.Contains("Theil–Sen", trend.Detail);
        // 每小時約 6 °C（0.10/分 × 60）
        Assert.Contains("每小時約 6", trend.Headline);
    }

    [Fact]
    public void 突變_報出變化點且時間點對得上()
    {
        // 前 100 分鐘 45 °C、之後跳 60 °C；開頭要有足夠基準段供 CUSUM 估計
        var temps = new double[300];
        var noise = Noise(300, 0.3, 999);
        for (int i = 0; i < temps.Length; i++) temps[i] = (i < 100 ? 45.0 : 60.0) + noise[i];

        var store = Fill(m => m == HistoryMetrics.CpuTemp ? temps : new double[300]);
        var svc = new TrendSentinelService();
        svc.Analyze(store, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        var cp = svc.Findings.FirstOrDefault(f => f.Headline.Contains("上移"));
        Assert.NotNull(cp);
        Assert.Contains("CUSUM", cp!.Detail);
        Assert.Contains("交叉判讀", cp.Detail);   // 誠實聲明必須在依據欄
    }

    [Fact]
    public void 純雜訊_不報趨勢也不報變化點()
    {
        var temps = new double[300];
        var noise = Noise(300, 1.0, 31337);
        for (int i = 0; i < temps.Length; i++) temps[i] = 50 + noise[i];

        var store = Fill(m => m == HistoryMetrics.CpuTemp ? temps : new double[300]);
        var svc = new TrendSentinelService();
        svc.Analyze(store, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        Assert.DoesNotContain(svc.Findings, f => f.Headline.Contains("趨勢"));
        Assert.DoesNotContain(svc.Findings, f => f.Headline.Contains("上移") || f.Headline.Contains("下移"));
    }

    [Fact]
    public void 覆蓋範圍如實申報有讀值與無感測器的項數()
    {
        // 只有處理器溫度有值：1 項有讀值、其餘（Count−1）項本機無感測器。
        // 這裡以 HistoryMetrics.Count 相對表達，新增指標時不必改測試——但斷言的
        // 語意不變：有幾項讀值、有幾項沒感測器，一律如實申報。
        var temps = new double[60];
        for (int i = 0; i < temps.Length; i++) temps[i] = 50;
        var store = Fill(m => m == HistoryMetrics.CpuTemp ? temps : new double[60]);

        var svc = new TrendSentinelService();
        svc.Analyze(store, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        Assert.Contains("1 項指標有讀值", svc.CoverageText);
        Assert.Contains($"{HistoryMetrics.Count - 1} 項本機無此感測器", svc.CoverageText);
    }

    [Fact]
    public void 負載與溫度後期脫鉤_報出關聯減弱()
    {
        // 前 150 分鐘溫度緊跟負載；後 150 分鐘溫度被固定墊高（不再隨負載變動），完全脫鉤。
        // 這是散熱劣化／感測器位移的統計指紋：前期高度耦合、後期失去耦合。
        var load = new double[300];
        var temp = new double[300];
        var noise = Noise(300, 1.0, 6060);
        for (int i = 0; i < 300; i++)
        {
            load[i] = 50 + 20 * Math.Sin(i / 12.0) + noise[i];
            double coupled = 45 + 0.8 * (load[i] - 50);
            temp[i] = (i < 150 ? coupled : 45 + 12) + noise[i];
        }

        var store = Fill(m => m switch
        {
            HistoryMetrics.CpuLoad => load,
            HistoryMetrics.CpuTemp => temp,
            _ => new double[300],
        });
        var svc = new TrendSentinelService();
        svc.Analyze(store, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        var rel = svc.Findings.FirstOrDefault(f => f.Headline.Contains("關聯"));
        Assert.NotNull(rel);
        Assert.Contains("不代表因果", rel!.Detail);   // 誠實聲明必須在依據欄
        Assert.Contains("Pearson", rel.Detail);
    }

    [Fact]
    public void 負載與溫度始終耦合_不報關聯減弱()
    {
        // 全程耦合是正常狀態，不該佔版面
        var load = new double[300];
        var temp = new double[300];
        var noise = Noise(300, 1.0, 6060);
        for (int i = 0; i < 300; i++)
        {
            load[i] = 50 + 20 * Math.Sin(i / 12.0) + noise[i];
            temp[i] = 45 + 0.8 * (load[i] - 50) + noise[i];
        }

        var store = Fill(m => m switch
        {
            HistoryMetrics.CpuLoad => load,
            HistoryMetrics.CpuTemp => temp,
            _ => new double[300],
        });
        var svc = new TrendSentinelService();
        svc.Analyze(store, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        Assert.DoesNotContain(svc.Findings, f => f.Headline.Contains("關聯"));
    }

    [Fact]
    public void 降採樣後仍如實標示樣本數()
    {
        // 1200 點遠超 MaxPoints：分析必須降採樣，且依據欄要寫出實際用到的點數
        var temps = new double[1200];
        var noise = Noise(1200, 0.3, 4242);
        for (int i = 0; i < temps.Length; i++) temps[i] = 45 + 0.05 * i + noise[i];

        var store = Fill(m => m == HistoryMetrics.CpuTemp ? temps : new double[1200]);
        var svc = new TrendSentinelService();
        svc.Analyze(store, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        var trend = svc.Findings.FirstOrDefault(f => f.Headline.Contains("趨勢"));
        Assert.NotNull(trend);
        Assert.Contains($"{TrendSentinelService.MaxPoints} 點", trend!.Detail);
    }
}
