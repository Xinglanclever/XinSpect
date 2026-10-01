using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 核心記憶體池細目的守門：讀取不丟例外、真機回合理數值、標籤格式正確。
/// </summary>
public class MemoryPoolServiceTests
{
    [Fact]
    public void Read不丟例外且回合理快照()
    {
        var snap = MemoryPoolService.Read();
        Assert.NotNull(snap);
        Assert.True(snap!.PagedPoolMB > 0, "Paged Pool 不該是 0——任何在跑的系統都有核心池");
        Assert.True(snap.NonPagedPoolMB > 0, "Nonpaged Pool 不該是 0");
        Assert.Equal(8, snap.StandbyByPriorityMB.Length);
    }

    [Fact]
    public void Standby總計是各優先級加總()
    {
        var snap = MemoryPoolService.Read();
        Assert.NotNull(snap);
        double sum = snap!.StandbyByPriorityMB.Sum();
        Assert.Equal(snap.StandbyTotalMB, sum, 1);
    }

    [Fact]
    public void 標籤格式是P優先級加MB()
    {
        Assert.Equal("P0 128 MB", MemoryPoolService.StandbyLabel(0, 128.0));
        Assert.Equal("P7 2048 MB", MemoryPoolService.StandbyLabel(7, 2048.0));
    }
}
