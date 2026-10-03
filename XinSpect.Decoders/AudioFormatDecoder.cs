namespace XinSpect;

/// <summary>
/// WAVEFORMATEX／WAVEFORMATEXTENSIBLE 的純解碼器（V7 WP23／A15）：
/// 混合格式（PKEY_AudioEngine_DeviceFormat）的聲道數、取樣率、位元深度。
/// 垃圾（聲道 0、取樣率 0）如實回 null——不解碼垃圾。
/// </summary>
public static class AudioFormatDecoder
{
    /// <summary>解 WAVEFORMATEX（至少 16 位元組；EXTENSIBLE 須 ≥ 40）。格式異常回 null。</summary>
    [SpecRef("Microsoft WAVEFORMATEX 佈局：wFormatTag u16、nChannels u16、nSamplesPerSec u32、nAvgBytesPerSec u32、nBlockAlign u16、wBitsPerSample u16、cbSize u16；EXTENSIBLE（0xFFFE）接 validBits u16、channelMask u32、SubFormat GUID")]
    public static (ushort Channels, uint SamplesPerSec, ushort Bits, string TagText)? Parse(byte[] data)
    {
        if (data.Length < 16) return null;
        ushort tag = GetU16(data, 0);
        ushort channels = GetU16(data, 2);
        uint rate = GetU32(data, 4);
        ushort bits = GetU16(data, 14);
        if (channels == 0 || channels > 64 || rate == 0 || rate > 2_000_000) return null;

        string tagText = tag switch
        {
            1 => "PCM",
            3 => "IEEE 浮點",
            0xFFFE => data.Length >= 40 ? "可延伸（EXTENSIBLE）" : "可延伸（宣告不足 40 位元組，擴充欄位缺失）",
            _ => $"formatTag 0x{tag:X4}",
        };
        return (channels, rate, bits, tagText);
    }

    /// <summary>端點一行描述。</summary>
    public static string Describe((ushort Channels, uint SamplesPerSec, ushort Bits, string TagText) f)
        => $"{f.Channels} 聲道、{f.SamplesPerSec} Hz、{f.Bits}-bit（{f.TagText}）";

    private static ushort GetU16(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));
    private static uint GetU32(byte[] b, int off) => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
}
