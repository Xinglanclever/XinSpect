using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Bus 0 裝置盤點（WP30）的契約：多功能位元決定 fn 掃描（規格行為，不硬掃 256 格）、
/// 空槽不列、原始 ID 與角色並列、讀取錯誤計數進摘要、全失敗整組三態。
/// </summary>
public class Bus0InventoryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 盤點_單功能與多功能裝置如實成列_空槽不列()
    {
        var pci = new FakeInventoryPci();
        var facts = Bus0InventoryService.Collect(pci, At);

        // dev 0x02 fn0：單功能顯示卡
        var igpu = Assert.Single(facts, f => f.Key == "pci.dev.02.0");
        Assert.Contains("顯示控制器", igpu.Value);
        Assert.Contains("Intel", igpu.Value);

        // dev 0x1F fn0+fn5：多功能（header type bit7 設）
        Assert.Single(facts, f => f.Key == "pci.dev.1f.0"); // ISA bridge
        var spi = Assert.Single(facts, f => f.Key == "pci.dev.1f.5"); // SPI 控制器
        Assert.Contains("0x8086:0x06C0", spi.Value);

        // dev 0x14：單功能（MF 未設）——fn3 的 xHCI 不該被掃出（規格行為）
        Assert.DoesNotContain(facts, f => f.Key == "pci.dev.14.3");

        var summary = Assert.Single(facts, f => f.Key == "pci.bus0.inventory");
        Assert.Contains("3 個裝置、4 個功能", summary.Value);
        Assert.Equal(3, summary.NumericValue);
    }

    [Fact]
    public void 盤點_讀取錯誤計數_全失敗整組三態()
    {
        var partial = new PartialFailPci(failFirst: 8);
        var facts = Bus0InventoryService.Collect(partial, At);
        var s1 = Assert.Single(facts, f => f.Key == "pci.bus0.inventory");
        Assert.Contains("讀取失敗", s1.Value);

        var all = Bus0InventoryService.Collect(new AllFailPci(), At);
        var f = Assert.Single(all);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不可達", f.UnavailableReason);
    }

    [Fact]
    public void 盤點_後端不可用整組三態()
    {
        var facts = Bus0InventoryService.Collect(new DeniedPci(), At);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability);
    }

    // ===== 假 PCI =====

    /// <summary>三個裝置：0:02.0 單功能顯示、0:14.0 單功能 xHCI、0:1F 多功能（ISA bridge + SPI）。</summary>
    private sealed class FakeInventoryPci : IPciConfigReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;

        public uint? ReadDword(byte bus, byte device, byte function, uint register)
        {
            if (bus != 0) return 0xFFFF_FFFF;
            return (device, function, register) switch
            {
                (0x02, 0, 0x00) => 0x3EA0_8086,   // iGPU
                (0x02, 0, 0x08) => 0x03000000u,   // 顯示/VGA
                (0x02, 0, 0x0C) => 0x0000_0000,   // 單功能
                (0x14, 0, 0x00) => 0xA36D_8086,   // xHCI
                (0x14, 0, 0x08) => 0x0C03_3000u,  // 序列匯流排/USB（base 0x0C、sub 0x03、progIF 0x30 = xHCI）
                (0x14, 0, 0x0C) => 0x0000_0000,   // 單功能（fn3 雖有資料但不掃）
                (0x1F, 0, 0x00) => 0x06D1_8086,   // LPC/eSPI
                (0x1F, 0, 0x08) => 0x0601_0000u,  // 橋接/ISA bridge
                (0x1F, 0, 0x0C) => 0x0080_0000,   // 多功能位元（header type bits[23:16] 的 bit7）
                (0x1F, 5, 0x00) => 0x06C0_8086,   // SPI 控制器
                (0x1F, 5, 0x08) => 0xFF000000u,   // 未分類（Intel SPI 控制器常見值）
                _ => 0xFFFF_FFFF,
            };
        }
    }

    /// <summary>前 N 個探頭回 null（讀取失敗），其後照 FakeInventoryPci 語意。</summary>
    private sealed class PartialFailPci(int failFirst) : IPciConfigReader
    {
        private int _remaining = failFirst;
        private readonly FakeInventoryPci _inner = new();

        public bool Available => true;
        public string? UnavailableReason => null;

        public uint? ReadDword(byte bus, byte device, byte function, uint register)
        {
            if (_remaining-- > 0) return null;
            return _inner.ReadDword(bus, device, function, register);
        }
    }

    private sealed class AllFailPci : IPciConfigReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public uint? ReadDword(byte bus, byte device, byte function, uint register) => null;
    }

    private sealed class DeniedPci : IPciConfigReader
    {
        public bool Available => false;
        public string? UnavailableReason => "缺 ring0（測試假件）";
        public uint? ReadDword(byte bus, byte device, byte function, uint register) => null;
    }
}
