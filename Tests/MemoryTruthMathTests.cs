using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>記憶體真實面貌（認可）的換算與判定（純函式，不呼叫 GetPerformanceInfo）。</summary>
public class MemoryTruthMathTests
{
    [Fact]
    public void 頁數依實測頁面大小換算成GB()
    {
        // 4 KB 頁 × 262,144 頁 = 1 GiB
        var r = MemoryTruthMath.ToGigabytes(262_144, 262_144, 262_144, 262_144, 4096);
        Assert.Equal(1.0, r.CommitGb, 3);
        Assert.Equal(1.0, r.PhysicalGb, 3);
    }

    [Fact]
    public void 大頁面大小不寫死4KB()
    {
        // 同樣頁數、64 KB 頁 → 16 倍，證明沒有把 4 KB 寫死
        var r = MemoryTruthMath.ToGigabytes(262_144, 0, 0, 0, 65_536);
        Assert.Equal(16.0, r.CommitGb, 3);
    }

    [Fact]
    public void 頁面大小為零時回安全值不除以零()
    {
        var r = MemoryTruthMath.ToGigabytes(262_144, 262_144, 262_144, 262_144, 0);
        Assert.Equal(new MemoryTruthMath.Reading(0, 0, 0, 0), r);
    }

    [Fact]
    public void 尖峰超過實體判定為曾動用分頁檔()
    {
        var (exceeded, verdict) = MemoryTruthMath.Judge(40.0, 32.0);
        Assert.True(exceeded);
        Assert.Contains("分頁檔", verdict);
    }

    [Fact]
    public void 尖峰未超過實體判定為未超過()
    {
        var (exceeded, verdict) = MemoryTruthMath.Judge(20.0, 32.0);
        Assert.False(exceeded);
        Assert.Equal("未超過實體", verdict);
    }

    [Fact]
    public void 尖峰恰等於實體不算超過()
    {
        // 邊界：相等不構成「超過」，不得因浮點或 >= 誤判
        var (exceeded, _) = MemoryTruthMath.Judge(32.0, 32.0);
        Assert.False(exceeded);
    }
}
