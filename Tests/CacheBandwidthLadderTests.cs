using System.Diagnostics;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 快取帶寬階梯（U10-TS-0015／0016／0017）的鍵形與誠實界線。
/// 合成樣本注入，<b>不跑真實負載</b>——真機有沒有值不是測試的事。
/// </summary>
public class CacheBandwidthLadderTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 家族前綴是ts點cache點bw點且不含Key字樣()
    {
        Assert.Equal("ts.cache.bw.", CacheBandwidthLadderService.LevelPrefix);
        Assert.DoesNotContain("Key", CacheBandwidthLadderService.LevelPrefix);
    }

    [Theory]
    [InlineData("l1", "ts.cache.bw.l1")]
    [InlineData("l2", "ts.cache.bw.l2")]
    [InlineData("l3", "ts.cache.bw.l3")]
    [InlineData("L1", "ts.cache.bw.l1")]   // 大小寫正規化
    public void 鍵形是層級尾碼(string level, string expected)
    {
        var f = CacheBandwidthLadderService.BuildFact(At,
            new(level, WorkingSetBytes: 32 * 1024, BytesRead: 16_000_000_000, Ticks: 10_000_000, Truncated: false));
        Assert.Equal(expected, f.Key);
    }

    [Fact]
    public void GBs由位元組除秒數換算_Measured_帶數值()
    {
        // 16e9 位元組 ÷ 1 秒（Ticks = Stopwatch.Frequency，與真機 QPC 頻率無關）＝ 16 GB/s
        var f = CacheBandwidthLadderService.BuildFact(At,
            new("l1", 32 * 1024, 16_000_000_000, Stopwatch.Frequency, Truncated: false));
        Assert.Equal(FactTrustLevel.Measured, f.Trust);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(16.0, (double)f.NumericValue!, 6);
        Assert.Contains("工作集 32 KB", f.Value);
    }

    [Fact]
    public void 量的是工作集內帶寬_DRAM層級聲明不重測()
    {
        var f = CacheBandwidthLadderService.BuildFact(At,
            new("l3", 16 * 1024 * 1024, 16_000_000_000, 10_000_000, Truncated: false));
        Assert.Contains("memory.stream-bandwidth", f.Value);
        Assert.Contains("不重測 DRAM", f.Value);
    }

    [Fact]
    public void 截斷窗在值裡標註而不靜默()
    {
        var f = CacheBandwidthLadderService.BuildFact(At,
            new("l2", 512 * 1024, 16_000_000_000, 10_000_000, Truncated: true));
        Assert.Contains($"達 {CacheBandwidthLadderService.PerLevelBudgetMs} ms 預算即停", f.Value);
    }

    [Fact]
    public void 非正值樣本不推算_回ReadError且數值收斂成null()
    {
        foreach (var bad in new[]
        {
            new CacheBandwidthLadderService.LevelSample("l1", 0, 1_000, 100, false),
            new CacheBandwidthLadderService.LevelSample("l1", 32 * 1024, 0, 100, false),
            new CacheBandwidthLadderService.LevelSample("l1", 32 * 1024, 1_000, 0, false),
        })
        {
            var f = CacheBandwidthLadderService.BuildFact(At, bad);
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Null(f.NumericValue);
        }
    }

    [Fact]
    public void 預設階梯是三層_不含DRAM層()
    {
        Assert.Equal(["l1", "l2", "l3"],
            CacheBandwidthLadderService.DefaultLevels.Select(l => l.Level));
    }

    [Fact]
    public void 注入探針時每樣本一把事實_不跑真實負載()
    {
        var facts = CacheBandwidthLadderService.Collect(At, () => new[]
        {
            new CacheBandwidthLadderService.LevelSample("l1", 32 * 1024, 100_000_000_000, 10_000_000, false),
            new CacheBandwidthLadderService.LevelSample("l2", 512 * 1024, 20_000_000_000, 10_000_000, false),
            new CacheBandwidthLadderService.LevelSample("l3", 16L * 1024 * 1024, 5_000_000_000, 10_000_000, false),
        });
        Assert.Equal(3, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactTrustLevel.Measured, f.Trust));
        Assert.Equal(["ts.cache.bw.l1", "ts.cache.bw.l2", "ts.cache.bw.l3"], facts.Select(f => f.Key));
        // 階梯語意：注入的樣本刻意讓 L1 > L2 > L3，數值應照實反映（不排序、不修飾）
        Assert.True((double)facts[0].NumericValue! > (double)facts[1].NumericValue!);
        Assert.True((double)facts[1].NumericValue! > (double)facts[2].NumericValue!);
    }

    [Fact]
    public void 探針拋例外時整體收斂成ReadError而非崩潰()
    {
        var facts = CacheBandwidthLadderService.Collect(At, () => throw new InvalidOperationException("boom"));
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("boom", f.UnavailableReason);
    }
}
