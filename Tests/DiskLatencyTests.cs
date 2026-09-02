using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 磁碟隨機 4K 延遲分佈（<see cref="DiskLatencyStats"/>）的純計算檢查。
///
/// QD1 是逐次同步 I/O，每一筆的延遲本來就量到了，只是過去被平均成 IOPS 之後丟掉。
/// 這裡驗的是留下來的數字有沒有被算對：百分位取最近排名、不內插；直方圖不吞樣本；
/// 沒有樣本就如實說沒有，不填一個 0 上去。
/// </summary>
public class DiskLatencyTests
{
    /// <summary>計時器頻率取 1 MHz，一刻正好一微秒——測試裡寫的數字就是微秒。</summary>
    private const long MhzTimer = 1_000_000;

    private static DiskLatencyStats Of(params long[] ticksAsUs) =>
        DiskLatencyStats.FromTicks(ticksAsUs, MhzTimer);

    [Fact]
    public void 百分位取最近排名不內插()
    {
        // 1…100 µs 各一筆，故意打亂順序送進去（它得自己排序）
        var ticks = Enumerable.Range(1, 100).Select(i => (long)i).OrderBy(i => i * 37 % 101).ToArray();
        var s = DiskLatencyStats.FromTicks(ticks, MhzTimer);

        Assert.Equal(100, s.Count);
        Assert.Equal(1, s.MinUs, 3);
        Assert.Equal(50, s.P50Us, 3);       // 第 50 筆，不是 50.5——不內插出一個不存在的數字
        Assert.Equal(99, s.P99Us, 3);
        Assert.Equal(100, s.P999Us, 3);     // ceil(0.999×100)=100 → 最後一筆
        Assert.Equal(100, s.MaxUs, 3);
    }

    [Fact]
    public void 單一樣本時每個百分位都等於它()
    {
        var s = Of(842);
        Assert.Equal(1, s.Count);
        Assert.Equal(842, s.MinUs, 3);
        Assert.Equal(842, s.P50Us, 3);
        Assert.Equal(842, s.P999Us, 3);
        Assert.Equal(842, s.MaxUs, 3);
    }

    [Fact]
    public void 微秒由計時器頻率換算而來()
    {
        // Windows 上 Stopwatch.Frequency 常見為 10 MHz：25 刻＝2.5 µs
        var s = DiskLatencyStats.FromTicks([25, 25, 25], 10_000_000);
        Assert.Equal(2.5, s.P50Us, 3);
        Assert.Equal(2.5, s.MaxUs, 3);
    }

    [Fact]
    public void 沒有樣本就如實說沒有()
    {
        var s = DiskLatencyStats.FromTicks([], MhzTimer);
        Assert.False(s.HasData);
        Assert.Equal(0, s.Count);
        Assert.Empty(s.Buckets);
        Assert.Equal("—", s.P50Text);
        Assert.Equal("—", s.P999Text);
        Assert.Contains("沒有樣本", s.SummaryText);
    }

    [Fact]
    public void 直方圖涵蓋每一筆樣本()
    {
        var s = Of(3, 12, 55, 90, 140, 1_500, 9_000, 250_000);
        Assert.Equal(8, s.Count);
        Assert.Equal(8, s.Buckets.Sum(b => b.Count));
    }

    [Fact]
    public void 直方圖去掉頭尾的空區間()
    {
        var s = Of(60, 150);
        Assert.Equal(2, s.Buckets.Count);
        Assert.All(s.Buckets, b => Assert.Equal(1, b.Count));
        Assert.Equal("50–100 µs", s.Buckets[0].RangeText);
        Assert.Equal("100–200 µs", s.Buckets[1].RangeText);
    }

    [Fact]
    public void 中間的空區間留著看得出斷層()
    {
        // 會停頓的碟，快慢兩群之間是空的——那段空白本身就是要看的東西
        var s = Of(60, 60, 3_000);
        Assert.Equal(2, s.Buckets[0].Count);
        Assert.Contains(s.Buckets, b => b.Count == 0);
        Assert.Equal(3, s.Buckets.Sum(b => b.Count));
    }

    [Fact]
    public void 區間標示微秒與毫秒分開寫()
    {
        Assert.Equal("< 10 µs", Of(5).Buckets[0].RangeText);
        Assert.Equal("1–2 ms", Of(1_500).Buckets[0].RangeText);
        Assert.Equal("≥ 100 ms", Of(250_000).Buckets[0].RangeText);
    }

    [Fact]
    public void 佔比對總數長條對最大格()
    {
        // 三筆落在 50–100 µs、一筆落在 100–200 µs
        var s = Of(60, 70, 80, 150);
        var mode = s.Buckets[0];
        var tail = s.Buckets[1];

        Assert.Equal(75, mode.SharePercent, 3);        // 佔全部樣本的 3/4
        Assert.Equal(25, tail.SharePercent, 3);
        Assert.Equal(100, mode.BarPercent, 3);         // 最多的那一格把長條佔滿
        Assert.Equal(100.0 / 3, tail.BarPercent, 3);   // 其餘按比例
    }

    [Fact]
    public void 摘要句同時給中位數與尾端()
    {
        string t = Of(80, 90, 100).SummaryText;
        Assert.Contains("3 筆", t);
        Assert.Contains("中位數", t);
        Assert.Contains("p99.9", t);
    }
}
