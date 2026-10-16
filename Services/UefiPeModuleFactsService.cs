using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 映像 PE32 模組清單（ULTIMATE-1000 階段二 P2-035）。
///
/// <para>
/// <b>這一項不新增 PE 解碼器——它把 FV 樹裡的 PE32 區段交給既有的
/// <see cref="PeInspect"/>。</b>那個解碼器本來是為磁碟上的驅動檔寫的，
/// 而它的輸入是「一段 PE 內容」——UEFI 映像裡的 PE32 區段正是這個
/// （PE/COFF 檔案格式、未載入）。
/// </para>
///
/// <para>
/// <b>界線，而這是本項最重要的一句：</b>
/// 規格的目標寫「列出映像內<b>驅動與應用模組</b>」，而
/// <b>PE 的子系統只分 NATIVE／GUI／CUI</b>——
/// 「NATIVE 通常對應驅動」是<b>慣例</b>，不是 PE 格式的定義。
/// 所以本服務<b>報子系統</b>，並把那個落差說出來，
/// <b>而不是直接把 NATIVE 標成「驅動」</b>。
/// </para>
///
/// <para>
/// <b>只走頂層。</b><c>DECODE_SECTIONS</c> 的巢狀結構（壓縮區段裡的 FV、
/// FV 映像區段）不在本版範圍——那需要解壓與遞迴，而兩者都有自己的界線。
/// 所以本服務報的是「<b>看得見的那一層有幾個 PE32</b>」。
/// </para>
/// </summary>
public static class UefiPeModuleFactsService
{
    private const string Category = "韌體";
    private const string Source = "UEFI PI FV/FFS/區段樹 ＋ PE/COFF 靜態解析（既有 PeInspect）";

    public const string CountKey = "uefi.pe.count";
    public const string ListKey = "uefi.pe.list";

    /// <summary>一個 PE32 模組的摘要。</summary>
    public sealed record Module(string FvIndex, string FileGuid, string? UiName,
        string Machine, string Subsystem, bool Parsed, string? Error);

    /// <summary>
    /// 從既有的解碼結果收集 PE32 模組。<paramref name="fvs"/> 是 <see cref="UefiFv.DecodeFvs"/> 的輸出，
    /// <paramref name="image"/> 是同一份輸入位元組（用來按 <see cref="UefiFvSection.DataOffset"/> 取內容）。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<UefiFvInfo>? fvs, byte[]? image)
    {
        if (fvs is null || image is null)
            return Na(at, "沒有解碼結果或原始映像——本服務不自己去讀 BIOS 區"
                + "（那條路徑有它自己的可及性事實）", FactAvailability.NotSupported);

        if (fvs.Count == 0)
            return Na(at, "沒有辨識出任何 FV——**「沒有 FV」與「FV 裡沒有 PE32」是不同的斷言**："
                + "前者代表整張映像可能是壓縮的或非 FV 佈局，那時根本沒有區段樹可以走",
                FactAvailability.NotApplicable);

        var modules = new List<Module>();
        int sectionsSeen = 0;

        foreach (var fv in fvs)
        {
            foreach (var file in fv.Files)
            {
                foreach (var sect in file.Sections)
                {
                    sectionsSeen++;
                    if (sect.TypeName != "PE32") continue;

                    // DataOffset 是資料在整份輸入裡的位移；Size 含表頭，
                    // 所以資料長度是 Size − 表頭（PE32 的表頭是 4 bytes）。
                    long start = (long)sect.DataOffset;
                    long payload = (long)sect.Size - 4;
                    if (start < 0 || payload <= 0 || start + payload > image.Length)
                    {
                        modules.Add(new Module($"#{fv.Index}", file.Guid, file.UiName,
                            "?", "?", false, "區段範圍超出映像——如實拒解，不猜"));
                        continue;
                    }

                    var bytes = new byte[payload];
                    Array.Copy(image, start, bytes, 0, payload);

                    var r = PeInspect.Inspect(bytes);
                    modules.Add(new Module($"#{fv.Index}", file.Guid, file.UiName,
                        r.Parsed ? r.MachineText : "?", r.Parsed ? r.SubsystemText : "?",
                        r.Parsed, r.ParseError));
                }
            }
        }

        return
        [
            CountFact(at, modules, sectionsSeen),
            ListFact(at, modules),
        ];
    }

    private static HardwareFact CountFact(DateTimeOffset at, List<Module> modules, int sectionsSeen)
    {
        int parsed = modules.Count(m => m.Parsed);
        int native = modules.Count(m => m.Parsed && m.Subsystem.Contains("NATIVE"));

        if (modules.Count == 0)
            return new HardwareFact(CountKey, Category, "映像內的 PE32 模組",
                $"走過 {sectionsSeen} 個區段，沒有 PE32", "個", Source,
                FactTrustLevel.Measured, false, at, 0, FactAvailability.NotApplicable,
                "**「沒有 PE32 區段」與「走得進不去」是不同的斷言**——"
                + "這個結果來自一次成功的走訪，而那次走訪沒有遇到 PE32。"
                + "另一種可能是整個映像被壓縮成單一區段，那時確實看不到 PE32");

        string value = $"{modules.Count} 個 PE32"
            + (parsed < modules.Count ? $"（{modules.Count - parsed} 個解析失敗）" : "")
            + $"，其中子系統為 NATIVE 的 {native} 個";

        return new HardwareFact(CountKey, Category, "映像內的 PE32 模組", value, "個", Source,
            FactTrustLevel.Measured, false, at, modules.Count,
            parsed == modules.Count ? FactAvailability.Present : FactAvailability.Unknown,
            parsed < modules.Count
                ? $"有 {modules.Count - parsed} 個 PE32 解析失敗——**那不代表它們不是 PE**，"
                  + "只代表本版解不開；失敗原因逐個列在清單裡"
                : "**規格說的是「驅動與應用模組」，而 PE 的子系統只分 NATIVE／GUI／CUI。**"
                  + "「NATIVE 通常是驅動」是慣例不是定義——"
                  + "所以本服務報子系統，不直接把 NATIVE 標成「驅動」");
    }

    private static HardwareFact ListFact(DateTimeOffset at, List<Module> modules)
    {
        if (modules.Count == 0)
            return new HardwareFact(ListKey, Category, "PE32 模組清單", "", "", Source,
                FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                "沒有 PE32 模組可列");

        const int cap = 20;
        var shown = modules.Take(cap).Select(m =>
        {
            string name = m.UiName is { Length: > 0 } n ? n : m.FileGuid;
            string tail = m.Parsed ? $"{m.Machine}／{m.Subsystem}" : $"解析失敗：{m.Error}";
            return $"{name}（FV{m.FvIndex}，{tail}）";
        });

        string value = string.Join("；", shown)
            + (modules.Count > cap ? $"；……等共 {modules.Count} 個（清單上限 {cap}）" : "");

        return new HardwareFact(ListKey, Category, "PE32 模組清單", value, "", Source,
            FactTrustLevel.Measured, false, at, modules.Count, FactAvailability.Present,
            "**只走頂層。**巢狀結構（壓縮區段裡的 FV、FV 映像區段）不在本版範圍"
            + "——那需要解壓與遞迴，而兩者都有自己的界線。"
            + "所以這是「**看得見的那一層**有幾個 PE32」，"
            + "不是「這顆韌體有幾個 PE32」。"
            + (modules.Count > cap ? $"（清單有上限，超過的部分只計數不列出——避免事實值無限長）" : ""));
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [CountKey, ListKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
