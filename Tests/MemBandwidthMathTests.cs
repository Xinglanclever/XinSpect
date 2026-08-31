using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>記憶體頻寬與負載延遲的純函式（理論上限推算、達成率、階梯、判讀）。</summary>
public class MemBandwidthMathTests
{
    // ── 理論上限 ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(3200, 25.6)]   // DDR4-3200：64 位元 ×3200 MT/s
    [InlineData(2666, 21.328)]
    [InlineData(6000, 48.0)]   // DDR5-6000
    public void 每支模組上限是MTs乘8位元組(int mtps, double expected)
        => Assert.Equal(expected, MemBandwidthMath.PerModulePeakGbps(mtps), 3);

    [Fact]
    public void 速度不明時上限為0而不是猜一個()
    {
        Assert.Equal(0, MemBandwidthMath.PerModulePeakGbps(0));
        Assert.Equal(0, MemBandwidthMath.PerModulePeakGbps(-1));
        Assert.Equal(0, MemBandwidthMath.AssumedPeakGbps(3200, 0));
    }

    [Fact]
    public void 整機上限是每支上限乘模組數()
    {
        Assert.Equal(51.2, MemBandwidthMath.AssumedPeakGbps(3200, 2), 3);
        Assert.Equal(102.4, MemBandwidthMath.AssumedPeakGbps(3200, 4), 3);
    }

    // ── 換算與達成率 ────────────────────────────────────────────────────────

    [Fact]
    public void 位元組除秒數換成GBs()
    {
        Assert.Equal(10, MemBandwidthMath.Gbps(1e10, 1.0), 6);
        Assert.Equal(20, MemBandwidthMath.Gbps(1e10, 0.5), 6);
    }

    [Fact]
    public void 秒數為0或沒搬東西時回0而不是無限大()
    {
        Assert.Equal(0, MemBandwidthMath.Gbps(1e10, 0));
        Assert.Equal(0, MemBandwidthMath.Gbps(0, 1));
        Assert.Equal(0, MemBandwidthMath.Gbps(1e10, -1));
    }

    [Fact]
    public void 達成率_上限不明時回0不硬掰()
    {
        Assert.Equal(0.5, MemBandwidthMath.Efficiency(25.6, 51.2), 6);
        Assert.Equal(0, MemBandwidthMath.Efficiency(25.6, 0));
        Assert.Equal(0, MemBandwidthMath.Efficiency(0, 51.2));
    }

    [Fact]
    public void 達成率文字_上限不明時是破折號()
    {
        Assert.Equal("70%", MemBandwidthMath.EfficiencyNote(35.84, 51.2));
        Assert.Equal("—", MemBandwidthMath.EfficiencyNote(35.84, 0));
    }

    [Fact]
    public void 頻寬文字_量不到時是破折號而不是0()
    {
        Assert.Equal("25.60 GB/s", MemBandwidthMath.FormatGbps(25.6));
        Assert.Equal("—", MemBandwidthMath.FormatGbps(0));
        Assert.Equal("—", MemBandwidthMath.FormatGbps(-3));
    }

    // ── 執行緒階梯 ──────────────────────────────────────────────────────────

    [Fact]
    public void 執行緒階梯_2的次方加上邏輯處理器數本身()
    {
        Assert.Equal(new[] { 1 }, MemBandwidthMath.ThreadLadder(1));
        Assert.Equal(new[] { 1, 2 }, MemBandwidthMath.ThreadLadder(2));
        Assert.Equal(new[] { 1, 2, 4, 8 }, MemBandwidthMath.ThreadLadder(8));
        Assert.Equal(new[] { 1, 2, 4, 8, 16, 24 }, MemBandwidthMath.ThreadLadder(24));
        Assert.Equal(new[] { 1, 2, 4, 6 }, MemBandwidthMath.ThreadLadder(6));
    }

    [Fact]
    public void 執行緒階梯_不會重複也不會超過邏輯處理器數()
    {
        foreach (int lp in new[] { 1, 2, 3, 4, 6, 8, 12, 16, 20, 24, 32, 64, 128 })
        {
            var ladder = MemBandwidthMath.ThreadLadder(lp);
            Assert.Equal(ladder.Distinct().Count(), ladder.Length);
            Assert.All(ladder, t => Assert.InRange(t, 1, lp));
            Assert.Equal(lp, ladder[^1]);
        }
    }

    [Fact]
    public void 施壓階梯_從0開始且最多留一個核心量延遲()
    {
        Assert.Equal(new[] { 0 }, MemBandwidthMath.LoadLadder(1));
        Assert.Equal(new[] { 0, 1 }, MemBandwidthMath.LoadLadder(2));
        Assert.Equal(new[] { 0, 1, 2, 4, 7 }, MemBandwidthMath.LoadLadder(8));
        Assert.Equal(new[] { 0, 1, 2, 4, 8, 16, 23 }, MemBandwidthMath.LoadLadder(24));
    }

    [Fact]
    public void 施壓階梯_永遠不會用光所有邏輯處理器()
    {
        foreach (int lp in new[] { 2, 4, 6, 8, 16, 24, 32 })
        {
            var ladder = MemBandwidthMath.LoadLadder(lp);
            Assert.Equal(0, ladder[0]);
            Assert.Equal(lp - 1, ladder[^1]);
            Assert.Equal(ladder.Distinct().Count(), ladder.Length);
        }
    }

    // ── 判讀 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 判讀_沒量到時不下結論()
    {
        var (text, severity) = MemBandwidthMath.Judge(0, 3200, 2);
        Assert.Equal(0, severity);
        Assert.Contains("沒有量到", text);
    }

    [Fact]
    public void 判讀_速度不明時只給實測值並說明為何不做達成率()
    {
        var (text, severity) = MemBandwidthMath.Judge(35.0, 0, 2);
        Assert.Equal(0, severity);
        Assert.Contains("35.00 GB/s", text);
        Assert.Contains("SMBIOS 沒回報", text);
        Assert.DoesNotContain("%", text);
    }

    [Fact]
    public void 判讀_兩支模組卻只有一支的頻寬時點出插槽插錯()
    {
        // DDR4-3200 兩支：正常雙通道約 35 GB/s；只有 24 GB/s 幾乎就是單支上限 25.6
        var (text, severity) = MemBandwidthMath.Judge(24.0, 3200, 2);
        Assert.Equal(2, severity);
        Assert.StartsWith("⚠", text);
        Assert.Contains("25.60 GB/s", text);   // 單支上限
        Assert.Contains("插槽插錯", text);
        Assert.Contains("不是主機板回報", text);   // 明說這是實測推論
    }

    [Fact]
    public void 判讀_只插一支時不會誤報成插槽插錯()
    {
        // 單支模組本來就只有單支的頻寬，這不是問題
        var (text, severity) = MemBandwidthMath.Judge(20.0, 3200, 1);
        Assert.NotEqual(2, severity);
        Assert.DoesNotContain("插槽插錯", text);
    }

    [Fact]
    public void 判讀_達成率過低時提醒值得查但不當成故障()
    {
        var (text, severity) = MemBandwidthMath.Judge(35.0, 3200, 4);   // 上限 102.4，約 34%
        Assert.Equal(1, severity);
        Assert.Contains("偏低值得查", text);
        Assert.Contains("假設", text);   // 要說明上限是假設每支各佔一個通道
    }

    [Fact]
    public void 判讀_達成率正常時明說拿不到百分之百是正常的()
    {
        var (text, severity) = MemBandwidthMath.Judge(36.0, 3200, 2);   // 上限 51.2，約 70%
        Assert.Equal(0, severity);
        Assert.DoesNotContain("⚠", text);
        Assert.Contains("70%", text);
        Assert.Contains("60–85%", text);
    }

    // ── 負載延遲結論 ────────────────────────────────────────────────────────

    private static LoadedLatencyRow Row(int loaders, double gbps, double ns)
        => new(loaders, gbps, ns, 1);

    [Fact]
    public void 負載結論_沒資料時說沒量測()
        => Assert.Contains("尚未量測", MemBandwidthMath.SummarizeLoaded([]));

    [Fact]
    public void 負載結論_缺無負載那一點時不做對照()
    {
        var text = MemBandwidthMath.SummarizeLoaded([Row(4, 30, 200)]);
        Assert.Contains("資料不足", text);
    }

    [Fact]
    public void 負載結論_漲到三倍以上時說明是記憶體子系統瓶頸()
    {
        var text = MemBandwidthMath.SummarizeLoaded([Row(0, 0, 80), Row(4, 30, 260)]);
        Assert.Contains("80.0 ns", text);
        Assert.Contains("260.0 ns", text);
        Assert.Contains("3.3 倍", text);
        Assert.Contains("30.00 GB/s", text);
        Assert.Contains("不是往 CPU", text);
    }

    [Fact]
    public void 負載結論_幅度常見時不誇大()
    {
        var text = MemBandwidthMath.SummarizeLoaded([Row(0, 0, 80), Row(4, 30, 140)]);
        Assert.Contains("常見範圍", text);
        Assert.DoesNotContain("瓶頸", text);
    }

    [Fact]
    public void 負載結論_取延遲最高的那一點來對照()
    {
        // 施壓最多的那一級不一定延遲最高（排程波動），以實際最高者為準
        var text = MemBandwidthMath.SummarizeLoaded([Row(0, 0, 80), Row(2, 20, 300), Row(4, 30, 200)]);
        Assert.Contains("300.0 ns", text);
        Assert.Contains("2 執行緒施壓", text);
    }

    [Fact]
    public void 負載列_無負載那一列不顯示頻寬()
    {
        Assert.Equal("—", Row(0, 0, 80).GbpsText);
        Assert.Equal("無負載", Row(0, 0, 80).LoadText);
        Assert.Equal("4 執行緒施壓", Row(4, 30, 200).LoadText);
        Assert.Equal("30.00 GB/s", Row(4, 30, 200).GbpsText);
    }

    [Fact]
    public void 頻寬列_文字組成()
    {
        var row = new MemBandwidthRow("三元運算", 8, 42.5, 0.9, "83%");
        Assert.Equal("8 執行緒", row.ThreadsText);
        Assert.Equal("42.50 GB/s", row.GbpsText);
        Assert.Equal("83%", row.Note);
    }

    // ── 切段：互斥且覆蓋 ────────────────────────────────────────────────────
    // 這組測試釘住的是一個曾經真的發生過的記帳錯誤：讓每條執行緒都讀「整個」陣列卻各記一整份
    // 位元組，第一條把快取行拉進 L3 之後其餘全變快取命中，於是「達成頻寬」被算成 156 GB/s——
    // 比同一頁印出的理論上限 115 GB/s 還高。每條執行緒必須各有一段、記帳只記自己那段。

    [Theory]
    [InlineData(1000, 1, 1)]
    [InlineData(1000, 4, 1)]
    [InlineData(1000, 7, 4)]
    [InlineData(1000, 36, 4)]
    [InlineData(24_000_000, 35, 8)]
    [InlineData(10, 36, 4)]      // 執行緒比元素還多
    public void 切段_每段互斥且合起來覆蓋整個範圍(int n, int threads, int width)
    {
        var covered = new bool[n];
        int prevHi = 0;
        for (int t = 0; t < threads; t++)
        {
            var (lo, hi) = MemBandwidthMath.Slice(n, threads, t, width);
            Assert.True(lo <= hi, $"第 {t} 段起點大於終點：{lo} > {hi}");
            Assert.InRange(lo, 0, n);
            Assert.InRange(hi, 0, n);
            Assert.True(lo >= prevHi, $"第 {t} 段與前一段重疊：lo={lo} < 前一段 hi={prevHi}");
            for (int i = lo; i < hi; i++)
            {
                Assert.False(covered[i], $"元素 {i} 被兩條執行緒同時算到");
                covered[i] = true;
            }
            prevHi = Math.Max(prevHi, hi);
        }
        Assert.All(covered, c => Assert.True(c, "有元素沒被任何一段覆蓋"));
    }

    [Fact]
    public void 切段_起點對齊向量寬度()
    {
        for (int t = 0; t < 8; t++)
        {
            var (lo, _) = MemBandwidthMath.Slice(1_000_003, 8, t, 4);
            Assert.Equal(0, lo % 4);
        }
    }

    [Fact]
    public void 切段_參數不合理時回傳空段而不是丟例外()
    {
        Assert.Equal((0, 0), MemBandwidthMath.Slice(0, 4, 0));
        Assert.Equal((0, 0), MemBandwidthMath.Slice(100, 0, 0));
        Assert.Equal((0, 0), MemBandwidthMath.Slice(100, 4, -1));
        Assert.Equal((0, 0), MemBandwidthMath.Slice(100, 4, 4));
    }
}
