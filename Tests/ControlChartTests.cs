using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 控制圖（U10-VZ-0105／<c>viz.controlchart</c>）的<b>數學守門</b>：
/// 中心線、σ、控制限與越界判定全是純函式，用合成資料直接驗證，不開 UI。
/// 「圖上標明統計方法、不把 ±kσ 說成規格判定」由方法標示字串釘住。
/// </summary>
public class ControlChartTests
{
    [Fact]
    public void 追蹤鍵_以U10目錄正式鍵為準()
        => Assert.Equal("viz.controlchart", VizTrackingKeys.ControlChartFactKey);

    // ── 中心線 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 中位數_奇數取中間_偶數取中間兩數平均()
    {
        Assert.Equal(3, SpcMath.Median([5, 1, 3]), 9);              // 不必預排序
        Assert.Equal(2.5, SpcMath.Median([1, 2, 3, 4]), 9);
    }

    [Fact]
    public void 中位數_空序列_如實拒絕_不回0()
        => Assert.Throws<ArgumentException>(() => SpcMath.Median([]));

    [Fact]
    public void 均值_與中位數在對稱序列上一致_在尖峰序列上分家()
    {
        var symmetric = new double[] { 10, 20, 30, 40 };
        Assert.Equal(SpcMath.Mean(symmetric), SpcMath.Median(symmetric), 9);

        var spike = Enumerable.Repeat(50.0, 20).Append(1000).ToArray();
        Assert.Equal(50, SpcMath.Median(spike), 9);                 // 中位數穩健：尖峰拖不走
        Assert.True(SpcMath.Mean(spike) > SpcMath.Median(spike));   // 均值被尖峰拖走——差異本身是選擇依據
    }

    // ── σ 與控制限 ──────────────────────────────────────────────────────────

    [Fact]
    public void 樣本標準差_n減1分母_與教科書值一致()
    {
        // 2,4,4,4,5,5,7,9：母體 σ＝2，樣本 σ＝sqrt(32/7)≈2.138
        Assert.Equal(2.138089935299395, SpcMath.Sigma([2, 4, 4, 4, 5, 5, 7, 9]), 9);
    }

    [Fact]
    public void 樣本標準差_單點或全段等值_回0_控制限退化成中心線()
    {
        Assert.Equal(0, SpcMath.Sigma([42]), 9);
        Assert.Equal(0, SpcMath.Sigma(Enumerable.Repeat(42.0, 9).ToArray()), 9);

        var lim = SpcMath.Compute(Enumerable.Repeat(42.0, 9).ToArray(), SpcCenter.Median);
        Assert.Equal(lim.Center, lim.Lower, 9);
        Assert.Equal(lim.Center, lim.Upper, 9);
        Assert.Contains("退化成中心線", lim.MethodLabel);
    }

    [Fact]
    public void 控制限_中位數模式_中心線是中位數且對稱()
    {
        var series = Enumerable.Range(0, 100).Select(i => (double)i).ToArray();   // 0..99
        var lim = SpcMath.Compute(series, SpcCenter.Median, 3);

        Assert.Equal(49.5, lim.Center, 9);
        Assert.Equal(lim.Upper - lim.Center, lim.Center - lim.Lower, 9);   // 上下半寬對稱
        Assert.Contains("中位數", lim.MethodLabel);
        Assert.Contains("±3σ", lim.MethodLabel);
    }

    [Fact]
    public void 控制限_均值模式_方法標示說明用均值()
    {
        var lim = SpcMath.Compute([1, 2, 3, 4], SpcCenter.Mean, 2);
        Assert.Equal(2.5, lim.Center, 9);
        Assert.Contains("均值", lim.MethodLabel);
        Assert.Contains("±2σ", lim.MethodLabel);
    }

    [Fact]
    public void 控制限_空序列_如實拒絕()
        => Assert.Throws<ArgumentException>(() => SpcMath.Compute([], SpcCenter.Mean));

    // ── 越界判定 ────────────────────────────────────────────────────────────

    [Fact]
    public void 越界_尖峰被標記_恰好在限上不算越界()
    {
        // 20 點 50 附近＋一個 200 的尖峰：50±3σ 之外只有尖峰
        var series = Enumerable.Repeat(50.0, 20).Concat([200.0]).ToArray();
        var lim = SpcMath.Compute(series, SpcCenter.Median);

        var outliers = SpcMath.Outliers(series, lim);
        Assert.Equal([20], outliers);           // 只有最後一點（索引 20）

        // 恰好落在上限上的點＝在界內（Shewhart 慣例：嚴格出界才算）
        var onLimit = new double[] { 0, lim.Upper, lim.Lower, 0 };
        Assert.Empty(SpcMath.Outliers(onLimit, lim));
    }

    [Fact]
    public void 越界_退化限下無越界_不虛構警戒()
    {
        var series = Enumerable.Repeat(42.0, 10).ToArray();
        var lim = SpcMath.Compute(series, SpcCenter.Median);
        Assert.Empty(SpcMath.Outliers(series, lim));
    }
}
