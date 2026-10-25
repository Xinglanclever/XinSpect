using System.Diagnostics;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// memcpy／memset 帶寬（U10-TS-0018／0019）的鍵形與誠實界線。
/// 合成樣本注入，<b>不跑真實負載</b>——真機有沒有值不是測試的事。
/// </summary>
public class CopyKernelBandwidthTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 家族前綴是ts點copy點kernel點且不含Key字樣()
    {
        Assert.Equal("ts.copy.kernel.", CopyKernelBandwidthService.KernelPrefix);
        Assert.DoesNotContain("Key", CopyKernelBandwidthService.KernelPrefix);
    }

    [Fact]
    public void 核與實作的形狀是二核三實作()
    {
        Assert.Equal(["memcpy", "memset"], CopyKernelBandwidthService.Kernels);
        Assert.Equal(["crt", "simd", "byte"], CopyKernelBandwidthService.Implementations);
    }

    [Theory]
    [InlineData("memcpy", "crt", "ts.copy.kernel.memcpy.crt")]
    [InlineData("memcpy", "simd", "ts.copy.kernel.memcpy.simd")]
    [InlineData("memcpy", "byte", "ts.copy.kernel.memcpy.byte")]
    [InlineData("memset", "crt", "ts.copy.kernel.memset.crt")]
    [InlineData("memset", "simd", "ts.copy.kernel.memset.simd")]
    [InlineData("memset", "byte", "ts.copy.kernel.memset.byte")]
    public void 鍵形是核點實作(string kernel, string impl, string expected)
    {
        var f = CopyKernelBandwidthService.BuildFact(At,
            new(kernel, impl, Bytes: 16_000_000_000, Ticks: 10_000_000, Truncated: false));
        Assert.Equal(expected, f.Key);
    }

    [Fact]
    public void GBs由位元組除秒數換算_Measured_帶數值()
    {
        var f = CopyKernelBandwidthService.BuildFact(At,
            new("memcpy", "crt", 16_000_000_000, Stopwatch.Frequency, Truncated: false));
        Assert.Equal(FactTrustLevel.Measured, f.Trust);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(16.0, (double)f.NumericValue!, 6);
        Assert.Contains("16 MiB", f.Value);
    }

    [Fact]
    public void CRT路徑照實說明_不自行判定微碼選路()
    {
        var crt = CopyKernelBandwidthService.BuildFact(At,
            new("memcpy", "crt", 16_000_000_000, 10_000_000, Truncated: false));
        Assert.Contains("REP MOVSB", crt.Value);
        Assert.Contains("不自行判定", crt.Value);
    }

    [Fact]
    public void 非正值樣本不推算_回ReadError且數值收斂成null()
    {
        foreach (var bad in new[]
        {
            new CopyKernelBandwidthService.KernelSample("memcpy", "crt", 0, 100, false),
            new CopyKernelBandwidthService.KernelSample("memcpy", "crt", 1_000, 0, false),
        })
        {
            var f = CopyKernelBandwidthService.BuildFact(At, bad);
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Null(f.NumericValue);
        }
    }

    [Fact]
    public void 注入探針時每樣本一把事實_鍵不重複()
    {
        var facts = CopyKernelBandwidthService.Collect(At, () => (
            from k in CopyKernelBandwidthService.Kernels
            from i in CopyKernelBandwidthService.Implementations
            select new CopyKernelBandwidthService.KernelSample(k, i, 16_000_000_000, 10_000_000, false)).ToList());
        Assert.Equal(6, facts.Count);
        Assert.Equal(facts.Count, facts.Select(f => f.Key).Distinct().Count());
        Assert.All(facts, f => Assert.Equal(FactTrustLevel.Measured, f.Trust));
    }

    [Fact]
    public void 探針拋例外時整體收斂成ReadError而非崩潰()
    {
        var facts = CopyKernelBandwidthService.Collect(At, () => throw new InvalidOperationException("boom"));
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("boom", f.UnavailableReason);
    }
}
