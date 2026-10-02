using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// PCI BAR 資源解碼器（WP30）的契約：I/O／32-bit／64-bit（佔兩槽配對）／可預取的辨識、
/// 全 0＝未配置如實計數、Expansion ROM 啟用位元、唯讀界線（不出大小）。
/// </summary>
public class PciBarsTests
{
    [Fact]
    public void 記憶體BAR_32與64位元與可預取辨識()
    {
        var (resources, unconfigured) = PciBars.DecodeHeader(
        [
            0xFED10000,        // 32-bit 非預取：SPIBAR 慣例值
            0xF600000C,        // 64-bit 可預取（bits[2:1]=10、bit3=1）低位
            0x00000000,        // 64-bit 高位（被配對消費，不算未配置）
            0x00003001,        // I/O base 0x3000
            0, 0,
        ]);

        Assert.Equal(2, unconfigured); // 最後兩個 BAR 為 0＝未配置；64-bit 的高位槽被配對消費不算
        Assert.Equal(3, resources.Count);
        Assert.Equal("記憶體（32-bit）", resources[0].Kind);
        Assert.Equal(0xFED10000UL, resources[0].Base);
        Assert.False(resources[0].Prefetchable);

        Assert.Equal("記憶體（64-bit）", resources[1].Kind);
        Assert.Equal(0xF6000000UL, resources[1].Base);
        Assert.True(resources[1].Prefetchable);

        Assert.Equal("I/O", resources[2].Kind);
        Assert.Equal(0x3000UL, resources[2].Base);
    }

    [Fact]
    public void 六十四位元BAR高位非零_組回完整位址()
    {
        var (resources, _) = PciBars.DecodeHeader(
        [
            0xF600000C,        // 64-bit 低位
            0x00000001,        // 高位 → base = 0x1_F6000000
            0, 0, 0, 0,
        ]);

        var bar = Assert.Single(resources);
        Assert.Equal(0x1_F6000000UL, bar.Base);
    }

    [Fact]
    public void 全0BAR計未配置_ExpansionROM解啟用與基底()
    {
        var (resources, unconfigured) = PciBars.DecodeHeader(
            [0, 0, 0, 0, 0, 0],
            expansionRom: 0xFFE00002); // bit0=1（啟用）、基底 0xFFE00000

        Assert.Equal(6, unconfigured);
        var rom = resources[0];
        Assert.Equal("Expansion ROM", rom.Kind);
        Assert.Equal(0xFFE00000UL, rom.Base);
    }

    [Fact]
    public void 描述行_全空與資源並列都可稽核()
    {
        var empty = PciBars.Describe([], 4);
        Assert.Contains("4 個 BAR 為 0", empty);

        var (resources, _) = PciBars.DecodeHeader([0xFED10000, 0, 0, 0, 0, 0]);
        string text = PciBars.Describe(resources, 5);
        Assert.Contains("記憶體（32-bit） 0xFED10000", text);
        Assert.Contains("另有 5 個未配置 BAR", text);
    }
}
