using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP38 訊號層的純數學契約：loopback 樣本 → 峰值／RMS／削波偵測。
/// 金標：±0.5 全波振盪的 RMS＝0.5/√2；全 1（滿格）＝峰值 1 且 RMS 1；
/// 全 0 是「靜音」——是有效資料不是讀取失敗（三態的 Present）。
/// </summary>
public class LoopbackSignalTests
{
    [Fact]
    public void 訊號統計_正弦與滿格與靜音的金標()
    {
        // ±0.5 交替波（方波）：峰值 0.5、RMS 0.5（每個樣本絕對值都是 0.5）
        var square = Enumerable.Range(0, 480).Select(i => i % 2 == 0 ? 0.5f : -0.5f).ToArray();
        var s1 = XinSpect.LoopbackSignalService.Compute(square);
        Assert.Equal(0.5, s1.Peak, 5);
        Assert.Equal(0.5, s1.Rms, 5);
        Assert.False(s1.Clipping);

        var full = Enumerable.Repeat(1.0f, 100).ToArray();
        var s2 = XinSpect.LoopbackSignalService.Compute(full);
        Assert.Equal(1.0, s2.Peak, 5);
        Assert.Equal(1.0, s2.Rms, 5);
        Assert.True(s2.Clipping);                       // 滿格連續＝削波

        var silence = XinSpect.LoopbackSignalService.Compute(new float[480]);
        Assert.Equal(0.0, silence.Peak, 5);
        Assert.False(silence.Clipping);
    }

    [Fact]
    public void 訊號統計_空樣本如實拒算()
        => Assert.Throws<ArgumentException>(() => XinSpect.LoopbackSignalService.Compute([]));

    [Fact]
    public void dBFS換算_靜音負無窮_滿格零()
    {
        Assert.Equal(-120.0, XinSpect.LoopbackSignalService.ToDbfs(0.0), 3);      // −∞ 以 −120 dBFS 底線呈現
        Assert.Equal(0.0, XinSpect.LoopbackSignalService.ToDbfs(1.0), 5);
        Assert.Equal(-6.0206, XinSpect.LoopbackSignalService.ToDbfs(0.5), 3);
    }
}
