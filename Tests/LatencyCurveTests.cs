using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>記憶體延遲曲線的純函式數學：點位規劃、邊界推導、與 CPUID 宣稱值的配對。</summary>
public class LatencyCurveTests
{
    [Fact]
    public void 點位規劃_從1KB半倍頻步進到上限()
    {
        var sizes = LatencyCurveMath.BuildSizes(256 * 1024);
        Assert.Equal(1024, sizes[0]);                       // 起點 1 KB
        Assert.True(sizes[^1] <= 256 * 1024);               // 不超過上限
        Assert.True(sizes[^1] >= 128 * 1024);               // 貼著上限收尾
        for (int i = 1; i < sizes.Length; i++)
            Assert.True(sizes[i] > sizes[i - 1]);           // 嚴格遞增
    }

    [Fact]
    public void 點位規劃_上限低於1KB仍有單點()
    {
        var sizes = LatencyCurveMath.BuildSizes(512);
        Assert.Single(sizes);
    }

    [Fact]
    public void 邊界推導_平台後階梯浮現一個邊界()
    {
        // 1 KB → 32 KB 平台（延遲 2 ns），之後平台（延遲 8 ns）→ 應在 32 KB 附近推導出單一邊界
        var sizes = LatencyCurveMath.BuildSizes(1024 * 1024);
        var lat = sizes.Select(s => s <= 32 * 1024 ? 2.0 : 8.0).ToArray();
        var b = LatencyCurveMath.DeriveBoundaries(sizes, lat);
        Assert.NotEmpty(b);
        Assert.True(b[0] >= 32 * 1024 && b[0] <= 64 * 1024, $"邊界 {b[0]} 應落在 32–64 KB");
        Assert.All(b, x => Assert.True(x < 128 * 1024));    // 之後都是同一平台，不再出新邊界
    }

    [Fact]
    public void 邊界推導_平緩曲線不產生邊界()
    {
        var sizes = LatencyCurveMath.BuildSizes(1024 * 1024);
        var lat = sizes.Select((_, i) => 2.0 + i * 0.01).ToArray();   // 極緩慢線性
        var b = LatencyCurveMath.DeriveBoundaries(sizes, lat);
        Assert.Empty(b);
    }

    [Fact]
    public void 配對_邊界找最近的宣稱值且不重複使用()
    {
        double[] boundaries = { 38_000, 940_000, 30_000_000 };
        double[] claimed = { 32_000, 1_000_000, 25_000_000 };
        var map = LatencyCurveMath.PairNearest(boundaries, claimed);
        Assert.Equal(0, map[0]);    // 38K → 32K
        Assert.Equal(1, map[1]);    // 940K → 1M
        Assert.Equal(2, map[2]);    // 30M → 25M
    }

    [Fact]
    public void 配對_超出倍率者不強配()
    {
        double[] boundaries = { 38_000 };
        double[] claimed = { 25_000_000 };
        var map = LatencyCurveMath.PairNearest(boundaries, claimed);
        Assert.Equal(-1, map[0]);   // 差了三個數量級，不該硬湊
    }

    [Fact]
    public void 偏差評估_偏差小於20Percent判定為高可信()
    {
        double[] boundaries = { 38_000 };
        int[] map = { 0 };
        double[] claimed = { 32_000 };
        var rows = LatencyCurveMath.AssessDeviation(boundaries, map, claimed);
        var r = Assert.Single(rows);
        Assert.Equal(DeviationConfidence.High, r.Confidence);
        Assert.True(r.DeviationPct > 0 && r.DeviationPct < 20);
    }

    [Fact]
    public void 偏差評估_偏差在20至50Percent判定為中可信()
    {
        double[] boundaries = { 45_000 };
        int[] map = { 0 };
        double[] claimed = { 32_000 };
        var rows = LatencyCurveMath.AssessDeviation(boundaries, map, claimed);
        var r = Assert.Single(rows);
        Assert.Equal(DeviationConfidence.Medium, r.Confidence);
    }

    [Fact]
    public void 偏差評估_偏差大於50Percent判定為低可信()
    {
        double[] boundaries = { 80_000 };
        int[] map = { 0 };
        double[] claimed = { 32_000 };
        var rows = LatencyCurveMath.AssessDeviation(boundaries, map, claimed);
        var r = Assert.Single(rows);
        Assert.Equal(DeviationConfidence.Low, r.Confidence);
    }

    [Fact]
    public void 偏差評估_未配對的邊界不列入評估()
    {
        double[] boundaries = { 38_000, 90_000 };
        int[] map = { 0, -1 };   // 第二個邊界未配對
        double[] claimed = { 32_000 };
        var rows = LatencyCurveMath.AssessDeviation(boundaries, map, claimed);
        Assert.Single(rows);
    }

    [Fact]
    public void 偏差評估_無宣稱值時不評估()
    {
        double[] boundaries = { 38_000 };
        int[] map = { 0 };
        double[] claimed = { 0 };
        Assert.Empty(LatencyCurveMath.AssessDeviation(boundaries, map, claimed));
    }
}
