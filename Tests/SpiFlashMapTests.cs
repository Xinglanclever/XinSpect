using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// SPI flash 地圖解碼器（WP4）的契約：由 FREG 推導快閃大小與 4GB 頂端映射基底、
/// BIOS 區（FREG1）位移與長度、全空不猜、異常範圍不參與推導。
/// </summary>
public class SpiFlashMapTests
{
    [Fact]
    public void 典型16MB快閃_基底推導與BIOS區範圍()
    {
        // 描述符 0x000-0x00F、BIOS 0x010-0xBFF、ME 0xC00-0xFFF（單位 4KB）→ 總大小 16MB
        var map = SpiFlashMap.Decode(
        [
            SyntheticFixtures.EncodeFreg(0x000, 0x00F),
            SyntheticFixtures.EncodeFreg(0x010, 0xBFF),
            SyntheticFixtures.EncodeFreg(0xC00, 0xFFF),
            0, 0, 0, // GbE／平台資料／EC 未配置
        ]);

        Assert.NotNull(map);
        Assert.Equal(16UL * 1024 * 1024, map.FlashSizeBytes);
        Assert.Equal(0xFF000000UL, map.MappedBase);            // 4GB 頂端
        Assert.Equal(0x010UL * 4096, map.BiosOffsetBytes);
        Assert.Equal((0xBFF - 0x010 + 1) * 4096UL, map.BiosLengthBytes);
    }

    [Fact]
    public void BIOS區未配置或異常_位移長度如實為null_地圖仍可推導()
    {
        var noBios = SpiFlashMap.Decode(
        [
            SyntheticFixtures.EncodeFreg(0x000, 0x00F),
            0, // BIOS 未配置
            SyntheticFixtures.EncodeFreg(0x010, 0x1FF),
            0, 0, 0,
        ]);
        Assert.NotNull(noBios);
        Assert.Null(noBios.BiosOffsetBytes);
        Assert.Null(noBios.BiosLengthBytes);

        var corrupt = SpiFlashMap.Decode(
        [
            SyntheticFixtures.EncodeFreg(0x000, 0x00F),
            SyntheticFixtures.EncodeFreg(0x500, 0x100), // 上限 < 基底：範圍異常，不參與
            SyntheticFixtures.EncodeFreg(0x010, 0x1FF),
            0, 0, 0,
        ]);
        Assert.NotNull(corrupt);
        Assert.Null(corrupt.BiosOffsetBytes);
        Assert.Equal(0x200UL * 4096, corrupt.FlashSizeBytes); // 大小只取有效區的最大上限
    }

    [Fact]
    public void FREG全空_回null不猜()
    {
        Assert.Null(SpiFlashMap.Decode([0, 0, 0, 0, 0, 0]));
        Assert.Null(SpiFlashMap.Decode([]));
    }
}
