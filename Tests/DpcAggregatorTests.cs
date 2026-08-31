using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>DPC／ISR 排行的彙整與判讀（純函式）。</summary>
public class DpcAggregatorTests
{
    private static List<DpcRow> Rank(IEnumerable<DpcSample> samples, double windowSeconds = 10, int top = 20)
        => DpcAggregator.Rank(DpcAggregator.Accumulate(samples), windowSeconds, top);

    [Fact]
    public void 以單次最長時長排序而非次數()
    {
        // 會造成爆音的是「某一次跑了 2 ms」，不是「跑了一萬次但每次 1 µs」。
        var rows = Rank(new[]
        {
            new DpcSample("nvlddmkm.sys", "DPC", 2.0),
            new DpcSample("ndis.sys", "DPC", 0.001),
            new DpcSample("ndis.sys", "DPC", 0.001),
            new DpcSample("ndis.sys", "DPC", 0.001),
        });
        Assert.Equal("nvlddmkm.sys", rows[0].Module);
        Assert.Equal(1, rows[0].Count);
        Assert.Equal(3, rows[1].Count);
    }

    [Fact]
    public void 統計值_次數平均最大與佔用比例()
    {
        var rows = Rank(new[]
        {
            new DpcSample("ndis.sys", "DPC", 0.4),
            new DpcSample("ndis.sys", "DPC", 0.6),
        }, windowSeconds: 10);
        var r = Assert.Single(rows);
        Assert.Equal(2, r.Count);
        Assert.Equal(600, r.MaxUs, 3);          // 0.6 ms = 600 µs
        Assert.Equal(500, r.MeanUs, 3);         // (400+600)/2
        Assert.Equal(0.01, r.BusyPercent, 6);   // 1 ms / 10 s
        Assert.Equal("600 µs", r.MaxText);
        Assert.Equal("500.0 µs", r.AvgText);
    }

    [Fact]
    public void 模組與類別分開統計()
    {
        var rows = Rank(new[]
        {
            new DpcSample("ndis.sys", "DPC", 0.2),
            new DpcSample("ndis.sys", "ISR", 0.3),
        });
        Assert.Equal(2, rows.Count);
        Assert.Equal("ISR", rows[0].Kind);      // 0.3 ms 比 0.2 ms 久
        Assert.Equal("DPC", rows[1].Kind);
    }

    [Fact]
    public void 時長為0時退化成頻次排行且時長顯示破折號()
    {
        // 極短常式 ETW 會回 0；此時不能印出「0 µs」假裝量到了。
        var rows = Rank(new[]
        {
            new DpcSample("a.sys", "DPC", 0),
            new DpcSample("b.sys", "DPC", 0),
            new DpcSample("b.sys", "DPC", 0),
        });
        Assert.Equal("b.sys", rows[0].Module);
        Assert.Equal("—", rows[0].MaxText);
        Assert.Equal("—", rows[0].AvgText);
        // 長條退回以次數為準，才不會全部一樣長
        Assert.Equal(1, rows[0].BarFraction, 6);
        Assert.Equal(0.5, rows[1].BarFraction, 6);
    }

    [Fact]
    public void 長條比例以最久的那列為滿格且有最小可見值()
    {
        var rows = Rank(new[]
        {
            new DpcSample("big.sys", "DPC", 10),
            new DpcSample("tiny.sys", "DPC", 0.0001),
        });
        Assert.Equal(1, rows[0].BarFraction, 6);
        Assert.Equal(0.02, rows[1].BarFraction, 6);   // 極小值仍保留最小可見長度
    }

    [Fact]
    public void 只取前N名()
    {
        var samples = Enumerable.Range(0, 50).Select(i => new DpcSample($"m{i}.sys", "DPC", i)).ToList();
        Assert.Equal(20, Rank(samples).Count);
        Assert.Equal(5, Rank(samples, 10, 5).Count);
        Assert.Equal("m49.sys", Rank(samples, 10, 5)[0].Module);
    }

    [Fact]
    public void 量測窗為0時佔用比例為0而不是無限大()
    {
        var rows = Rank(new[] { new DpcSample("a.sys", "DPC", 1) }, windowSeconds: 0);
        Assert.Equal(0, rows[0].BusyPercent);
        Assert.Equal("—", rows[0].BusyText);
    }

    // ── 判讀 ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(499, 0)]
    [InlineData(500, 1)]
    [InlineData(999, 1)]
    [InlineData(1000, 2)]
    [InlineData(5000, 2)]
    public void 判讀門檻(double maxUs, int expected) => Assert.Equal(expected, DpcAggregator.Judge(maxUs));

    [Fact]
    public void 結論_沒量到事件時說沒量到而不是說沒問題()
    {
        var text = DpcAggregator.Verdict([]);
        Assert.Contains("沒有量到", text);
    }

    [Fact]
    public void 結論_平台不給時長時明說只有頻次()
    {
        var rows = Rank(new[] { new DpcSample("a.sys", "DPC", 0) });
        var text = DpcAggregator.Verdict(rows);
        Assert.Contains("沒有帶回執行時長", text);
        Assert.DoesNotContain("µs 以下", text);
    }

    [Fact]
    public void 結論_超過門檻時給警示並附上肇事模組()
    {
        var rows = Rank(new[] { new DpcSample("nvlddmkm.sys", "DPC", 2.5) });
        var text = DpcAggregator.Verdict(rows);
        Assert.StartsWith("⚠", text);
        Assert.Contains("nvlddmkm.sys", text);
        Assert.Contains("2,500 µs", text);
        Assert.Contains("不是判決", text);   // 統計不下判決，這句不能掉
    }

    [Fact]
    public void 結論_全部低於門檻時明說沒有哪支驅動吃住CPU()
    {
        var rows = Rank(new[] { new DpcSample("ndis.sys", "DPC", 0.05) });
        var text = DpcAggregator.Verdict(rows);
        Assert.DoesNotContain("⚠", text);
        Assert.Contains("沒有哪支驅動吃住", text);
    }
}
