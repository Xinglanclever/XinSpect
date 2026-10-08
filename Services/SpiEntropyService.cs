using System.Text;

namespace XinSpect;

/// <summary>一個快閃區域的熵摘要（對應 FREG0-5 之一）。</summary>
/// <param name="RegionIndex">區域索引（0 描述符、1 BIOS、2 ME、3 GbE、4 平台資料、5 EC）。</param>
/// <param name="RegionName">區域名稱。</param>
/// <param name="FlashOffset">該區在快閃內的起始位移。</param>
/// <param name="Length">區長（位元組）。</param>
/// <param name="Summary">逐塊熵的摘要。</param>
/// <param name="ReadableFraction">可讀位元組的比例（讀保護 RPE 攔截的範圍會以全 F 呈現，見服務層說明）。</param>
public sealed record RegionEntropy(
    int RegionIndex, string RegionName, ulong FlashOffset, ulong Length,
    EntropySummary Summary, double ReadableFraction);

/// <summary>熵圖卡片的一列：區域名稱、區間與熵值說明、組成比例。純顯示用，字串皆為繁中原文交由語言層翻譯。</summary>
/// <param name="RegionName">區域名稱。</param>
/// <param name="Detail">區間與平均熵的說明（含方法與數字，供核對）。</param>
/// <param name="Composition">內容組成（高熵／低熵／抹除的塊數）。</param>
public sealed record EntropyRegionRow(string RegionName, string Detail, string Composition);

/// <summary>
/// SPI 快閃內容的熵圖（韌體鑑識第三層）：唯讀讀回快閃內容，逐 4 KiB 塊算 Shannon 熵，
/// 依 FREG 區域切分後給出「這顆快閃的內容組成」。
/// </summary>
/// <remarks>
/// <para>
/// <b>這個功能回答什麼、不回答什麼：</b>它把快閃內容按區域與熵值分類，讓使用者看出
/// 「哪裡是壓縮／加密的資料、哪裡是空白、哪裡高度規律」。<b>它不判斷內容好壞、不判斷
/// 是否原廠、更不判斷是否被篡改</b>——原廠 UEFI 韌體本來就有大量壓縮區段，高熵完全正常。
/// 熵圖的用途是與 BIOS 區雜湊、原廠映像比對放在一起看，不是單獨下判決。
/// </para>
/// <para>
/// <b>讀保護的誠實界線：</b>PRx 的讀保護（RPE）範圍讀出來會是全 F，這在本分析裡會被
/// 判為「抹除區」——但那是<b>讀不到</b>而不是<b>沒內容</b>。因此服務層會檢查 BIOS 區是否
/// 與 RPE 範圍重疊，重疊時在事實文字裡明說「抹除區可能包含讀保護攔截」，
/// 不讓使用者把「被擋住的內容」誤讀成「空的」。
/// </para>
/// <para>
/// <b>成本：</b>整顆快閃（常見 16–32 MiB）需完整讀回一次，且只在深層存取（MMIO 後端）
/// 可用時才執行；讀不到就三態標示，不猜。
/// </para>
/// </remarks>
public static class SpiEntropyService
{
    private const string Category = "韌體安全";
    private const string Key = "spi.entropy_map";
    private const string Name = "SPI 快閃熵圖（內容組成）";
    private const string Source = "記憶體映射快閃（唯讀）÷ FREG 區域";

    /// <summary>本版讀取上限：超過就如實拒讀，不假裝讀完。</summary>
    private const ulong ReadCapBytes = 64 * 1024 * 1024;

    /// <summary>熵圖的事實鍵（供對帳規則與查詢語言引用）。</summary>
    public const string FactKey = Key;

    public static IReadOnlyList<HardwareFact> Collect(IPciConfigReader pci, IMmioReader mmio, DateTimeOffset at)
    {
        var access = SpiFlashService.ReadController(pci, mmio, out var availability, out string error);
        if (access is null)
            return [Unavailable(at, availability, error)];

        var fregs = Enumerable.Range(0, 6).Select(i => BitConverter.ToUInt32(access.Block, 0x54 + i * 4)).ToArray();
        var map = SpiFlashMap.Decode(fregs);
        if (map is null)
            return [Unavailable(at, FactAvailability.NotApplicable,
                "FREG0-5 全空或範圍異常——無從推導快閃大小與映射基底，不猜")];

        if (map.FlashSizeBytes > ReadCapBytes)
            return [Unavailable(at, FactAvailability.NotSupported,
                $"快閃 {SizeText(map.FlashSizeBytes)} 超出本版讀取上限（{SizeText(ReadCapBytes)}）——如實拒讀，不假裝讀完")];

        var (bytes, chunked, readError) = SpiFlashHashService.ReadRange(mmio, map.MappedBase, map.FlashSizeBytes);
        if (bytes is null)
            return [Unavailable(at, FactAvailability.ReadError, $"快閃讀取失敗：{readError}")];

        var blocks = EntropyMap.Analyze(bytes);
        if (blocks.Count == 0)
            return [Unavailable(at, FactAvailability.ReadError,
                $"快閃 {SizeText(map.FlashSizeBytes)} 不足一個分析區塊（{EntropyMap.DefaultBlockSize} 位元組），無從分析")];

        var overall = EntropyMap.Summarize(blocks);
        var regions = AnalyzeRegions(fregs, bytes, blocks);
        bool rpeOverlap = HasAnyReadProtection(access.Block);

        var text = new StringBuilder();
        text.Append($"全區 {SizeText(map.FlashSizeBytes)}：{blocks.Count} 塊")
            .Append($"（高熵 {overall.HighEntropyBlocks}、中 {overall.MediumEntropyBlocks}、")
            .Append($"低 {overall.LowEntropyBlocks}、抹除 {overall.ErasedBlocks}），")
            .Append($"平均熵 {overall.MeanEntropyBitsPerByte:0.00} bits/byte");

        if (regions.Count > 0)
        {
            text.Append("。分區：");
            text.Append(string.Join("；", regions.Select(r =>
                $"{r.RegionName} 平均 {r.Summary.MeanEntropyBitsPerByte:0.00}" +
                $"（抹除 {r.Summary.ErasedBlocks}/{r.Summary.Blocks} 塊）")));
        }

        if (chunked)
            text.Append($"。分塊讀取（{4096}-byte 粒度）");
        text.Append(rpeOverlap
            ? "。注意：BIOS 區有 PRx 讀保護（RPE）重疊，被擋範圍讀出全 F、會被算成抹除區——那是讀不到而非沒內容"
            : "。內容組成僅描述位元組分布，不判斷好壞或是否原廠；原廠韌體含壓縮區段為常態");

        return
        [
            new HardwareFact(Key, Category, Name, text.ToString(), "",
                Source, FactTrustLevel.Measured, false, at, overall.MeanEntropyBitsPerByte),
        ];
    }

    /// <summary>依 FREG 區域切分後各自的熵摘要；區域計算時只取與該區重疊的區塊。</summary>
    internal static IReadOnlyList<RegionEntropy> AnalyzeRegions(uint[] fregs, byte[] bytes, IReadOnlyList<EntropyBlock> blocks)
    {
        string[] names = ["描述符", "BIOS", "Intel ME", "GbE", "平台資料", "EC"];
        var result = new List<RegionEntropy>();
        for (int i = 0; i < fregs.Length; i++)
        {
            var d = SpiFlash.DecodeFreg(fregs[i]);
            if (d.Empty || d.Limit4k < d.Base4k) continue;
            ulong start = (ulong)d.Base4k * 4096;
            ulong length = ((ulong)d.Limit4k - d.Base4k + 1) * 4096;
            if (start >= (ulong)bytes.Length) continue;
            if (start + length > (ulong)bytes.Length) length = (ulong)bytes.Length - start;
            if (length == 0) continue;

            var inRange = blocks
                .Where(b => (ulong)b.Offset >= start && (ulong)b.Offset + (ulong)b.Length <= start + length)
                .ToList();
            if (inRange.Count == 0) continue;

            // 可讀比例＝非全 F 區塊的比重。全 F 可能是抹除也可能是讀保護攔截，
            // 兩者在這一層無法區分——因此這個數字只叫「可讀比例」，不做進一步推論。
            double readable = (double)inRange.Count(b => !b.IsErased) / inRange.Count;
            result.Add(new RegionEntropy(
                i, names[i], start, length, EntropyMap.Summarize(inRange), readable));
        }
        return result;
    }

    /// <summary>
    /// 供 UI 顯示的分區明細：重新讀一次快閃會很貴，因此這裡只在<i>呼叫端已經有內容快取</i>的前提下使用；
    /// 讀不到時回空清單，由呼叫端自行標示原因（不在這裡臆測）。
    /// </summary>
    public static IReadOnlyList<EntropyRegionRow> DescribeRegions(IPciConfigReader pci, IMmioReader mmio)
    {
        var access = SpiFlashService.ReadController(pci, mmio, out _, out _);
        if (access is null) return [];

        var fregs = Enumerable.Range(0, 6).Select(i => BitConverter.ToUInt32(access.Block, 0x54 + i * 4)).ToArray();
        var map = SpiFlashMap.Decode(fregs);
        if (map is null || map.FlashSizeBytes > ReadCapBytes) return [];

        var (bytes, _, _) = SpiFlashHashService.ReadRange(mmio, map.MappedBase, map.FlashSizeBytes);
        if (bytes is null) return [];

        var blocks = EntropyMap.Analyze(bytes);
        var regions = AnalyzeRegions(fregs, bytes, blocks);
        var rows = new List<EntropyRegionRow>(regions.Count);
        foreach (var r in regions)
        {
            var s = r.Summary;
            string detail = $"位移 0x{r.FlashOffset:X}–0x{r.FlashOffset + r.Length - 1:X}（{SizeText(r.Length)}）"
                          + $" ・ 平均 {s.MeanEntropyBitsPerByte:0.00} bits/byte ・ {s.Blocks} 塊";
            var parts = new List<string>();
            if (s.ErasedBlocks > 0) parts.Add($"抹除 {s.ErasedBlocks}");
            if (s.HighEntropyBlocks > 0) parts.Add($"高熵 {s.HighEntropyBlocks}");
            if (s.LowEntropyBlocks > 0) parts.Add($"低熵 {s.LowEntropyBlocks}");
            if (s.MediumEntropyBlocks > 0) parts.Add($"中 {s.MediumEntropyBlocks}");
            rows.Add(new EntropyRegionRow(r.RegionName, detail, string.Join(" ・ ", parts)));
        }
        return rows;
    }

    // 是否有任何啟用的 PRx 讀保護（RPE）範圍——重疊判定在事實文字裡如實標注。
    private static bool HasAnyReadProtection(byte[] block)
    {
        for (int i = 0; i < 5; i++)
        {
            var pr = SpiFlash.DecodePrx(BitConverter.ToUInt32(block, 0x74 + i * 4));
            if (pr.Enabled && pr.ReadProtect) return true;
        }
        return false;
    }

    private static string SizeText(ulong bytes) =>
        bytes >= (1UL << 20) ? $"{bytes >> 20} MiB" : $"{bytes >> 10} KiB";

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason) =>
        new(Key, Category, Name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
