using System.Text;

namespace XinSpect;

/// <summary>一個 BSS（基地台）的解碼結果。Channel=0 表示中心頻率換算不出頻道——不猜。</summary>
public sealed record WifiBssEntry(string Ssid, string Bssid, int Rssi, uint LinkQuality, int Channel);

/// <summary>
/// WLAN_BSS_ENTRY 的純解碼器（ITER44）：頻道由 BSS list 的中心頻率換算——頻率是量到的、
/// 換算是查表公式，兩步都有據；頻率落在任何等差之外就回 0（呼叫方顯示 —，不猜）。
/// 佈局依 wlanapi.h 手算：sizeof=360（DOT11_SSID 36＋uPhyId 8 對齊到 40＋BSSID 48＋RSSI 64＋
/// LinkQuality 68＋中心頻率 96，單位 kHz）。
/// </summary>
public static class WifiBssDecoder
{
    /// <summary>WLAN_BSS_ENTRY 的位元組數（wlanapi.h）。</summary>
    public const int EntrySize = 360;

    /// <summary>解單一 BSS 項目。緩衝過短（不足解到中心頻率欄）回 null。</summary>
    [SpecRef("Microsoft wlanapi.h：WLAN_BSS_ENTRY 佈局（sizeof=360）——dot11Ssid(DOT11_SSID 36)＋uPhyId(ULONGLONG 對齊 offset 40)＋dot11Bssid(6, offset 48)＋lRssi(offset 64)＋uLinkQuality(offset 68)＋ulChCenterFrequency(offset 96, kHz)")]
    public static WifiBssEntry? DecodeEntry(byte[] entry)
    {
        if (entry.Length < 100) return null;
        uint ssidLen = Math.Min(ReadU32(entry, 0), 32);
        string ssid = Encoding.UTF8.GetString(entry, 4, (int)ssidLen).TrimEnd('\0');
        string bssid = $"{entry[48]:X2}:{entry[49]:X2}:{entry[50]:X2}:{entry[51]:X2}:{entry[52]:X2}:{entry[53]:X2}";
        int rssi = BitConverter.ToInt32(entry, 64);
        uint quality = ReadU32(entry, 68);
        int channel = CenterFrequencyToChannel(ReadU32(entry, 96)) ?? 0;
        return new WifiBssEntry(ssid, bssid, rssi, quality, channel);
    }

    /// <summary>
    /// 中心頻率（kHz）→ 802.11 頻道號。換算不出（頻段外／非 5 MHz 格點）回 null 不猜。
    /// </summary>
    [SpecRef("IEEE 802.11-2020 Annex E 頻道編號：2.4 GHz 中心頻率＝2407+5×ch（ch 14＝2484 特例）、4.9 GHz＝4000+5×ch、5 GHz＝5000+5×ch、6 GHz＝5955+5×(ch-1)；wlanapi ulChCenterFrequency 單位為 kHz")]
    public static int? CenterFrequencyToChannel(uint centerFreqKhz)
    {
        uint mhz = centerFreqKhz / 1000;
        return mhz switch
        {
            2484 => 14,
            >= 2412 and <= 2472 when (mhz - 2407) % 5 == 0 => (int)((mhz - 2407) / 5),
            >= 4910 and <= 4990 when (mhz - 4000) % 5 == 0 => (int)((mhz - 4000) / 5),
            >= 5000 and <= 5885 when (mhz - 5000) % 5 == 0 => (int)((mhz - 5000) / 5),
            >= 5955 and <= 7115 when (mhz - 5955) % 5 == 0 => (int)((mhz - 5955) / 5 + 1),
            _ => null,
        };
    }

    private static uint ReadU32(byte[] b, int off)
        => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
}
