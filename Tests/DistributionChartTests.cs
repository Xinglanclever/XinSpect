using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 延遲分布圖（U10-VZ-0009 直方圖／<c>viz.hist</c>；U10-VZ-0103 ECDF／<c>viz.ecdf</c>）的
/// <b>數學守門</b>：分桶、百分位、ECDF 與座標映射全是純函式，用合成資料直接驗證，
/// 不開 UI（畫法本身沿 FrameTimeChart／Sparkline 的既有先例，數學錯了畫面才會錯）。
/// </summary>
public class DistributionChartTests
{
    // ── 百分位：最近排名（與 DiskLatencyStats 同一條規矩）────────────────────

    [Fact]
    public void 百分位_最近排名_取真的量到的那一筆()
    {
        // 1..100：P50=第50筆、P95=第95筆、P99=第99筆（⌈p×N⌉）
        var sorted = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        var (p50, p95, p99) = LatencyDistributionMath.Percentiles(sorted);

        Assert.Equal(50, p50);
        Assert.Equal(95, p95);
        Assert.Equal(99, p99);
    }

    [Fact]
    public void 百分位_十筆樣本_P95落在第10筆_不內插出量不到的數()
    {
        var sorted = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var (p50, p95, p99) = LatencyDistributionMath.Percentiles(sorted);

        Assert.Equal(5, p50);     // ⌈0.5×10⌉=5 → 第5筆
        Assert.Equal(10, p95);    // ⌈0.95×10⌉=10 → 第10筆
        Assert.Equal(10, p99);
    }

    [Fact]
    public void 百分位_空樣本_如實拒絕_不回0()
    {
        Assert.Throws<ArgumentException>(() => LatencyDistributionMath.Percentile([], 0.5));
    }

    // ── 直方圖：等寬分桶 ─────────────────────────────────────────────────────

    [Fact]
    public void 等寬分桶_計數與邊界如實()
    {
        // 0..20 十一格值、十格：寬 2，首格 [0,2)、尾格 [18,20] 含上端（20 夾進最後一格）
        var values = Enumerable.Range(0, 21).Select(i => (double)i).ToList();
        var r = LatencyDistributionMath.Histogram(values, 10, logBuckets: false);

        Assert.Equal(21, r.Used);
        Assert.Equal(10, r.Buckets.Count);
        Assert.Equal(0, r.Buckets[0].From);
        Assert.Equal(2, r.Buckets[0].To);
        Assert.Equal(2, r.Buckets[0].Count);       // 0、1
        Assert.Equal(2, r.Buckets[5].Count);       // 10、11
        Assert.Equal(18, r.Buckets[^1].From);
        Assert.Equal(3, r.Buckets[^1].Count);      // 18、19、20（含上端）
        Assert.Equal(21, r.Buckets.Sum(b => b.Count));
    }

    [Fact]
    public void 等寬分桶_恰為最大值的樣本_落在最後一格而不是虛構一格外()
    {
        var values = new double[] { 0, 10 };
        var r = LatencyDistributionMath.Histogram(values, 2, logBuckets: false);

        Assert.Equal(2, r.Buckets.Count);
        Assert.Equal(1, r.Buckets[0].Count);   // [0,5)
        Assert.Equal(1, r.Buckets[1].Count);   // [5,10]——含上端
    }

    [Fact]
    public void 分桶_中間的空格留著_頭尾的空格砍掉()
    {
        // 快慢兩群：10 筆在 10 附近、10 筆在 1000 附近——中間的空白本身就是要看的東西
        var values = Enumerable.Repeat(10.0, 10).Concat(Enumerable.Repeat(1000.0, 10)).ToList();
        var r = LatencyDistributionMath.Histogram(values, 10, logBuckets: false);

        Assert.Equal(10, r.Buckets.Count);
        Assert.Equal(10, r.Buckets[0].Count);      // 頭格：快群（下界＝最小值 10）
        Assert.Equal(10, r.Buckets[^1].Count);     // 尾格：慢群
        Assert.Equal(0, r.Buckets[4].Count);       // 中間的空格留著，不因為是 0 而被拿掉
        Assert.Equal(10, r.Buckets[0].From);       // 頭格從最小值（10µs）開始
    }

    // ── 直方圖：對數分桶 ─────────────────────────────────────────────────────

    [Fact]
    public void 對數分桶_長尾樣本有自己的格子_不會全擠在第一格()
    {
        // 90 筆 ~10µs ＋ 10 筆 ~10000µs：等寬分桶時慢的那群會跟快的一起擠在最左格
        var values = Enumerable.Repeat(10.0, 90).Concat(Enumerable.Repeat(10_000.0, 10)).ToList();
        var log = LatencyDistributionMath.Histogram(values, 10, logBuckets: true);
        var linear = LatencyDistributionMath.Histogram(values, 10, logBuckets: false);

        Assert.True(log.Log);
        Assert.False(linear.Log);
        // 線性：快群獨占第一格，慢群也在前段（10 萬範圍裡 1 萬只佔 1/10）——兩群可分但長尾被壓扁
        // 對數：慢群應該落在明顯靠後的格子（跨兩個數量級）
        int logSlowBin = IndexOfBin(log, 10_000.0);
        Assert.True(logSlowBin >= log.Buckets.Count / 2,
            $"對數分桶下 10⁴µs 應落在後半段，實際在第 {logSlowBin}／{log.Buckets.Count} 格。");
        // 總數如實：兩種分桶的 Used 一致
        Assert.Equal(100, log.Used);
        Assert.Equal(100, linear.Used);
    }

    [Fact]
    public void 對數分桶_含非正值時_如實退化成等寬()
    {
        // 對數軸上沒有 0 的位置：含 0／負值就整體退回等寬，不硬塞
        var values = new double[] { -1, 0, 5, 50, 500 };
        var r = LatencyDistributionMath.Histogram(values, 4, logBuckets: true);

        Assert.False(r.Log);
        Assert.Equal(5, r.Used);
    }

    [Fact]
    public void 對數分桶_全段等值_一格說清楚()
    {
        var values = Enumerable.Repeat(42.0, 7).ToList();
        var r = LatencyDistributionMath.Histogram(values, 8, logBuckets: true);

        var b = Assert.Single(r.Buckets);
        Assert.Equal(7, b.Count);
        Assert.Equal(42, b.From);
    }

    [Fact]
    public void 分桶_髒值剔除且Used如實_不假裝樣本還在()
    {
        var values = new double[] { 1, 2, double.NaN, 3, double.PositiveInfinity, 4 };
        var r = LatencyDistributionMath.Histogram(values, 4, logBuckets: true);

        Assert.Equal(4, r.Used);          // NaN 與 ∞ 不是樣本
        Assert.Equal(6, values.Length);   // 原清單不動
        Assert.Equal(4, r.Buckets.Sum(b => b.Count));
    }

    [Fact]
    public void 分桶_空清單_回沒有樣本_不丟例外()
    {
        var r = LatencyDistributionMath.Histogram([], 16, logBuckets: true);
        Assert.Empty(r.Buckets);
        Assert.Equal(0, r.Used);
    }

    // ── ECDF ────────────────────────────────────────────────────────────────

    [Fact]
    public void ECDF_排序遞增_末端恰為1()
    {
        var pts = LatencyDistributionMath.Ecdf(new double[] { 3, 1, 2 });

        Assert.Equal(3, pts.Length);
        Assert.Equal(1, pts[0].X);
        Assert.Equal(3, pts[^1].X);
        Assert.All(pts, p => Assert.InRange(p.P, 0, 1));
        Assert.Equal(1.0, pts[^1].P, 12);      // 最大的樣本：累積到 100%
        Assert.True(pts.Zip(pts.Skip(1)).All(z => z.Second.X > z.First.X), "X 應嚴格遞增。");
        Assert.True(pts.Zip(pts.Skip(1)).All(z => z.Second.P >= z.First.P), "P 應單調不減。");
    }

    [Fact]
    public void ECDF_同值樣本合併成階梯踏面_比例跳到N分之N()
    {
        // 1, 2, 2, 3：第 2、3 筆同值 → 同一個踏面，P 從 2/4 跳到 3/4
        var pts = LatencyDistributionMath.Ecdf(new double[] { 1, 2, 2, 3 });

        Assert.Equal(3, pts.Length);           // 同值合併後只剩 3 個踏面
        Assert.Equal((2.0, 0.75), pts[1]);     // 第 3 筆（含）的累積＝3/4
        Assert.Equal((3.0, 1.0), pts[2]);
    }

    [Fact]
    public void ECDF_空清單_回空_不虛構曲線()
        => Assert.Empty(LatencyDistributionMath.Ecdf([]));

    // ── 座標映射 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 座標映射_min貼底max貼頂_中點在中線()
    {
        // top=100, height=200：min→300（底）、max→100（頂）、中點→200
        Assert.Equal(300, LatencyDistributionMath.MapY(0, 0, 10, 100, 200), 9);
        Assert.Equal(100, LatencyDistributionMath.MapY(10, 0, 10, 100, 200), 9);
        Assert.Equal(200, LatencyDistributionMath.MapY(5, 0, 10, 100, 200), 9);
    }

    [Fact]
    public void 座標映射_全段等值_畫在中線_不除以零()
        => Assert.Equal(200, LatencyDistributionMath.MapY(7, 7, 7, 100, 200), 9);

    [Fact]
    public void 座標映射_比例夾限_超出0到1的輸入不畫出繪圖區()
    {
        Assert.Equal(50, LatencyDistributionMath.MapX(-3, 50, 100), 9);    // 負比例夾成 0
        Assert.Equal(150, LatencyDistributionMath.MapX(9, 50, 100), 9);    // 超過 1 夾成 1
    }

    [Fact]
    public void 標線位置_落在樣本所屬的格子比例上()
    {
        // 0..90 十值十格：寬 9，格 7＝[63,72)、格 8＝[72,81)
        var values = Enumerable.Range(0, 10).Select(i => (double)i * 10).ToList();
        var r = LatencyDistributionMath.Histogram(values, 10, logBuckets: false);

        Assert.Equal(0.0, LatencyDistributionMath.FractionOf(0, r.Buckets, log: false), 9);    // 恰在下界＝左緣
        Assert.Equal(0.8, LatencyDistributionMath.FractionOf(72, r.Buckets, log: false), 9);   // 恰為格 8 的下界＝8/10
        Assert.Equal(1.0, LatencyDistributionMath.FractionOf(999, r.Buckets, log: false), 9);  // 超出右端＝封頂
    }

    // ── 版面標示 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 延遲標示_微秒毫秒分開寫_不讓人自己換算()
    {
        Assert.Equal("750 µs", LatencyDistributionMath.LatencyText(750));
        Assert.Equal("1.5 ms", LatencyDistributionMath.LatencyText(1500));
        Assert.Equal("1 ms", LatencyDistributionMath.LatencyText(1000));
    }

    private static int IndexOfBin(LatencyDistributionMath.HistogramResult r, double value)
    {
        for (int i = 0; i < r.Buckets.Count; i++)
        {
            var b = r.Buckets[i];
            if (value >= b.From && (double.IsPositiveInfinity(b.To) || value < b.To)) return i;
        }
        return r.Buckets.Count - 1;
    }
}
