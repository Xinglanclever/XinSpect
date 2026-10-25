using System.Diagnostics;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 向量寬度階梯吞吐（U10-TS-0003／0005／0006 吞吐面）的鍵形與誠實界線。
/// 合成樣本注入，<b>不跑真實負載</b>——真機有沒有值不是測試的事。
/// </summary>
public class VectorWidthThroughputTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 家族前綴是ts點vector點flops點且不含Key字樣()
    {
        Assert.Equal("ts.vector.flops.", VectorWidthThroughputService.FamilyPrefix);
        Assert.DoesNotContain("Key", VectorWidthThroughputService.FamilyPrefix);
    }

    [Theory]
    [InlineData("scalar", false, "ts.vector.flops.scalar.st")]
    [InlineData("scalar", true, "ts.vector.flops.scalar.mt")]
    [InlineData("128", false, "ts.vector.flops.128.st")]
    [InlineData("256", true, "ts.vector.flops.256.mt")]
    [InlineData("512", false, "ts.vector.flops.512.st")]
    public void 鍵形是寬度點相位(string width, bool mt, string expected)
    {
        var f = VectorWidthThroughputService.BuildFact(At,
            new(width, mt, Flops: 2_000_000_000, Ticks: 10_000_000, Truncated: false));
        Assert.Equal(expected, f.Key);
    }

    [Fact]
    public void GFLOPS由FLOP除秒數換算_Measured_帶數值()
    {
        // 2e9 FLOP ÷ 1 秒（Ticks = Stopwatch.Frequency，與真機 QPC 頻率無關）＝ 2 GFLOPS
        var f = VectorWidthThroughputService.BuildFact(At,
            new("256", false, 2_000_000_000, Stopwatch.Frequency, Truncated: false));
        Assert.Equal(FactTrustLevel.Measured, f.Trust);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(2.0, (double)f.NumericValue!, 6);
        Assert.Contains("乘與加各計 1 FLOP", f.Value);
    }

    [Fact]
    public void 截斷窗在值裡標註而不靜默()
    {
        var f = VectorWidthThroughputService.BuildFact(At,
            new("256", false, 2_000_000_000, 10_000_000, Truncated: true));
        Assert.Contains($"達 {VectorWidthThroughputService.PerPhaseBudgetMs} ms 預算即停", f.Value);
    }

    [Fact]
    public void 非正值樣本不推算_回ReadError且數值收斂成null()
    {
        foreach (var bad in new[]
        {
            new VectorWidthThroughputService.WidthSample("256", false, 0, 10_000_000, false),
            new VectorWidthThroughputService.WidthSample("256", false, 2_000_000_000, 0, false),
            new VectorWidthThroughputService.WidthSample("256", false, -1, 10_000_000, false),
        })
        {
            var f = VectorWidthThroughputService.BuildFact(At, bad);
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Null(f.NumericValue);
        }
    }

    [Fact]
    public void AVX512不支援時如實NotSupported_不降級冒充()
    {
        foreach (bool mt in new[] { false, true })
        {
            var f = VectorWidthThroughputService.BuildUnsupportedFact(At, mt);
            Assert.Equal($"ts.vector.flops.512.{(mt ? "mt" : "st")}", f.Key);
            Assert.Equal(FactAvailability.NotSupported, f.Availability);
            Assert.Null(f.NumericValue);
            Assert.Contains("不降級", f.UnavailableReason);
        }
    }

    [Fact]
    public void 注入探針時每樣本一把事實_不跑真實負載()
    {
        var facts = VectorWidthThroughputService.Collect(At, () => new[]
        {
            new VectorWidthThroughputService.WidthSample("scalar", false, 1_000_000_000L, 5_000_000, false),
            new VectorWidthThroughputService.WidthSample("scalar", true, 8_000_000_000L, 5_000_000, false),
            new VectorWidthThroughputService.WidthSample("256", false, 16_000_000_000L, 5_000_000, false),
        }, avx512Supported: false);
        Assert.Equal(3, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactTrustLevel.Measured, f.Trust));
        Assert.Equal(["ts.vector.flops.scalar.st", "ts.vector.flops.scalar.mt", "ts.vector.flops.256.st"],
            facts.Select(f => f.Key));
    }

    [Fact]
    public void 探針拋例外時整體收斂成ReadError而非崩潰()
    {
        var facts = VectorWidthThroughputService.Collect(At, () => throw new InvalidOperationException("boom"));
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("boom", f.UnavailableReason);
    }
}
