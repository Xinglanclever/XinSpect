namespace XinSpect;

/// <summary>
/// SPI flash 位址地圖的純解碼器（V7 WP4／A9）：從 FREG0-5 推導快閃總大小與記憶體映射基底。
/// Intel PCH 標準行為：快閃內容映射至實體位址 4GB 頂端——基底＝0x1_0000_0000 − 快閃大小。
/// 快閃大小由 FREGx 的最大上限（4KB 單位）推得；FREG 全空或上限異常時如實回 null，不猜。
/// 區域順序（index）：0 描述符、1 BIOS、2 ME、3 GbE、4 平台資料、5 EC——與 RegionsFact 同源。
/// </summary>
public static class SpiFlashMap
{
    /// <summary>快閃地圖：總大小、記憶體映射基底、BIOS 區在快閃內的位移與長度（BIOS 區未配置時為 null）。</summary>
    public sealed record FlashMap(ulong FlashSizeBytes, ulong MappedBase, ulong? BiosOffsetBytes, ulong? BiosLengthBytes);

    public static FlashMap? Decode(IReadOnlyList<uint> fregsRaw)
    {
        uint maxLimit4k = 0;
        bool any = false;
        foreach (var raw in fregsRaw)
        {
            var d = SpiFlash.DecodeFreg(raw);
            if (d.Empty || d.Limit4k < d.Base4k) continue; // 空區或範圍異常：不參與大小推導
            any = true;
            if (d.Limit4k > maxLimit4k) maxLimit4k = d.Limit4k;
        }
        if (!any) return null; // 全空：無從推得快閃大小——不猜

        ulong flashSize = ((ulong)maxLimit4k + 1) * 4096;
        ulong mappedBase = 0x1_0000_0000UL - flashSize;

        ulong? biosOffset = null, biosLength = null;
        if (fregsRaw.Count > 1)
        {
            var bios = SpiFlash.DecodeFreg(fregsRaw[1]);
            if (!bios.Empty && bios.Limit4k >= bios.Base4k)
            {
                biosOffset = (ulong)bios.Base4k * 4096;
                biosLength = ((ulong)bios.Limit4k - bios.Base4k + 1) * 4096;
            }
        }
        return new FlashMap(flashSize, mappedBase, biosOffset, biosLength);
    }
}
