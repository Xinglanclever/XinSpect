using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// FV／FFS 逐項分解的對照陳述（U10-FW-0050，鍵 <c>fw.fv.cross</c>）。
///
/// <para>
/// <b>判定文檔說的缺口：</b>既有的 <see cref="UefiFvFactsService"/> 把 BIOS 區解成
/// FV → FFS 檔案 → 區段的樹，但「<b>列了哪些 FV、各含多少 FFS 檔、哪些分解過、哪些只列名</b>」
/// 這張對照表沒有以事實鍵的形式攤開——<c>ufv.fv.{i}.summary</c> 有逐 FV 的摘要、
/// <c>fw.fv</c>（<see cref="FirmwareDeepFactsService"/>）有空間統計，而「分解面」本身沒有陳述。
/// </para>
///
/// <para>
/// <b>「分解過」與「只列名」在既有解碼器裡各是什麼（不新造概念）：</b>
/// 每個非填充且資料有效的 FFS 檔案，其<b>頂層區段都已解碼</b>（<see cref="UefiFvFile.Sections"/>）——
/// 那是「分解過」；而 <b>GUID_DEFINED／COMPRESSION 區段的內容只列出不解壓</b>、
/// <b>巢狀 FV 映像只列出不遞迴</b>——那是本專案明說的界線（引入解壓引擎是另一件事），不是解析失敗。
/// 這把鍵把兩個面分別計數，讓「沒有展開」有明確的歸屬。
/// </para>
///
/// <para>
/// <b>沿用既有讀取，不新增第二份讀取</b>（同型教訓：兩份讀取＝兩個答案）——
/// 輸入就是 <c>UefiFv.DecodeFvs</c> 已經算出來的那份 <see cref="UefiFvInfo"/> 清單，
/// 本服務不碰 SPI 快閃、不重新解析。
/// </para>
///
/// <para>
/// <b>只出單一固定鍵</b>：FV 數隨平台而變（成員不固定），逐 FV 明細已有
/// <c>ufv.fv.{i}.summary</c> 家族在列——這裡不再開第二個逐 FV 家族重複同樣的內容。
/// </para>
/// </summary>
public static class FirmwareFvCrossFactsService
{
    private const string Category = "韌體安全";
    private const string Source = "UEFI PI FV/FFS 解析（既有的 UefiFv.DecodeFvs 結果，不重讀快閃）";

    public const string CrossKey = "fw.fv.cross";

    public static HardwareFact Collect(DateTimeOffset at, IReadOnlyList<UefiFvInfo>? fvs)
    {
        if (fvs is null)
            return Unavailable(at, FactAvailability.NotSupported,
                "沒有 FV 走訪結果——本服務沿用 UefiFvFactsService 那次解析的輸出，**不自己讀 SPI 快閃**"
                + "（同一份讀取只許一個答案）；連同 ufv.* 那組一起缺源");

        if (fvs.Count == 0)
            return Unavailable(at, FactAvailability.NotApplicable,
                "**BIOS 區內沒有可辨識的 _FVH 標記**——這是解析後的答案（可能整顆壓縮或非 FV 佈局），"
                + "與「沒有走訪」是不同的斷言；不猜");

        int totalFiles = fvs.Sum(f => f.FileCount);
        int named = fvs.Sum(f => f.Files.Count(x => x.UiName is not null));
        int guidOnly = totalFiles - named;
        int withCompression = fvs.Sum(f => f.Files.Count(x =>
            x.Sections.Any(s => s.TypeName is "COMPRESSION" or "GUID_DEFINED")));
        int nestedFv = fvs.Sum(f => f.Files.Count(x =>
            x.Sections.Any(s => s.TypeName == "FIRMWARE_VOLUME_IMAGE")));
        int checksumMismatch = fvs.Sum(f => f.ChecksumMismatchCount);
        int skipped = fvs.Sum(f => f.SkippedCount);

        var perFv = fvs.Take(8).Select(f =>
            $"FV#{f.Index}：檔案 {f.FileCount}"
            + $"（具 UI 名 {f.Files.Count(x => x.UiName is not null)}、僅 GUID {f.Files.Count(x => x.UiName is null)}"
            + $"、含未展開壓縮 {f.Files.Count(x => x.Sections.Any(s => s.TypeName is "COMPRESSION" or "GUID_DEFINED"))}"
            + $"、巢狀 FV 引用 {f.Files.Count(x => x.Sections.Any(s => s.TypeName == "FIRMWARE_VOLUME_IMAGE"))}）")
            .ToList();
        string perFvText = perFv.Count < fvs.Count
            ? string.Join("；", perFv) + $"；等 {fvs.Count} 個 FV"
            : string.Join("；", perFv);

        return new HardwareFact(CrossKey, Category, "FV／FFS 分解對照",
            $"{perFvText}。合計：FFS 檔案 {totalFiles} 個——**頂層區段全數分解過**；"
            + $"「只列名不解開」的有：含未展開壓縮區段的檔案 {withCompression} 個、巢狀 FV 引用 {nestedFv} 個"
            + "（**那是本專案的界線**：解壓需要壓縮引擎、遞迴需要第二輪走訪，都不是解析失敗）；"
            + (named > 0 ? $"具 UI 名 {named} 個、僅以 GUID 呈現 {guidOnly} 個" : $"全數僅以 GUID 呈現")
            + (checksumMismatch > 0 ? $"；表頭校驗和不符 {checksumMismatch} 個（如實呈現，不拒收）" : "；表頭校驗和全過")
            + (skipped > 0 ? $"；未過資料有效位元而略過 {skipped} 個" : ""),
            "檔", Source, FactTrustLevel.Measured, false, at, totalFiles,
            FactAvailability.Present, null);
    }

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason)
        => new(CrossKey, Category, "FV／FFS 分解對照", "", "檔", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason);
}
