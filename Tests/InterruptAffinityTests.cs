using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 中斷落在哪顆核心：逐核歸因已經會說「哪顆核的 ISR 佔比高」，但不說是誰造成的。
///
/// 這一份守的是彙整與判讀：把每一筆中斷事件的（驅動、核心）配對數出來，看得出
/// 「core 13 的中斷幾乎全部來自 nvlddmkm」還是「分散在十幾顆核上」。
/// 界線：ETW 給的是<b>驅動映像</b>，不是裝置實例——一支驅動可能服務好幾個裝置，
/// 所以文字一律說「驅動」，不假裝知道是哪張卡。
/// </summary>
public class InterruptAffinityTests
{
    private static List<IsrSample> Samples(params (string Module, int Cpu, int Times)[] spec)
    {
        var list = new List<IsrSample>();
        foreach (var (m, c, n) in spec)
            for (int i = 0; i < n; i++) list.Add(new IsrSample(m, c));
        return list;
    }

    [Fact]
    public void 邊收邊累加與留全量事件必須得到相同結果()
    {
        // 服務在量測時是即時累加成計數字典的（不留原始事件，否則忙碌機器一分鐘上百萬筆）。
        // 兩條路徑若給出不同答案，畫面上的數字就不可信了。
        var samples = Samples(("a.sys", 0, 7), ("b.sys", 3, 11), ("a.sys", 3, 5));
        var counts = InterruptAffinityAggregator.Accumulate(samples);

        var viaSamples = InterruptAffinityAggregator.ByCpu(samples);
        var viaCounts = InterruptAffinityAggregator.ByCpu(counts);
        Assert.Equal(viaSamples.Count, viaCounts.Count);
        for (int i = 0; i < viaSamples.Count; i++)
        {
            Assert.Equal(viaSamples[i].Cpu, viaCounts[i].Cpu);
            Assert.Equal(viaSamples[i].Count, viaCounts[i].Count);
            Assert.Equal(viaSamples[i].TopModule, viaCounts[i].TopModule);
        }

        var mSamples = InterruptAffinityAggregator.ByModule(samples);
        var mCounts = InterruptAffinityAggregator.ByModule(counts);
        Assert.Equal(mSamples.Select(r => (r.Module, r.Count, r.TopCpu)),
                     mCounts.Select(r => (r.Module, r.Count, r.TopCpu)));
    }

    [Fact]
    public void 沒有事件時兩張表都是空的且不下判決()
    {
        Assert.Empty(InterruptAffinityAggregator.ByCpu([]));
        Assert.Empty(InterruptAffinityAggregator.ByModule([]));
        Assert.Contains("沒有量到", InterruptAffinityAggregator.Verdict([], []));
    }

    [Fact]
    public void 逐核表依事件數由多到少排列()
    {
        var s = Samples(("a.sys", 0, 10), ("b.sys", 3, 50), ("c.sys", 7, 30));
        var rows = InterruptAffinityAggregator.ByCpu(s);

        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows[0].Cpu);
        Assert.Equal(50, rows[0].Count);
        Assert.Equal(7, rows[1].Cpu);
        Assert.Equal(0, rows[2].Cpu);
    }

    [Fact]
    public void 每一顆核要說出它上面最多的是哪一支驅動()
    {
        var s = Samples(("net.sys", 5, 80), ("disk.sys", 5, 20));
        var row = InterruptAffinityAggregator.ByCpu(s).Single();

        Assert.Equal("net.sys", row.TopModule);
        Assert.Equal(80.0, row.TopSharePercent, 3);
        Assert.Contains("net.sys", row.Text);
        Assert.Contains("80", row.Text);
    }

    [Fact]
    public void 只在一顆核上發生的驅動要說它集中在單一核心()
    {
        var s = Samples(("net.sys", 5, 100));
        var row = InterruptAffinityAggregator.ByModule(s).Single();

        Assert.Equal(5, row.TopCpu);
        Assert.Equal(100.0, row.TopSharePercent, 3);
        Assert.Contains("集中", row.SpreadText);
        Assert.Contains("CPU 5", row.SpreadText);
    }

    [Fact]
    public void 平均分散在多顆核上的驅動不要說它集中()
    {
        var s = Samples(("x.sys", 0, 25), ("x.sys", 1, 25), ("x.sys", 2, 25), ("x.sys", 3, 25));
        var row = InterruptAffinityAggregator.ByModule(s).Single();

        Assert.Equal(25.0, row.TopSharePercent, 3);
        Assert.Contains("分散", row.SpreadText);
        Assert.Contains("4", row.SpreadText);      // 幾顆核要講出來
        Assert.DoesNotContain("集中", row.SpreadText);
    }

    [Fact]
    public void 判決要指出被打爆的那顆核與它的主要來源()
    {
        // core 9 拿下 700／1000＝七成，且幾乎全來自 nvlddmkm
        var s = Samples(("nvlddmkm.sys", 9, 690), ("other.sys", 9, 10),
                        ("a.sys", 1, 100), ("b.sys", 2, 100), ("c.sys", 3, 100));
        string v = InterruptAffinityAggregator.Verdict(
            InterruptAffinityAggregator.ByCpu(s), InterruptAffinityAggregator.ByModule(s));

        Assert.Contains("CPU 9", v);
        Assert.Contains("nvlddmkm.sys", v);
        Assert.Contains("70", v);
    }

    [Fact]
    public void 分佈平均時判決不要編出一顆有問題的核()
    {
        var s = Samples(("a.sys", 0, 100), ("b.sys", 1, 100), ("c.sys", 2, 100), ("d.sys", 3, 100));
        string v = InterruptAffinityAggregator.Verdict(
            InterruptAffinityAggregator.ByCpu(s), InterruptAffinityAggregator.ByModule(s));

        Assert.Contains("平均", v);
        Assert.DoesNotContain("打爆", v);
    }

    [Fact]
    public void 驅動表依事件數排序且只留前幾名()
    {
        var spec = new List<(string, int, int)>();
        for (int i = 0; i < 30; i++) spec.Add(($"m{i:00}.sys", i % 4, 30 - i));
        var rows = InterruptAffinityAggregator.ByModule(Samples(spec.ToArray()), top: 10);

        Assert.Equal(10, rows.Count);
        Assert.Equal("m00.sys", rows[0].Module);
        Assert.True(rows[0].Count >= rows[^1].Count);
    }
}
