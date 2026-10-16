using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 映像壓縮型別支援清單（ULTIMATE-1000 階段二 P2-038）。
///
/// <para>
/// <b>這一項不新增解碼器——它把既有解碼器認得的引擎攤開成一份可查詢的清單。</b>
/// 引擎辨識的判斷鏈本來就寫在 <see cref="UefiFv"/> 的區段解析裡
/// （<see cref="UefiFv.GuidLzmaCustomDecompress"/>、
/// <see cref="UefiFv.GuidCrc32GuidedSection"/>，其餘標「未收錄引擎」）。
/// 本服務<b>直接引用那兩個常數</b>，不另外複製一份 GUID——
/// 兩份 GUID 表就是兩處漂移的來源。
/// </para>
///
/// <para>
/// <b>界線，而這是本項最重要的一句：</b>
/// <b>「認得」不等於「能解壓」。</b>
/// 既有的區段解析是<b>只列出引擎名與資料位移、不解壓</b>——
/// 解壓需要引入解壓引擎，那是另一件事。
/// 所以 <c>uefi.comp.supported</c> 的語意是「<b>本版認得的壓縮引擎</b>」，
/// 而不是「本版能還原內容的引擎」。
/// </para>
///
/// <para>
/// <b>混淆這兩者會讓使用者以為「支援」＝「能還原內容」。</b>
/// 本服務把它們分成兩把鍵，讓那個差別在資料層就看得見。
/// </para>
/// </summary>
public static class UefiCompressionFactsService
{
    private const string Category = "韌體";
    private const string Source = "UEFI PI 區段解析（XinSpect.Decoders/UefiFv.cs 的引擎辨識判斷鏈）";

    public const string RecognizedKey = "uefi.comp.supported";
    public const string DecompressedKey = "uefi.comp.decompressed";

    /// <summary>一個壓縮引擎的條目。</summary>
    public sealed record Engine(string Guid, string Name, bool CanDecompress);

    /// <summary>
    /// 本版認得的引導引擎清單。
    /// <b>GUID 直接取自 <see cref="UefiFv"/> 的常數</b>——單一來源。
    /// </summary>
    public static IReadOnlyList<Engine> Recognized() =>
    [
        new(UefiFv.GuidLzmaCustomDecompress.ToString().ToUpperInvariant(),
            "LZMA 壓縮", CanDecompress: false),
        new(UefiFv.GuidCrc32GuidedSection.ToString().ToUpperInvariant(),
            "CRC32 引導", CanDecompress: false),
    ];

    /// <summary>
    /// 收集壓縮支援事實。<paramref name="seenEngineGuids"/> 是本次映像掃描實際遇到的引擎 GUID；
    /// 傳 null 代表沒掃過映像（那時只報靜態清單）。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyCollection<string>? seenEngineGuids = null)
    {
        var known = Recognized();
        var knownSet = known.Select(e => e.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string list = string.Join("；", known.Select(e =>
            $"{e.Name}（{e.Guid}，{(e.CanDecompress ? "可解" : "只標名不解壓")}）"));

        var facts = new List<HardwareFact>
        {
            new(RecognizedKey, Category, "本版認得的映像壓縮引擎", list, "個",
                Source, FactTrustLevel.Measured, false, at, known.Count,
                FactAvailability.Present,
                "**「認得」不等於「能解壓」**：本版認得這兩個引擎的 GUID 並標出它們的名字，"
                + "但**不解壓**它們的內容——解壓需要引入解壓引擎，那是另一件事。"
                + "所以這把鍵的語意是「本版認得的壓縮引擎」，"
                + "**不是「本版能還原內容的引擎」**"),
        };

        // 靜態清單永遠有；「實際遇到什麼」只有在掃過映像時才有。
        if (seenEngineGuids is null)
        {
            facts.Add(new HardwareFact(DecompressedKey, Category, "本次映像遇到的壓縮引擎",
                "", "個", Source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable,
                "沒有掃過映像——本服務不自己去讀 BIOS 區（那條路徑有它自己的可及性事實），"
                + "所以這一項由呼叫端把遇到的引擎 GUID 傳進來"));
            return facts;
        }

        if (seenEngineGuids.Count == 0)
        {
            facts.Add(new HardwareFact(DecompressedKey, Category, "本次映像遇到的壓縮引擎",
                "映像裡沒有 GUID_DEFINED 或 COMPRESSION 區段", "個", Source,
                FactTrustLevel.Measured, false, at, 0, FactAvailability.NotApplicable,
                "**「沒有壓縮區段」與「掃描失敗」是不同的斷言**——"
                + "這個結果來自一次成功的掃描，而那次掃描沒有遇到壓縮區段"));
            return facts;
        }

        var unknown = seenEngineGuids.Where(g => !knownSet.Contains(g)).ToList();
        string value = $"{seenEngineGuids.Count} 個：" + string.Join("、", seenEngineGuids)
            + (unknown.Count > 0 ? $"（其中 {unknown.Count} 個未收錄）" : "");

        return
        [
            facts[0],
            new HardwareFact(DecompressedKey, Category, "本次映像遇到的壓縮引擎",
                value, "個", Source, FactTrustLevel.Measured, false, at,
                seenEngineGuids.Count,
                unknown.Count > 0 ? FactAvailability.Unknown : FactAvailability.Present,
                unknown.Count > 0
                    ? $"**有 {unknown.Count} 個引擎不在本版的清單裡**——"
                      + "那不代表它們有問題，只代表本版沒有收錄它們的名字與慣例。"
                      + "既有的區段解析會如實印出「未收錄引擎 <GUID>」，"
                      + "**而不是猜一個名字**"
                    : "本次映像遇到的引擎都在本版的清單裡——"
                      + "**但那仍然不代表能解壓**：清單是「認得」，不是「能還原」"),
        ];
    }
}
