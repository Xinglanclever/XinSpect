using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>幀時間統計純函式：間隔、百分位 Low、樣本不足的誠實回退。</summary>
public class FrameTimeTests
{
    [Fact]
    public void 間隔計算_相鄰時間戳相減轉毫秒()
    {
        var ts = new double[] { 0, 0.016, 0.033, 0.050 };
        var iv = FrameTimeStats.IntervalsMs(ts);
        Assert.Equal(3, iv.Length);
        Assert.Equal(16, iv[0], 1);
        Assert.Equal(17, iv[2], 0);
    }

    [Fact]
    public void 單幀不產生間隔()
    {
        Assert.Empty(FrameTimeStats.IntervalsMs(new double[] { 1.0 }));
    }

    [Fact]
    public void 穩定60FPS的平均與中位數()
    {
        var ts = Enumerable.Range(0, 101).Select(i => i / 60.0).ToList();   // 每幀 16.67ms
        var (avg, low1, low01, med, max) = FrameTimeStats.Compute(ts);
        Assert.NotNull(avg);
        Assert.Equal(60, avg!.Value, 1);
        Assert.Equal(16.67, med, 1);
        Assert.Equal(16.67, max, 1);
    }

    [Fact]
    public void 最差幀拉低百分位Low()
    {
        // 100 幀 60 FPS，中間塞 5 幀 30 FPS（33ms）→ 1% Low 應明顯低於 60
        var ts = new List<double>();
        double t = 0;
        for (int i = 0; i < 100; i++)
        {
            ts.Add(t);
            t += (i >= 50 && i < 55) ? 1.0 / 30 : 1.0 / 60;
        }
        var (_, low1, low01, _, _) = FrameTimeStats.Compute(ts);
        Assert.NotNull(low1);
        Assert.True(low1 < 55, $"1% Low 應被 30 FPS 幀拉低，實得 {low1}");
        Assert.True(low01 is null || low01 <= low1, "0.1% Low 不應高於 1% Low");
    }

    [Fact]
    public void 樣本不足時Low回null而不是硬掰()
    {
        var ts = Enumerable.Range(0, 5).Select(i => i / 60.0).ToList();   // 只有 4 個間隔
        var (_, low1, low01, _, _) = FrameTimeStats.Compute(ts);
        Assert.Null(low1);
        Assert.Null(low01);
    }
}

/// <summary>SLC 快取耗盡曲線的純函式：尖峰、斷崖偵測、後段中位。</summary>
public class SlcMathTests
{
    [Fact]
    public void 斷崖偵測_快取耗盡後速度崩落()
    {
        // 前 10 秒 3000 MB/s，第 11 秒起掉到 400 MB/s 並維持
        var times = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();
        var mbps = times.Select(t => t <= 10 ? 3000.0 : 400.0).ToArray();
        var (peak, peakSec, cliffSec, postMed) = SlcMath.Analyze(times, mbps);
        Assert.Equal(3000, peak);
        Assert.Equal(1, peakSec);   // 尖峰平台從第 1 秒開始（首個最大值）
        Assert.True(cliffSec > 10 && cliffSec <= 12, $"斷崖應在 10–12 秒，實得 {cliffSec}");
        Assert.Equal(400, postMed);
    }

    [Fact]
    public void 全程高速_不誤報斷崖()
    {
        var times = Enumerable.Range(1, 30).Select(i => (double)i).ToArray();
        var mbps = times.Select(t => 3000.0 - t).ToArray();   // 緩慢遞減但遠高於門檻
        var (_, _, cliffSec, _) = SlcMath.Analyze(times, mbps);
        Assert.Equal(-1, cliffSec);
    }

    [Fact]
    public void 單點雜訊不構成斷崖()
    {
        // 3000 平台，中間一個點掉到 300 但馬上回復 → 不是真斷崖
        var times = Enumerable.Range(1, 30).Select(i => (double)i).ToArray();
        var mbps = times.Select(t => 3000.0).ToArray();
        mbps[15] = 300;
        var (_, _, cliffSec, _) = SlcMath.Analyze(times, mbps);
        Assert.Equal(-1, cliffSec);
    }

    [Fact]
    public void 空曲線回傳安全值()
    {
        var (peak, _, cliff, post) = SlcMath.Analyze([], []);
        Assert.Equal(0, peak);
        Assert.Equal(-1, cliff);
        Assert.Equal(0, post);
    }
}
