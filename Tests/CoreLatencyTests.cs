using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>核心到核心延遲矩陣的純函式：遮罩展開、中位數、矩陣統計。</summary>
public class CoreLatencyTests
{
    [Fact]
    public void 親和性遮罩可展開為由低到高的邏輯處理器編號()
    {
        var lps = CoreLatencyService.LogicalProcessorsFromMask(0b1010_0101UL);
        Assert.Equal(new[] { 0, 2, 5, 7 }, lps);
    }

    [Fact]
    public void 全滿遮罩展開為連續編號()
    {
        var lps = CoreLatencyService.LogicalProcessorsFromMask(0xFFFFUL);
        Assert.Equal(16, lps.Count);
        Assert.Equal(0, lps[0]);
        Assert.Equal(15, lps[^1]);
    }

    [Fact]
    public void 中位數_奇數取正中()
    {
        Assert.Equal(3.0, CoreLatencyService.Median(new[] { 5.0, 1.0, 3.0 }));
    }

    [Fact]
    public void 中位數_偶數取兩中間值平均()
    {
        Assert.Equal(2.5, CoreLatencyService.Median(new[] { 1.0, 2.0, 3.0, 100.0 }));
    }

    [Fact]
    public void 中位數_空集合丟出例外()
    {
        Assert.Throws<ArgumentException>(() => CoreLatencyService.Median(Array.Empty<double>()));
    }

    [Fact]
    public void 矩陣統計忽略對角線與非有限值()
    {
        var m = new double[3, 3];
        for (int i = 0; i < 3; i++) m[i, i] = double.NaN;      // 對角線
        m[0, 1] = 40; m[0, 2] = 120; m[1, 0] = 42;
        m[1, 2] = double.PositiveInfinity;                      // 非有限值不計
        m[2, 0] = 130; m[2, 1] = 60;

        var (min, med, max) = CoreLatencyService.Stats(m);
        Assert.Equal(40, min);
        Assert.Equal(60, med);
        Assert.Equal(130, max);
    }

    [Fact]
    public void 矩陣統計_全空回傳非有限值()
    {
        var m = new double[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                m[i, j] = double.NaN;   // 全部非有限值（未量到）
        var (min, med, max) = CoreLatencyService.Stats(m);
        Assert.True(double.IsNaN(min) && double.IsNaN(med) && double.IsNaN(max));
    }
}
