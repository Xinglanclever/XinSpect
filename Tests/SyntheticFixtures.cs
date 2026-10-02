namespace XinSpect.Tests;

/// <summary>
/// 合成產生器（V7 M2／WP45）：與解碼器<b>獨立實作</b>的編碼器——位元位置直接照規格文件硬寫，
/// 不參考解碼器的程式碼與常數。它的價值在「兩邊都錯但錯得一樣」會被黃金答案向量抓到：
/// encode 與 decode 各自對著 spec 硬編，再互相比對往返；金標向量（如 FLOCKDN=bit15→0x8000）
/// 釘住絕對值，防兩邊一起漂。
/// </summary>
public static class SyntheticFixtures
{
    // ── PCIe AER：4KB 擴充組態空間合成 ──

    /// <summary>擴充能力鏈條目（offset 是 ECAM 內的絕對位址，0x100 起）。</summary>
    public readonly record struct ExtCap(int Offset, ushort CapId, int? Next);

    /// <summary>照 PCIe Spec 編能力鏈表頭：dword = CapID[15:0]｜Version[19:16]｜Next[31:20]。</summary>
    public static void WriteExtCapHeader(byte[] config4k, int offset, ushort capId, int? next, byte version = 1)
    {
        uint nextRaw = next is { } n ? (uint)(n & 0xFFF) : 0u;
        uint header = capId
            | (uint)(version & 0xF) << 16
            | nextRaw << 20;
        Put(config4k, offset, header);
    }

    /// <summary>照 PCIe Spec AER 能力佈局填錯誤狀態：Uncorrectable Error Status @+0x04、Correctable Error Status @+0x10。</summary>
    public static void WriteAerStatus(byte[] config4k, int aerOffset, uint uncorrectable, uint correctable)
    {
        Put(config4k, aerOffset + 0x04, uncorrectable);
        Put(config4k, aerOffset + 0x10, correctable);
    }

    /// <summary>造一個帶能力鏈的 4KB 空間；offsets 不在鏈上的位元組保持 0（真實空間的保留區也是 0）。</summary>
    public static byte[] Config4k(IReadOnlyList<ExtCap> caps, IReadOnlyList<(int Offset, uint Value)> dwords)
    {
        var cfg = new byte[4096];
        foreach (var cap in caps)
            WriteExtCapHeader(cfg, cap.Offset, cap.CapId, cap.Next);
        foreach (var (offset, value) in dwords)
            Put(cfg, offset, value);
        return cfg;
    }

    // ── PCH SPI：暫存器編碼（位元定義照 Intel PCH EDS 硬寫）──

    public static uint EncodeHsfsts(bool fdone, bool fcerr, bool ael, bool wrsdis, bool fdopss, bool flockdn, uint unknownBits)
    {
        // Intel PCH EDS SPIBAR+0x04：FDONE bit0、FCERR bit1、AEL bit2、WRSDIS bit11、FDOPSS bit13、FLOCKDN bit15
        uint raw = unknownBits;
        if (fdone) raw |= 1u << 0;
        if (fcerr) raw |= 1u << 1;
        if (ael) raw |= 1u << 2;
        if (wrsdis) raw |= 1u << 11;
        if (fdopss) raw |= 1u << 13;
        if (flockdn) raw |= 1u << 15;
        return raw;
    }

    /// <summary>FRAP：bits[3:0] BRWA、bits[7:4] BRRA。</summary>
    public static uint EncodeFrap(byte biosRegionWriteMask, byte biosRegionReadMask)
        => (uint)(biosRegionWriteMask & 0xF) | (uint)(biosRegionReadMask & 0xF) << 4;

    /// <summary>FREGx：基底 bits[14:0]、上限 bits[30:16]，4KB 單位。</summary>
    public static uint EncodeFreg(ushort base4k, ushort limit4k)
        => base4k | (uint)(limit4k & 0x7FFF) << 16;

    /// <summary>PRx：範圍佈局同 FREG、WPE bit15、RPE bit31。</summary>
    public static uint EncodePrx(ushort base4k, ushort limit4k, bool writeProtect, bool readProtect)
    {
        uint raw = EncodeFreg(base4k, limit4k);
        if (writeProtect) raw |= 1u << 15;
        if (readProtect) raw |= 1u << 31;
        return raw;
    }

    private static void Put(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
