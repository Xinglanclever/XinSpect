using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WLAN_BSS_ENTRY 純解碼契約（ITER44）：頻道由 BSS list 的中心頻率換算（不是猜的），
/// 佈局依 wlanapi.h 手算（sizeof=360）；頻率換不出頻道就回 0 不猜。
/// 解碼器只吃位元組緩衝——wlanapi 通路層極薄不在單測範圍。
/// </summary>
public class WifiBssDecoderTests
{
    [Theory]
    [InlineData(2412000u, 1)]    // 2.4 GHz：2407 + 5×1
    [InlineData(2437000u, 6)]
    [InlineData(2472000u, 13)]
    [InlineData(2484000u, 14)]   // 2.4 GHz ch14 特例（2484 不在等差上）
    [InlineData(5180000u, 36)]   // 5 GHz：5000 + 5×36
    [InlineData(5500000u, 100)]
    [InlineData(5825000u, 165)]
    [InlineData(5955000u, 1)]    // 6 GHz：5955 + 5×(ch-1)
    [InlineData(6115000u, 33)]
    [InlineData(4910000u, 182)]  // 4.9 GHz 公共安全頻段：4000 + 5×ch
    public void 中心頻率換算頻道_各頻段等差公式(uint freqKhz, int expected)
        => Assert.Equal(expected, WifiBssDecoder.CenterFrequencyToChannel(freqKhz));

    [Theory]
    [InlineData(0u)]          // 0＝未提供
    [InlineData(2400000u)]    // 頻段外
    [InlineData(2483000u)]    // 2.4G 等差之外
    [InlineData(2413000u)]    // 非 5 MHz 格點（不猜）
    [InlineData(5920000u)]    // 5G 與 6G 間的空洞
    public void 頻率換不出頻道如實回null(uint freqKhz)
        => Assert.Null(WifiBssDecoder.CenterFrequencyToChannel(freqKhz));

    [Fact]
    public void BSS項目_金標向量_依wlanapih手算()
    {
        // 手算 WLAN_BSS_ENTRY（offset 依 wlanapi.h）：
        // 0: uSSIDLength=6、4..10 "HomeAP"；48..54 BSSID；64: lRssi=-58；68: uLinkQuality=72；
        // 96: ulChCenterFrequency=5180000 kHz。
        var entry = new byte[WifiBssDecoder.EntrySize];
        WriteU32(entry, 0, 6);
        System.Text.Encoding.UTF8.GetBytes("HomeAP").CopyTo(entry, 4);
        byte[] bssid = [0x9A, 0x3B, 0x8F, 0xC3, 0xC8, 0x32];
        bssid.CopyTo(entry, 48);
        WriteI32(entry, 64, -58);
        WriteU32(entry, 68, 72);
        WriteU32(entry, 96, 5180000);

        var e = WifiBssDecoder.DecodeEntry(entry);
        Assert.NotNull(e);
        Assert.Equal("HomeAP", e!.Ssid);
        Assert.Equal("9A:3B:8F:C3:C8:32", e.Bssid);
        Assert.Equal(-58, e.Rssi);
        Assert.Equal(72u, e.LinkQuality);
        Assert.Equal(36, e.Channel);
    }

    [Fact]
    public void BSS項目_過短拒解_頻率不明頻道歸零_SSID截斷到32()
    {
        Assert.Null(WifiBssDecoder.DecodeEntry(new byte[99]));

        var unmapped = new byte[WifiBssDecoder.EntrySize];
        WriteU32(unmapped, 0, 3);
        System.Text.Encoding.UTF8.GetBytes("abc").CopyTo(unmapped, 4);
        WriteU32(unmapped, 96, 2400000); // 頻段外
        var e = WifiBssDecoder.DecodeEntry(unmapped);
        Assert.NotNull(e);
        Assert.Equal(0, e!.Channel);     // 換不出頻道不猜

        var longSsid = new byte[WifiBssDecoder.EntrySize];
        WriteU32(longSsid, 0, 200);      // 長度欄說謊（>32）
        System.Text.Encoding.UTF8.GetBytes("X").CopyTo(longSsid, 4);
        var truncated = WifiBssDecoder.DecodeEntry(longSsid);
        Assert.NotNull(truncated);
        Assert.Equal("X", truncated!.Ssid); // 只信佈局允許的 32 位元組，不越界
    }

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
    }

    private static void WriteI32(byte[] b, int off, int v) => WriteU32(b, off, unchecked((uint)v));
}
