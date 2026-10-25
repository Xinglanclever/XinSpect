using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// TSC 跨核同步（U10-TS-0024）的外推數學與誠實界線。
/// 合成取樣注入，<b>不真的跨核取樣</b>——真機有沒有值不是測試的事。
/// </summary>
public class TscCrossCoreSyncTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    private const double QpcHz = 10_000_000.0;   // 假的 QPC 頻率（0.1 µs/刻）

    [Fact]
    public void 家族前綴是ts點tsc點skew點core點且不含Key字樣()
    {
        Assert.Equal("ts.tsc.skew.core.", TscCrossCoreSyncService.SkewCorePrefix);
        Assert.DoesNotContain("Key", TscCrossCoreSyncService.SkewCorePrefix);
        Assert.Equal("ts.tsc.skew.max", TscCrossCoreSyncService.SkewMaxKey);
    }

    [Fact]
    public void 未提供讀取通路時如實NotSupported_不冒充量測()
    {
        var facts = TscCrossCoreSyncService.Collect(At);
        var f = Assert.Single(facts);
        Assert.Equal(TscCrossCoreSyncService.SkewMaxKey, f.Key);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("無 rdtsc", f.UnavailableReason);
        Assert.Contains("不冒充", f.UnavailableReason);
    }

    [Fact]
    public void 逐核頻率由兩次取樣的斜率算出()
    {
        // 核 A：0.1 秒窗內 TSC 前進 2,000,000 → 20 MHz
        var samples = new List<TscCrossCoreSyncService.CoreTscSample>
        {
            new(0, "LP0",
                new(QpcMid: 0, Tsc: 1_000_000),
                new(QpcMid: 1_000_000, Tsc: 3_000_000)),
        };
        var facts = TscCrossCoreSyncService.BuildFacts(At, samples, QpcHz);
        var core = facts.Single(f => f.Key == "ts.tsc.skew.core.0");
        Assert.Equal(FactTrustLevel.Measured, core.Trust);
        Assert.Equal(20.0, (double)core.NumericValue!, 6);   // MHz
    }

    [Fact]
    public void 跨核偏移把各核外推到同一參考時刻再比()
    {
        // 核 A：0.1 秒窗（QPC 0→1,000,000），TSC 1M→3M → 20 MHz
        // 核 B：0.11 秒窗（QPC 0→1,100,000），TSC 2M→4.05M → 18.63… MHz
        // 參考時刻＝最晚的第二取樣（1,100,000）；A 外推 +100,000 QPC 刻（0.01 秒）×20 MHz = +200,000 TSC
        // 對齊後：A = 3,200,000；B = 4,050,000 → 跨度 850,000 TSC ÷ 最大頻率 20 MHz = 42.5 ms
        var samples = new List<TscCrossCoreSyncService.CoreTscSample>
        {
            new(0, "LP0",
                new(QpcMid: 0, Tsc: 1_000_000),
                new(QpcMid: 1_000_000, Tsc: 3_000_000)),
            new(2, "LP2",
                new(QpcMid: 0, Tsc: 2_000_000),
                new(QpcMid: 1_100_000, Tsc: 4_050_000)),
        };
        var facts = TscCrossCoreSyncService.BuildFacts(At, samples, QpcHz);
        var max = facts.Single(f => f.Key == "ts.tsc.skew.max");
        Assert.Equal(FactTrustLevel.Measured, max.Trust);
        Assert.Equal(FactAvailability.Present, max.Availability);
        Assert.Equal(42_500_000.0, (double)max.NumericValue!, 0);   // ns
        Assert.Contains("含取樣延遲誤差", max.Value);
        Assert.Contains("不判決", max.Value);
    }

    [Fact]
    public void 只有一核完成取樣時不以單核冒充跨核偏移()
    {
        var samples = new List<TscCrossCoreSyncService.CoreTscSample>
        {
            new(0, "LP0",
                new(QpcMid: 0, Tsc: 1_000_000),
                new(QpcMid: 1_000_000, Tsc: 3_000_000)),
        };
        var facts = TscCrossCoreSyncService.BuildFacts(At, samples, QpcHz);
        var max = facts.Single(f => f.Key == "ts.tsc.skew.max");
        Assert.Equal(FactAvailability.NotApplicable, max.Availability);
        Assert.Null(max.NumericValue);
        Assert.Contains("至少 2 核", max.UnavailableReason);
    }

    [Fact]
    public void TSC未前進的核標ReadError且不進入偏移計算()
    {
        var samples = new List<TscCrossCoreSyncService.CoreTscSample>
        {
            new(0, "LP0",
                new(QpcMid: 0, Tsc: 3_000_000),
                new(QpcMid: 1_000_000, Tsc: 3_000_000)),   // 沒前進
            new(2, "LP2",
                new(QpcMid: 0, Tsc: 2_000_000),
                new(QpcMid: 1_100_000, Tsc: 4_050_000)),
        };
        var facts = TscCrossCoreSyncService.BuildFacts(At, samples, QpcHz);
        var bad = facts.Single(f => f.Key == "ts.tsc.skew.core.0");
        Assert.Equal(FactAvailability.ReadError, bad.Availability);
        // 只剩 1 個有效核 → 偏移不可算
        var max = facts.Single(f => f.Key == "ts.tsc.skew.max");
        Assert.Equal(FactAvailability.NotApplicable, max.Availability);
    }

    [Fact]
    public void 預算常數存在且截斷在值裡標註()
    {
        Assert.Equal(100, TscCrossCoreSyncService.SampleIntervalMs);
        Assert.Equal(5000, TscCrossCoreSyncService.TotalBudgetMs);
        var samples = new List<TscCrossCoreSyncService.CoreTscSample>
        {
            new(0, "LP0", new(0, 1_000_000), new(1_000_000, 3_000_000)),
            new(2, "LP2", new(0, 2_000_000), new(1_000_000, 4_050_000)),
        };
        var facts = TscCrossCoreSyncService.BuildFacts(At, samples, QpcHz,
            truncated: true, totalProcessorCount: 36);
        var max = facts.Single(f => f.Key == "ts.tsc.skew.max");
        Assert.Contains("未涵蓋全部 36 個邏輯處理器", max.Value);
    }
}
