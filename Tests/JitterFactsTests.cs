using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 本機抖動量測（TU-008）單元測試。
/// 重點是白名單政策（未指定＝不出網）與「樣本太少 p95 沒意義」要在值裡看得見。
/// </summary>
public class JitterFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(
        (string Target, int Samples, double P50Ms, double P95Ms, double JitterMs, double LossRate)? s)
        => DeepBenchSecondWaveFactsService.JitterFact(At, () => s);

    [Fact]
    public void 未指定目標是不適用不是零抖動()
    {
        var f = Fact(null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        // 紅線：0 代表量到很穩，與「沒量」完全不同。
        Assert.Contains("與「沒量」是完全不同的兩件事", f.UnavailableReason);
        Assert.Contains("未指定＝絕不出網", f.UnavailableReason);
        Assert.Equal("", f.Value);
        Assert.Null(f.NumericValue);
    }

    [Fact]
    public void 探針擲例外也不出網並標不適用()
    {
        var f = DeepBenchSecondWaveFactsService.JitterFact(At,
            () => throw new InvalidOperationException("x"));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 陳述p50p95抖動與損失()
    {
        var f = Fact(("example.invalid", 100, 18.4, 42.7, 3.25, 0.004));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("p50 18.4 ms", f.Value);
        Assert.Contains("p95 42.7 ms", f.Value);
        Assert.Contains("抖動 3.3 ms", f.Value);
        Assert.Contains("損失 0.4%", f.Value);
        Assert.Equal(3.25d, f.NumericValue);
    }

    [Fact]
    public void 目標名稱出現在值裡因為是使用者自己指定的()
    {
        var f = Fact(("example.invalid", 100, 1, 2, 0.5, 0));
        Assert.Contains("目標「example.invalid」", f.Value);
    }

    [Fact]
    public void 樣本太少時標明統計意義有限()
    {
        var f = Fact(("example.invalid", 5, 10, 20, 2, 0));
        Assert.Contains("樣本僅 5 個", f.Value);
        Assert.Contains("統計意義有限", f.Value);
    }

    [Fact]
    public void 樣本足夠時不囉嗦()
    {
        var f = Fact(("example.invalid", DeepBenchSecondWaveFactsService.MinJitterSamples, 10, 20, 2, 0));
        Assert.Contains($"樣本 {DeepBenchSecondWaveFactsService.MinJitterSamples} 個", f.Value);
        Assert.DoesNotContain("統計意義有限", f.Value);
    }

    [Fact]
    public void 門檻前一顆樣本仍算不足()
    {
        var f = Fact(("t", DeepBenchSecondWaveFactsService.MinJitterSamples - 1, 1, 2, 0.5, 0));
        Assert.Contains("低於 20", f.Value);
    }

    [Fact]
    public void 聲明不判斷網路品質()
    {
        var f = Fact(("t", 100, 10, 20, 2, 0));
        Assert.Contains("抖動不是本機效能", f.Value);
        Assert.Contains("不寫「網路品質良好」這類結論", f.Value);
    }

    [Fact]
    public void 說明為何看p95不只平均()
    {
        var f = Fact(("t", 100, 10, 20, 2, 0));
        Assert.Contains("能不能用取決於尾端與掉包", f.Value);
        Assert.Contains("平均值看起來很漂亮", f.Value);
    }

    [Fact]
    public void 抖動定義寫明以利跨工具比較()
    {
        var f = Fact(("t", 100, 10, 20, 2, 0));
        Assert.Contains("相鄰樣本差的平均絕對值", f.Value);
        Assert.Contains("跨工具比數字會得出錯的結論", f.Value);
    }

    [Fact]
    public void 與ms_ping同為白名單政策()
    {
        var f = Fact(("t", 100, 10, 20, 2, 0));
        Assert.Contains("與 ms.ping 同一套白名單政策", f.Value);
        Assert.Contains("不自動掃描、不預設目標", f.Value);
    }

    [Fact]
    public void 信任度是Measured單位是毫秒()
    {
        var f = Fact(("t", 100, 10, 20, 2, 0));
        Assert.Equal(FactTrustLevel.Measured, f.Trust);
        Assert.Equal("ms", f.Unit);
    }
}
