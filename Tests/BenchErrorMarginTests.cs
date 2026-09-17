using System.IO;
using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 誤差值計算：標準差、變異係數、95% 信賴區間、可信度等級。
/// </summary>
/// <remarks>
/// 跑分的數字精確到個位數，但波動量級是百分比級——不附帶誤差範圍的分數
/// 會讓人以為它比實際精確得多。這裡確保計算正確、邊界情境不炸。
/// </remarks>
public class BenchErrorMarginTests
{
    [Fact]
    public void Null_ReturnsEmpty()
    {
        Assert.Equal("", BenchErrorMargin.FormatMargin(null!));
        Assert.Equal("", BenchErrorMargin.FormatConfidence(null!));
        Assert.Equal("", BenchErrorMargin.FormatConfidenceInterval(null!));
    }

    [Fact]
    public void SingleValue_ReturnsEmpty()
    {
        var one = new double[] { 42.0 };
        Assert.Equal("", BenchErrorMargin.FormatMargin(one));
        Assert.Equal("", BenchErrorMargin.FormatConfidence(one));
        Assert.Equal("", BenchErrorMargin.FormatConfidenceInterval(one));
    }

    [Fact]
    public void TwoIdenticalValues_ZeroSpread()
    {
        var vals = new double[] { 100, 100 };
        Assert.Equal("±0.0%", BenchErrorMargin.FormatMargin(vals));
        Assert.Equal("高", BenchErrorMargin.FormatConfidence(vals));
    }

    [Fact]
    public void TightValues_HighConfidence()
    {
        // CV < 2%
        var vals = new double[] { 1000, 1010, 1005, 1002, 1008 };
        string conf = BenchErrorMargin.FormatConfidence(vals);
        Assert.Equal("高", conf);
    }

    [Fact]
    public void ModerateValues_MediumConfidence()
    {
        // CV around 3-5%
        var vals = new double[] { 1000, 1050, 960, 1020, 980 };
        string conf = BenchErrorMargin.FormatConfidence(vals);
        Assert.Equal("中", conf);
    }

    [Fact]
    public void WideValues_LowConfidence()
    {
        // CV > 8%
        var vals = new double[] { 1000, 1200, 800, 1100, 900 };
        string conf = BenchErrorMargin.FormatConfidence(vals);
        Assert.Equal("低", conf);
    }

    [Fact]
    public void FormatMargin_PercentMode_ForNonTimeUnit()
    {
        var vals = new double[] { 1000, 1020, 1010 };
        string margin = BenchErrorMargin.FormatMargin(vals, "分");
        Assert.StartsWith("±", margin);
        Assert.EndsWith("%", margin);
    }

    [Fact]
    public void FormatMargin_AbsoluteMode_ForTimeUnit()
    {
        var vals = new double[] { 5.100, 5.200, 5.150 };
        string margin = BenchErrorMargin.FormatMargin(vals, "秒", "0.000");
        Assert.StartsWith("±", margin);
        Assert.Contains("秒", margin);
        Assert.DoesNotContain("%", margin);
    }

    [Fact]
    public void CvPercent_CalculatesCorrectly()
    {
        // mean = 100, sd = sqrt(((10^2+10^2)/1)) = ~14.14 (sample sd for n=2)
        // Actually for [90, 110]: mean=100, sample sd = sqrt((100+100)/1) = sqrt(200) = 14.142
        // CV = 14.142/100*100 = 14.142%
        var vals = new double[] { 90, 110 };
        double cv = BenchErrorMargin.CvPercent(vals);
        Assert.InRange(cv, 14.0, 14.3);
    }

    [Fact]
    public void Mean_Correct()
    {
        var vals = new double[] { 10, 20, 30 };
        Assert.Equal(20, BenchErrorMargin.Mean(vals));
    }

    [Fact]
    public void StdDev_SampleStdDev()
    {
        // [2, 4, 6]: mean=4, ss=(4+0+4)=8, sample var=8/2=4, sd=2
        var vals = new double[] { 2, 4, 6 };
        Assert.Equal(2.0, BenchErrorMargin.StdDev(vals, 4.0), 6);
    }

    [Fact]
    public void FormatConfidenceInterval_ContainsRange()
    {
        var vals = new double[] { 100, 102, 98, 101, 99 };
        string ci = BenchErrorMargin.FormatConfidenceInterval(vals, "#,0", "分");
        Assert.Contains("95%", ci);
        Assert.Contains("信賴區間", ci);
        Assert.Contains("分", ci);
        Assert.Contains("–", ci);   // en-dash in the range
    }

    [Fact]
    public void ZeroMean_ReturnsEmptyNotCrash()
    {
        var vals = new double[] { 0, 0, 0 };
        Assert.Equal("", BenchErrorMargin.FormatMargin(vals));
        Assert.Equal("", BenchErrorMargin.FormatConfidence(vals));
    }

    [Fact]
    public void BenchLog_Scores_FiltersByKindAndConfig()
    {
        string dir = Path.Combine(Path.GetTempPath(), "XinSpectTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new BenchLog(dir);
            log.Add(new BenchRun { Kind = "bench.composite", Config = "30 秒", Score = 1000, Unit = "分" });
            log.Add(new BenchRun { Kind = "bench.composite", Config = "30 秒", Score = 1100, Unit = "分" });
            log.Add(new BenchRun { Kind = "bench.composite", Config = "60 秒", Score = 5000, Unit = "分" });
            log.Add(new BenchRun { Kind = "superpi", Config = "30 秒", Score = 8.5, Unit = "秒" });

            var scores = log.Scores("bench.composite", "30 秒");
            Assert.Equal(2, scores.Count);
            Assert.Equal(1000, scores[0]);
            Assert.Equal(1100, scores[1]);

            Assert.Single(log.Scores("bench.composite", "60 秒"));
            Assert.Empty(log.Scores("bench.composite", "90 秒"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
