using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 卡點診斷引擎：純規則，餵資料袋就能驗，不需要任何硬體。
/// 這裡刻意連「沒量到的東西不能當成沒問題」一起測——那是這個引擎唯一不能退讓的性質。
/// </summary>
public class BottleneckAnalyzerTests
{
    [Fact]
    public void 全空的資料袋不會生出任何卡點()
    {
        var r = BottleneckAnalyzer.Analyze(new BottleneckFacts());

        Assert.False(r.HasFindings);
        Assert.Equal(Severity.Good, r.Severity);
        Assert.Contains("沒有明顯卡點", r.Headline);
        // 但必須明講這只是「沒踩到判斷線」，不是「這台機器沒有極限」
        Assert.Contains("不是", r.Detail);
        Assert.True(r.HasUnknowns);
    }

    [Fact]
    public void 正在降頻是最高優先()
    {
        var f = new BottleneckFacts { ThermalLogSeen = true, ThrottlingNow = true, CpuLoad = 100 };
        var r = BottleneckAnalyzer.Analyze(f);

        Assert.Equal(BottleneckKind.Thermal, r.Findings[0].Kind);
        Assert.Equal(Severity.Critical, r.Findings[0].Severity);
        Assert.Contains("最該先看", r.Findings[0].PriorityText);
        Assert.Contains("降頻", r.Headline);
    }

    [Fact]
    public void 黏滯位元不可信時不採用那三個位元()
    {
        var f = new BottleneckFacts { StickyUnreliable = true, ThermalLogSeen = true, ThrottlingNow = true };
        var r = BottleneckAnalyzer.Analyze(f);

        Assert.DoesNotContain(r.Findings, x => x.Kind == BottleneckKind.Thermal);
        Assert.Contains(r.Unknowns, u => u.Contains("可信"));
    }

    [Fact]
    public void 溫度門檻用使用者自己設的值()
    {
        var mine = new BottleneckFacts { CpuTempC = 82, CpuTempThreshold = 80 };
        Assert.Contains(BottleneckAnalyzer.Analyze(mine).Findings, x => x.Title.Contains("超過你設的門檻"));

        var loose = new BottleneckFacts { CpuTempC = 82, CpuTempThreshold = 100 };
        Assert.DoesNotContain(BottleneckAnalyzer.Analyze(loose).Findings, x => x.Title.Contains("超過你設的門檻"));
    }

    [Fact]
    public void 風扇被下令卻沒轉是嚴重問題()
    {
        var r = BottleneckAnalyzer.Analyze(new BottleneckFacts { FanCount = 3, FansCommandedButStopped = 1 });
        var hit = Assert.Single(r.Findings, x => x.Title.Contains("沒在轉"));
        Assert.Equal(Severity.Serious, hit.Severity);
    }

    [Fact]
    public void 一核滿載其餘閒著要單獨指出單執行緒瓶頸()
    {
        var f = new BottleneckFacts { CoreCount = 16, MaxCoreLoad = 99, MedianCoreLoad = 10 };
        var r = BottleneckAnalyzer.Analyze(f);

        Assert.Contains(r.Findings, x => x.Title.Contains("單一執行緒"));
        Assert.DoesNotContain(r.Findings, x => x.Title.Contains("全核都吃滿"));
    }

    [Fact]
    public void 全核吃滿與單執行緒不會同時成立()
    {
        var f = new BottleneckFacts { CoreCount = 8, MaxCoreLoad = 99, MedianCoreLoad = 95 };
        var r = BottleneckAnalyzer.Analyze(f);

        Assert.Contains(r.Findings, x => x.Title.Contains("全核都吃滿"));
        Assert.DoesNotContain(r.Findings, x => x.Title.Contains("單一執行緒"));
    }

    [Fact]
    public void 顯示卡滿載而處理器閒著算卡在顯示卡()
    {
        var r = BottleneckAnalyzer.Analyze(new BottleneckFacts { GpuLoad = 99, CpuLoad = 40 });
        Assert.Contains(r.Findings, x => x.Kind == BottleneckKind.Gpu && x.Title.Contains("顯示卡"));
    }

    [Fact]
    public void 兩者都滿載時不歸咎顯示卡()
    {
        var r = BottleneckAnalyzer.Analyze(new BottleneckFacts { GpuLoad = 99, CpuLoad = 95 });
        Assert.DoesNotContain(r.Findings, x => x.Title == "卡在顯示卡");
    }

    [Fact]
    public void 不可修正的MCA事件壓過其他一切()
    {
        var f = new BottleneckFacts { McaUncorrected = 1, McaCorrectedBanks = 3, CpuTempC = 95, CpuTempThreshold = 90 };
        var r = BottleneckAnalyzer.Analyze(f);

        Assert.Equal(BottleneckKind.Platform, r.Findings[0].Kind);
        Assert.Equal(Severity.Critical, r.Findings[0].Severity);
        // 有不可修正事件時就不用再提可修正計數，避免稀釋掉重點
        Assert.DoesNotContain(r.Findings, x => x.Title.Contains("已修正"));
    }

    [Fact]
    public void 電源政策只轉述服務自己標記的項目()
    {
        var f = new BottleneckFacts();
        f.PolicyFlags.Add(("處理器最大狀態", "50%"));
        var r = BottleneckAnalyzer.Analyze(f);

        var hit = Assert.Single(r.Findings, x => x.Kind == BottleneckKind.Policy);
        Assert.Contains("處理器最大狀態", hit.Evidence);
        Assert.Contains("50%", hit.Evidence);
    }

    [Fact]
    public void 沒有政策旗標就不生成政策卡點()
    {
        var r = BottleneckAnalyzer.Analyze(new BottleneckFacts { PowerPlanName = "平衡" });
        Assert.DoesNotContain(r.Findings, x => x.Kind == BottleneckKind.Policy);
    }

    [Fact]
    public void 每一條卡點都要有依據與做法()
    {
        var f = new BottleneckFacts
        {
            CpuTempC = 95, CpuTempThreshold = 90, MemLoad = 96, MemLoadThreshold = 92,
            MaxDpcPercent = 12, GpuLoad = 99, CpuLoad = 20, McaCorrectedBanks = 2,
            CoreCount = 8, MaxCoreLoad = 99, MedianCoreLoad = 5,
        };
        var r = BottleneckAnalyzer.Analyze(f);

        Assert.True(r.Findings.Count >= 5);
        Assert.All(r.Findings, x =>
        {
            Assert.NotEmpty(x.Title);
            Assert.NotEmpty(x.Evidence);
            Assert.NotEmpty(x.Advice);
            Assert.NotEmpty(x.KindText);
        });
    }

    [Fact]
    public void 卡點依分數由高到低排序()
    {
        var f = new BottleneckFacts
        {
            CpuTempC = 95, CpuTempThreshold = 90,     // 86 分
            MaxInterruptPercent = 12,                  // 66 分
            FanCount = 2, FansCommandedButStopped = 1, // 90 分
        };
        var r = BottleneckAnalyzer.Analyze(f);

        var scores = r.Findings.Select(x => x.Score).ToList();
        Assert.Equal(scores.OrderByDescending(x => x), scores);
    }

    [Fact]
    public void 沒讀到的資料源會被列進未納入判斷()
    {
        var r = BottleneckAnalyzer.Analyze(new BottleneckFacts());
        string all = string.Join("\n", r.Unknowns);

        Assert.Contains("溫度", all);
        Assert.Contains("DPC", all);
        Assert.True(r.Unknowns.Count >= 5);
    }

    [Fact]
    public void 資料越齊信心說明越正面()
    {
        string thin = BottleneckAnalyzer.Analyze(new BottleneckFacts()).Confidence;
        var rich = new BottleneckFacts
        {
            CpuLoad = 30, CpuTempC = 60, CpuPowerW = 45, MemLoad = 50, GpuLoad = 20,
            ThermalLogSeen = false, PowerLimitLogSeen = false, ThrottlingNow = false,
            MaxDpcPercent = 1, TdBackend = 20, CeilingHasVerdict = true, CeilingHeadline = "在標準內",
            McaUncorrected = 0, McaCorrectedBanks = 0, CommitGb = 12, HistoryMinutes = 240,
        };
        string full = BottleneckAnalyzer.Analyze(rich).Confidence;

        Assert.NotEqual(thin, full);
        Assert.NotEmpty(full);
    }

    [Theory]
    [InlineData(0, "不到 1 分鐘")]
    [InlineData(45, "45 分鐘")]
    [InlineData(60, "1 小時")]
    [InlineData(150, "2 小時 30 分鐘")]
    public void 時間長度講人話(int minutes, string text)
        => Assert.Equal(text, BottleneckAnalyzer.Span(minutes));
}

/// <summary>七段數字的段落遮罩：字形對不對，用「幾段亮著」與「彼此不重複」驗。</summary>
public class OutlineDigitsTests
{
    private static int Bits(int mask)
    {
        int n = 0;
        for (int i = 0; i < 8; i++) if ((mask & (1 << i)) != 0) n++;
        return n;
    }

    [Fact]
    public void 八是七段全亮()
    {
        Assert.Equal(7, Bits(OutlineDigits.MaskOf('8')));
        Assert.Equal(127, OutlineDigits.MaskOf('8'));
    }

    [Fact]
    public void 一只亮兩段()
        => Assert.Equal(2, Bits(OutlineDigits.MaskOf('1')));

    [Fact]
    public void 減號只亮中間一段()
        => Assert.Equal(1, Bits(OutlineDigits.MaskOf('-')));

    [Fact]
    public void 十個數字的字形彼此不同()
    {
        var masks = "0123456789".Select(OutlineDigits.MaskOf).ToList();
        Assert.Equal(10, masks.Distinct().Count());
    }

    [Fact]
    public void 未知字元整格熄燈而不是亂亮()
    {
        Assert.Equal(0, OutlineDigits.MaskOf('x'));
        Assert.Equal(0, OutlineDigits.MaskOf(' '));
        Assert.Equal(0, OutlineDigits.MaskOf('#'));
    }

    [Fact]
    public void 每個數字都至少亮兩段()
        => Assert.All("0123456789", c => Assert.True(Bits(OutlineDigits.MaskOf(c)) >= 2));
}
