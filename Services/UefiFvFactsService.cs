using System.Text;

namespace XinSpect;

/// <summary>UEFI FV 卡片的一列：FV 名、明細（GUID／長度／修訂／檔案數）、頂層檔案清單（有上限，不無限長）。</summary>
public sealed record UefiFvRow(string FvName, string Detail, string Files);

/// <summary>
/// UEFI 韌體磁碟區結構（FV／FFS）：唯讀讀回 SPI 快閃的 BIOS 區，依 UEFI PI 規格解析出
/// FV → FFS 檔案 → 區段的頂層樹。輸入也可以是使用者提供的映像（DecodeFvs 是純解碼器）。
/// </summary>
/// <remarks>
/// <para>
/// <b>誠實界線：</b>結構存在與否不構成對韌體真偽的判決；GUID_DEFINED／COMPRESSION 區段
/// <b>只列出、不解壓</b>——解壓需要引入解壓引擎，不是本服務的事。表頭校驗和不符合規格的
/// 檔案如實計數呈現，不拒收也不掩蓋（實測過的韌體有不符個案）。BIOS 區找不到任何
/// _FVH 標記時如實回 0 並說明可能是整顆壓縮映像，不猜。
/// </para>
/// <para>
/// <b>事實鍵的形狀：</b><c>ufv.bios.count</c> 是固定鍵（總數）；每個 FV 的摘要鍵
/// <c>ufv.fv.{i}.summary</c> 是動態鍵（FV 數隨平台而變），與 storage.reliability 的逐碟鍵同一處理。
/// </para>
/// </remarks>
public static class UefiFvFactsService
{
    private const string Category = "韌體安全";
    public const string CountKey = "ufv.bios.count";
    private const string Source = "記憶體映射快閃 BIOS 區（唯讀）÷ UEFI PI FV/FFS";

    /// <summary>本版讀取上限（BIOS 區）：超過就如實拒讀，不假裝讀完。</summary>
    private const ulong ReadCapBytes = 64 * 1024 * 1024;

    /// <summary>事實總數上限：1 總數 + 8 個 FV 摘要——極端多 FV 的平台不再展開，摘要明說「等」。</summary>
    private const int MaxFacts = 9;

    /// <summary>摘要文字裡列出的檔案數上限。</summary>
    private const int MaxFilesInText = 12;

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

        if (map.BiosOffsetBytes is not ulong biosOffset || map.BiosLengthBytes is not ulong biosLength)
            return [Unavailable(at, FactAvailability.NotApplicable,
                "BIOS 區未配置（FREG1 空）——沒有 FV 結構可談，不猜")];

        if (biosLength > ReadCapBytes)
            return [Unavailable(at, FactAvailability.NotSupported,
                $"BIOS 區 {SizeText(biosLength)} 超出本版讀取上限（{SizeText(ReadCapBytes)}）——如實拒讀，不假裝讀完")];

        var (bytes, chunked, readError) = SpiFlashHashService.ReadRange(mmio, map.MappedBase + biosOffset, biosLength);
        if (bytes is null)
            return [Unavailable(at, FactAvailability.ReadError, $"BIOS 區讀取失敗：{readError}")];

        return CollectFromBytes(bytes, chunked, at);
    }

    /// <summary>從已讀回的 BIOS 區內容產出事實；讀取通路與解析分離，測試不必碰真驅動。</summary>
    internal static IReadOnlyList<HardwareFact> CollectFromBytes(byte[] biosBytes, bool chunked, DateTimeOffset at)
    {
        var fvs = UefiFv.DecodeFvs(biosBytes);
        var facts = new List<HardwareFact>(Math.Min(fvs.Count, MaxFacts - 1) + 1)
        {
            CountFact(fvs, biosBytes, chunked, at),
        };
        foreach (var fv in fvs.Take(MaxFacts - 1))
            facts.Add(new HardwareFact(
                $"ufv.fv.{fv.Index}.summary", Category, $"UEFI FV #{fv.Index}", SummaryText(fv), "",
                Source, FactTrustLevel.Measured, false, at));
        return facts;
    }

    private static HardwareFact CountFact(IReadOnlyList<UefiFvInfo> fvs, byte[] biosBytes, bool chunked, DateTimeOffset at)
    {
        var text = new StringBuilder();
        text.Append($"BIOS 區 {SizeText((ulong)biosBytes.Length)} 內辨識出 {fvs.Count} 個韌體磁碟區（FV）");
        if (fvs.Count == 0)
        {
            text.Append("。未找到可辨識的 _FVH 標記——BIOS 區可能是整顆壓縮映像或非 FV 佈局；如實回 0，不猜");
        }
        else
        {
            int mismatch = fvs.Sum(f => f.ChecksumMismatchCount);
            text.Append(mismatch > 0
                ? $"。表頭校驗和：{fvs.Count - fvs.Count(f => f.ChecksumMismatchCount > 0)} 個 FV 全過、{fvs.Count(f => f.ChecksumMismatchCount > 0)} 個 FV 含不符檔案（共 {mismatch} 個，如實呈現不拒收）"
                : "。表頭校驗和全過");
        }
        if (chunked) text.Append("。分塊讀取（4096-byte 粒度）");
        text.Append("。結構存在與否不構成對韌體真偽的判決；壓縮區段只列出不解壓");

        return new HardwareFact(CountKey, Category, "UEFI 韌體磁碟區（FV）總數", text.ToString(), "",
            Source, FactTrustLevel.Measured, false, at, fvs.Count);
    }

    private static string SummaryText(UefiFvInfo fv)
    {
        var text = new StringBuilder();
        text.Append($"GUID {fv.FileSystemGuid}、長 {SizeText(fv.Length)}、修訂 {fv.Revision}、頂層 FFS 檔案 {fv.FileCount} 個");
        if (fv.SkippedCount > 0) text.Append($"（另略過未過資料有效位元的 {fv.SkippedCount} 個）");
        text.Append(fv.HeaderChecksumOk ? "、FV 表頭校驗和過" : "、FV 表頭校驗和不符");
        if (fv.ExtHeader) text.Append("、含延伸標頭");
        if (fv.Files.Count > 0)
        {
            text.Append("。檔案：");
            text.Append(string.Join("、", fv.Files.Take(MaxFilesInText).Select(FileLabel)));
            if (fv.Files.Count > MaxFilesInText) text.Append($" 等 {fv.Files.Count} 個");
        }
        return text.ToString();
    }

    private static string FileLabel(UefiFvFile f)
    {
        string name = f.UiName ?? f.Guid[..8].ToUpperInvariant();
        return $"{name}（{f.TypeName}）";
    }

    /// <summary>供 UI 顯示的 FV 明細列；讀不到時回空清單，由呼叫端用事實的三態呈現原因（不在這裡臆測）。</summary>
    public static IReadOnlyList<UefiFvRow> DescribeRows(IPciConfigReader pci, IMmioReader mmio)
    {
        var access = SpiFlashService.ReadController(pci, mmio, out _, out _);
        if (access is null) return [];
        var fregs = Enumerable.Range(0, 6).Select(i => BitConverter.ToUInt32(access.Block, 0x54 + i * 4)).ToArray();
        var map = SpiFlashMap.Decode(fregs);
        if (map is null || map.BiosOffsetBytes is not ulong biosOffset || map.BiosLengthBytes is not ulong biosLength
            || biosLength > ReadCapBytes) return [];
        var (bytes, _, _) = SpiFlashHashService.ReadRange(mmio, map.MappedBase + biosOffset, biosLength);
        if (bytes is null) return [];

        var fvs = UefiFv.DecodeFvs(bytes);
        var rows = new List<UefiFvRow>(fvs.Count);
        foreach (var fv in fvs)
        {
            string detail = $"GUID {fv.FileSystemGuid} ・ 長 {SizeText(fv.Length)} ・ 修訂 {fv.Revision} ・ 檔案 {fv.FileCount}"
                + (fv.SkippedCount > 0 ? $" ・ 略過 {fv.SkippedCount}" : "")
                + (fv.ChecksumMismatchCount > 0 ? $" ・ 校驗和不符 {fv.ChecksumMismatchCount}" : "");
            string files = fv.Files.Count == 0
                ? "（無已生效檔案）"
                : string.Join("、", fv.Files.Take(16).Select(FileLabel)) + (fv.Files.Count > 16 ? $" 等 {fv.Files.Count} 個" : "");
            rows.Add(new UefiFvRow($"FV {fv.Index}", detail, files));
        }
        return rows;
    }

    private static string SizeText(ulong bytes) =>
        bytes >= (1UL << 20) ? $"{bytes >> 20} MiB" : $"{bytes >> 10} KiB";

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason) =>
        new(CountKey, Category, "UEFI 韌體磁碟區（FV）總數", "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
