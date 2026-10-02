using System.Security.Cryptography;

namespace XinSpect;

/// <summary>
/// SPI 快閃地圖與 BIOS 區雜湊（V7 WP4／A9 的第一層）。快閃內容依 Intel PCH 標準映射在實體位址
/// 4GB 頂端（基底由快閃大小推得，見 <see cref="SpiFlashMap"/>）；BIOS 區（FREG1）位元組經 MMIO
/// 讀回後取 SHA-256。誠實界線：
/// ① 雜湊＝可讀面的雜湊——PRx 讀保護（RPE）攔截的範圍會以全 F 呈現，事實文字如實標注；
/// ② 「原廠比對」需要原廠映像檔，本版未涵蓋——雜湊的當前用途是跨時間比對（快照差分）與留存，不是驗正版；
/// ③ BIOS 區大於 64 MiB 時如實拒讀（不假裝讀完）。
/// </summary>
public static class SpiFlashHashService
{
    private const string Category = "韌體安全";
    private const ulong ReadCapBytes = 64 * 1024 * 1024;
    private const int FallbackChunk = 4096; // DriverMmioReader 的契約上限；單次大讀失敗時退到這個粒度

    public static IReadOnlyList<HardwareFact> Collect(IPciConfigReader pci, IMmioReader mmio, DateTimeOffset at)
    {
        const string mapKey = "spi.flash_map", mapName = "SPI 快閃地圖（FREG 推導）";
        const string hashKey = "spi.bios_hash", hashName = "BIOS 區雜湊（SHA-256）";
        const string mapSource = "SPIBAR FREG0-5＋PCH 4GB 頂端映射慣例";

        var access = SpiFlashService.ReadController(pci, mmio, out var availability, out string error);
        if (access is null)
        {
            var a = Unavailable(mapKey, mapName, mapSource, at, availability, error);
            var b = Unavailable(hashKey, hashName, "記憶體映射快閃", at, availability, error);
            return [a, b];
        }

        var fregs = Enumerable.Range(0, 6).Select(i => BitConverter.ToUInt32(access.Block, 0x54 + i * 4)).ToArray();
        var map = SpiFlashMap.Decode(fregs);
        if (map is null)
        {
            return
            [
                Unavailable(mapKey, mapName, mapSource, at, FactAvailability.NotApplicable,
                    "FREG0-5 全空或範圍異常——無從推導快閃大小與映射基底，不猜"),
                Unavailable(hashKey, hashName, "記憶體映射快閃", at, FactAvailability.NotApplicable,
                    "快閃地圖無法推導，BIOS 區無從定址"),
            ];
        }

        var mapFact = new HardwareFact(mapKey, Category, mapName,
            $"快閃 {SizeText(map.FlashSizeBytes)}、映射基底 0x{map.MappedBase:X8}" +
            (map.BiosOffsetBytes is { } off && map.BiosLengthBytes is { } len
                ? $"、BIOS 區快閃位移 0x{off:X}-0x{off + len - 1:X}（{SizeText(len)}）"
                : "、BIOS 區未配置"),
            "", mapSource, FactTrustLevel.Measured, false, at, map.FlashSizeBytes);

        if (map.BiosOffsetBytes is not { } biosOffset || map.BiosLengthBytes is not { } biosLength)
            return [mapFact, Unavailable(hashKey, hashName, "記憶體映射快閃", at, FactAvailability.NotApplicable,
                "BIOS 區（FREG1）未配置或範圍異常，無可雜湊範圍")];

        if (biosLength > ReadCapBytes)
            return [mapFact, Unavailable(hashKey, hashName, "記憶體映射快閃", at, FactAvailability.NotSupported,
                $"BIOS 區 {SizeText(biosLength)} 超出本版讀取上限（{SizeText(ReadCapBytes)}）——如實拒讀，不假裝讀完")];

        var (bytes, chunked, readError) = ReadRange(mmio, map.MappedBase + biosOffset, biosLength);
        if (bytes is null)
            return [mapFact, Unavailable(hashKey, hashName, "記憶體映射快閃", at, FactAvailability.ReadError,
                $"BIOS 區讀取失敗：{readError}")];

        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var notes = new List<string>();
        if (chunked)
            notes.Add($"分塊讀取（{FallbackChunk}-byte 粒度——後端單次讀取有上限）");
        int allFfPages = CountAllFfPages(bytes);
        if (allFfPages > 0)
            notes.Add($"含 {allFfPages} 個全 F 4KB 頁（擦除區或讀保護攔截——雜湊僅代表可讀面）");
        if (HasReadProtectedOverlap(access.Block, biosOffset, biosLength))
            notes.Add("BIOS 區有 PRx 讀保護（RPE）範圍重疊——被擋內容不可讀，雜湊不含");

        return [mapFact, new HardwareFact(hashKey, Category, hashName,
            $"SHA-256={hash}（BIOS 區 {SizeText(biosLength)}{(notes.Count > 0 ? "；" + string.Join("；", notes) : "")}）",
            "", $"記憶體映射快閃 0x{map.MappedBase + biosOffset:X8} 起", FactTrustLevel.Measured, false, at)];
    }

    /// <summary>讀一段實體位址：先試單次大讀（WinRing0 可行），失敗退 4KB 分塊（相容契約上限 4096 的後端）。chunked 標示實際走的路徑。</summary>
    private static (byte[]? Data, bool Chunked, string? Error) ReadRange(IMmioReader mmio, ulong address, ulong length)
    {
        if (length <= int.MaxValue)
        {
            var one = mmio.ReadBlock(address, (int)length);
            if (one is not null) return (one, false, null);
            if (length <= FallbackChunk) return (null, false, mmio.LastFailReason ?? "讀取失敗");
        }

        var buffer = new byte[length];
        ulong done = 0;
        while (done < length)
        {
            int chunk = (int)Math.Min(FallbackChunk, length - done);
            var part = mmio.ReadBlock(address + done, chunk);
            if (part is null)
                return (null, false, $"{mmio.LastFailReason ?? "讀取失敗"}——中斷於快閃位移 0x{address + done:X}（已讀 {done}/{length}）");
            Buffer.BlockCopy(part, 0, buffer, (int)done, chunk);
            done += (ulong)chunk;
        }
        return (buffer, true, null);
    }

    private static int CountAllFfPages(byte[] bytes)
    {
        int pages = 0;
        for (int page = 0; page + 4096 <= bytes.Length; page += 4096)
        {
            bool allFf = true;
            for (int i = page; i < page + 4096; i++)
                if (bytes[i] != 0xFF) { allFf = false; break; }
            if (allFf) pages++;
        }
        return pages;
    }

    /// <summary>PRx（讀保護 RPE）範圍是否與 BIOS 區重疊——重疊時雜湊不是完整內容的雜湊，必須標注。</summary>
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

    private static string SizeText(ulong bytes) =>
        bytes >= (1UL << 20) ? $"{bytes >> 20} MiB" : $"{bytes >> 10} KiB";

    private static HardwareFact Unavailable(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
