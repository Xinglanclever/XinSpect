namespace XinSpect;

/// <summary>
/// BIOS 區與參考映像的比對（V7 WP4／A9 第二層）：使用者供原廠（或信任來源）的 BIOS 區映像，
/// 逐 4KB 塊與快閃可讀面比對，報差異塊數與位移。誠實界線：
/// ① 參考映像大小與 BIOS 區不符→如實拒比（不猜對齊、不截斷、不補值）；
/// ② 比對的是<b>可讀面</b>——PRx 讀保護攔截的範圍以全 F 呈現，會被報成差異，事實文字標注此界線；
/// ③ 差異不等於「被改壞」——原廠更新、OEM 客製都可能造成，判讀權在使用者。
/// </summary>
public static class SpiFlashCompareService
{
    private const string Category = "韌體安全";
    private const string Key = "spi.bios_compare";
    private const string Name = "BIOS 區比對（vs 參考映像）";

    public static HardwareFact Compare(IPciConfigReader pci, IMmioReader mmio, byte[] reference, DateTimeOffset at)
    {
        const string source = "記憶體映射快閃 vs 使用者提供映像";
        var access = SpiFlashService.ReadController(pci, mmio, out var availability, out string error);
        if (access is null)
            return Unavailable(source, at, availability, error);

        var fregs = Enumerable.Range(0, 6).Select(i => BitConverter.ToUInt32(access.Block, 0x54 + i * 4)).ToArray();
        var map = SpiFlashMap.Decode(fregs);
        if (map is null)
            return Unavailable(source, at, FactAvailability.NotApplicable, "快閃地圖無法推導（FREG 全空或異常），BIOS 區無從定址");
        if (map.BiosOffsetBytes is not { } biosOffset || map.BiosLengthBytes is not { } biosLength)
            return Unavailable(source, at, FactAvailability.NotApplicable, "BIOS 區（FREG1）未配置或範圍異常，無可比對範圍");
        if (reference.Length != (int)biosLength)
            return Unavailable(source, at, FactAvailability.NotApplicable,
                $"參考映像 {reference.Length} 位元組與 BIOS 區 {biosLength} 位元組大小不符——誠實拒比，不猜對齊或補值");

        var (bytes, _, readError) = SpiFlashHashService.ReadRange(mmio, map.MappedBase + biosOffset, biosLength);
        if (bytes is null)
            return Unavailable(source, at, FactAvailability.ReadError, $"BIOS 區讀取失敗：{readError}");

        int diffBlocks = 0;
        var firstOffsets = new List<ulong>();
        for (int block = 0; block * 4096 < bytes.Length; block++)
        {
            int start = block * 4096, end = Math.Min(start + 4096, bytes.Length);
            bool differs = false;
            for (int i = start; i < end; i++)
                if (bytes[i] != reference[i]) { differs = true; break; }
            if (!differs) continue;
            diffBlocks++;
            if (firstOffsets.Count < 8) firstOffsets.Add(biosOffset + (ulong)start);
        }

        bool rpeOverlap = HasReadProtectedOverlap(access.Block, biosOffset, biosLength);
        string rpeNote = rpeOverlap ? "；BIOS 區有 PRx 讀保護（RPE）重疊——被擋範圍以全 F 呈現，會被計為差異" : "";
        return diffBlocks == 0
            ? new HardwareFact(Key, Category, Name,
                $"一致（{biosLength / 4096} 個 4KB 塊全部相同）{rpeNote}", "", source, FactTrustLevel.Measured, false, at, 0)
            : new HardwareFact(Key, Category, Name,
                $"差異 {diffBlocks} 個 4KB 塊：前 {firstOffsets.Count} 個快閃位移 {string.Join(" ", firstOffsets.Select(o => $"0x{o:X}"))}" +
                $"；差異本身不等於被改壞（原廠更新／OEM 客製皆可能），判讀權在使用者{rpeNote}",
                "", source, FactTrustLevel.Measured, false, at, diffBlocks);
    }

    private static bool HasReadProtectedOverlap(byte[] block, ulong biosOffset, ulong biosLength)
    {
        for (int i = 0; i < 5; i++)
        {
            var pr = SpiFlash.DecodePrx(BitConverter.ToUInt32(block, 0x74 + i * 4));
            if (!pr.Enabled || !pr.ReadProtect) continue;
            ulong prStart = (ulong)pr.Base4k * 4096, prEnd = ((ulong)pr.Limit4k + 1) * 4096;
            if (prStart < biosOffset + biosLength && biosOffset < prEnd) return true;
        }
        return false;
    }

    private static HardwareFact Unavailable(string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(Key, Category, Name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
