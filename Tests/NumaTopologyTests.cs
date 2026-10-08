using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// NUMA 拓撲事實（WP21）的契約：多節點／單節點呈現、遮罩與 popcount、節點查詢失敗三態、
/// API 不可用整組三態。全部注入探測，不碰真 kernel32。
/// </summary>
public class NumaTopologyTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 雙節點_節點數與遮罩成列_popcount正確()
    {
        var facts = NumaTopologyService.Collect(At,
            highestNodeProbe: () => 1,
            nodeMaskProbe: node => node switch
            {
                0 => (0x00000000000000FFUL, 0),  // 8 個邏輯 CPU
                1 => (0x0000000000FF0000UL, 0),  // 8 個邏輯 CPU（不同區段）
                _ => null,
            });

        var top = Assert.Single(facts, f => f.Key == "numa.topology");
        Assert.Contains("2 個 NUMA 節點", top.Value);
        Assert.Equal(2, top.NumericValue);

        var n0 = Assert.Single(facts, f => f.Key == "numa.node.0");
        Assert.Contains("0xFF（8 個邏輯 CPU、群組 0）", n0.Value);
        Assert.Equal(8, n0.NumericValue);

        var n1 = Assert.Single(facts, f => f.Key == "numa.node.1");
        Assert.Contains("0xFF0000", n1.Value);
    }

    [Fact]
    public void 單節點_誠實標桌面常態()
    {
        var facts = NumaTopologyService.Collect(At,
            highestNodeProbe: () => 0,
            nodeMaskProbe: _ => (0xFFFFFUL, 0));

        var top = Assert.Single(facts, f => f.Key == "numa.topology");
        Assert.Contains("單 NUMA 節點", top.Value);
        Assert.Equal(1, top.NumericValue);
        var n0 = Assert.Single(facts, f => f.Key == "numa.node.0");
        Assert.Equal(20, n0.NumericValue); // 0xFFFFF popcount
    }

    [Fact]
    public void 單節點_跨節點要標不適用而不是正常()
    {
        // 誠實契約：未驗證 ≠ 可用。本機看不到跨節點延遲不代表這台機器有那個能力，
        // 也不代表沒有——就是沒量到。標「正常」會讓閱讀者以為已經驗過了。
        var facts = NumaTopologyService.Collect(At,
            highestNodeProbe: () => 0,
            nodeMaskProbe: _ => (0xFFFFFUL, 0));

        var x = Assert.Single(facts, f => f.Key == "numa.topology.xnode");
        Assert.Equal(FactAvailability.NotApplicable, x.Availability);
        Assert.Contains("無從量測", x.Value);
        Assert.Contains("未驗證不代表可用", x.UnavailableReason);
        Assert.Contains("沒量到不等於沒有問題", x.UnavailableReason);

        // 主列的措辭也要帶上這一點，不能只藏在延伸欄位
        Assert.Contains("未驗證不代表可用", Assert.Single(facts, f => f.Key == "numa.topology").Value);
    }

    [Fact]
    public void 多節點_跨節點標為可量測()
    {
        var facts = NumaTopologyService.Collect(At,
            highestNodeProbe: () => 1,
            nodeMaskProbe: node => node == 0 ? (0xFFUL, (ushort)0) : (0xFF00UL, (ushort)0));

        var x = Assert.Single(facts, f => f.Key == "numa.topology.xnode");
        Assert.Equal(FactAvailability.Present, x.Availability);
        Assert.Contains("可量測", x.Value);
        Assert.Null(x.UnavailableReason);
    }

    [Fact]
    public void API不可用整組三態_節點查詢失敗單點三態()
    {
        var denied = NumaTopologyService.Collect(At, highestNodeProbe: () => null);
        var d = Assert.Single(denied);
        Assert.Equal(FactAvailability.NotSupported, d.Availability);

        var fail = NumaTopologyService.Collect(At,
            highestNodeProbe: () => 1,
            nodeMaskProbe: node => node == 0 ? (0xFUL, 0) : null);
        var n1 = Assert.Single(fail, f => f.Key == "numa.node.1");
        Assert.Equal(FactAvailability.ReadError, n1.Availability);
        Assert.Null(n1.NumericValue);
    }

    [Fact]
    public void 接線_平台事實組含NUMA與Rowhammer聲明()
    {
        var svc = new EvidenceLabService();
        svc.LoadPlatformFacts();

        // 本機 kernel32 一定可答（測試機為 Windows）：鍵形狀釘住，值不釘（機器相依）。
        Assert.Contains(svc.PlatformFacts, f => f.Key == "numa.topology");
        Assert.Contains(svc.PlatformFacts, f => f.Key == "numa.node.0");
        var rh = Assert.Single(svc.PlatformFacts, f => f.Key == "mem.rowhammer");
        Assert.Equal(FactAvailability.Present, rh.Availability);
        Assert.Contains("未施測", rh.Value);
        Assert.Contains("R-MEM-05", rh.Value);

        // 進韌體安全頁渲染列（分類排序後仍在）。
        Assert.Contains(svc.FirmwareSecurityRows, r => r.Name == "Rowhammer／記憶體攻擊面");
    }
}
