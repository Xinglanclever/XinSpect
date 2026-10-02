using System.Text;

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

    // ── TSE2004（TSOD）──

    /// <summary>溫度 → 暫存器：°C×16 取 12 位元二補數放 bits[15:4]，旗號 bits[3:0] 自訂。</summary>
    public static ushort EncodeTsodTemperature(double celsius, byte flags = 0)
        => (ushort)((((int)Math.Round(celsius * 16) & 0xFFF) << 4) | (flags & 0xF));

    // ── 平台安全 MSR（Intel SDM Vol.4）──

    /// <summary>IA32_FEATURE_CONTROL：Lock bit0、VMX-in-SMX bit1、VMX-outside-SMX bit2。</summary>
    public static ulong EncodeFeatureControl(bool lockBit, bool vmxInSmx, bool vmxOutsideSmx)
    {
        ulong raw = 0;
        if (lockBit) raw |= 1;
        if (vmxInSmx) raw |= 1 << 1;
        if (vmxOutsideSmx) raw |= 1 << 2;
        return raw;
    }

    /// <summary>IA32_DEBUG_INTERFACE：ENABLE bit0、LOCK bit30、DEBUG_OCCURRED bit31。</summary>
    public static ulong EncodeDebugInterface(bool enable, bool lockBit, bool debugOccurred)
    {
        ulong raw = 0;
        if (enable) raw |= 1;
        if (lockBit) raw |= 1UL << 30;
        if (debugOccurred) raw |= 1UL << 31;
        return raw;
    }

    // ── 晶片組安全暫存器 ──

    /// <summary>BIOS_CNTL：BIOSWE bit0、BLE bit1、SMM_BWP bit5。</summary>
    public static uint EncodeBiosCntl(bool biosWe, bool ble, bool smmBwp)
    {
        uint raw = 0;
        if (biosWe) raw |= 1;
        if (ble) raw |= 1 << 1;
        if (smmBwp) raw |= 1 << 5;
        return raw;
    }

    /// <summary>SMRAMC：D_LCK bit4、D_CLS bit5、D_OPEN bit6。</summary>
    public static uint EncodeSmramc(bool dLck, bool dCls, bool dOpen)
    {
        uint raw = 0;
        if (dLck) raw |= 1 << 4;
        if (dCls) raw |= 1 << 5;
        if (dOpen) raw |= 1 << 6;
        return raw;
    }

    // ── ACPI 表 ──

    /// <summary>照 ACPI Spec 編 36 位元組表頭：Signature@0、Length@4、Revision@8、Checksum@9、OEMID@10、OEM Table ID@16。</summary>
    public static byte[] EncodeAcpiTable(string signature, byte revision, string oemId, string oemTableId, byte[] payload)
    {
        var table = new byte[36 + payload.Length];
        Encoding.ASCII.GetBytes(signature.PadRight(4, ' ')).CopyTo(table, 0);
        BitConverter.GetBytes((uint)table.Length).CopyTo(table, 4);
        table[8] = revision;
        Encoding.ASCII.GetBytes(oemId.PadRight(6, ' ')).CopyTo(table, 10);
        Encoding.ASCII.GetBytes(oemTableId.PadRight(8, ' ')).CopyTo(table, 16);
        payload.CopyTo(table, 36);
        byte sum = 0;
        foreach (byte b in table) sum += b;
        table[9] = (byte)(0 - sum); // 校驗和：全表位元組和 mod 256 = 0
        return table;
    }

    /// <summary>照 ACPI Spec 編 MCFG 條目：基底 u64@0、PCI Segment Group u16@8、起始 bus@10、結束 bus@11、保留 4 bytes。</summary>
    public static byte[] EncodeMcfgPayload(params (ulong Base, ushort Group, byte StartBus, byte EndBus)[] entries)
    {
        var payload = new byte[8 + entries.Length * 16]; // 8 bytes 保留區
        for (int i = 0; i < entries.Length; i++)
        {
            int off = 8 + i * 16;
            var (b, g, s, e) = entries[i];
            BitConverter.GetBytes(b).CopyTo(payload, off);
            BitConverter.GetBytes(g).CopyTo(payload, off + 8);
            payload[off + 10] = s;
            payload[off + 11] = e;
        }
        return payload;
    }

    private static void Put(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
