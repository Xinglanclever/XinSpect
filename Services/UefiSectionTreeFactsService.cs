using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// UEFI 映像 FV／FFS／區段樹（ULTIMATE-1000 階段二 P2-034）。
///
/// <para>
/// <b>這一項不新增解碼器——它把既有的三層結構彙總成一把可讀的鍵。</b>
/// <see cref="UefiFv"/> 早就解出 FV → FFS 檔案 → 區段三層，
/// 而既有的 <c>ufv.fv.{i}.summary</c> 只印到第二層（檔案），
/// <see cref="UefiFvFile.Sections"/> 那一層沒有被彙總過。
/// </para>
///
/// <para>
/// <b>為什麼是一把固定鍵而不是動態家族：</b>
/// 逐項導覽需要「每個 FV 每個檔案一把鍵」，那會是第 24 個動態家族
/// ——而 <c>FactKeyCatalogTests</c> 的 <c>FamilyCap</c> 就是 23，
/// 它的訊息寫著「動態面膨脹到這個程度，先回頭數一數哪些其實可以靜態枚舉」。
/// <b>那句訊息是在說「別再加了」，而這次我聽進去了。</b>
/// </para>
///
/// <para>
/// 所以本服務回答的是：<b>這棵樹有多大、由什麼組成</b>——
/// 三層計數與區段型別分佈。而逐項導覽由既有的 <c>ufv.fv.{i}.summary</c>
/// 與 UI 的 <c>DescribeRows</c> 負責（本項順帶把它們補上區段那一層）。
/// </para>
///
/// <para>
/// <b>界線：計數不等於完整。</b>壓縮區段裡的巢狀 FV 不在本版範圍
/// （不遞迴、不解壓），所以這是「<b>看得見的三層有多大</b>」，
/// 不是「這顆韌體有幾個區段」。
/// </para>
/// </summary>
public static class UefiSectionTreeFactsService
{
    private const string Category = "韌體";
    private const string Source = "UEFI PI FV/FFS/區段樹（既有的 UefiFv 解碼器）";

    public const string TreeKey = "uefi.section.tree";
    public const string DepthKey = "uefi.section.depth";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<UefiFvInfo>? fvs)
    {
        if (fvs is null)
            return Na(at, "沒有解碼結果——本服務不自己去讀 BIOS 區"
                + "（那條路徑有它自己的可及性事實）", FactAvailability.NotSupported);

        if (fvs.Count == 0)
            return
            [
                new HardwareFact(TreeKey, Category, "映像結構樹", "", "", Source,
                    FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                    "**「沒有 FV」與「FV 裡沒有區段」是不同的斷言**："
                    + "前者代表整張映像可能是壓縮的或非 FV 佈局，那時根本沒有樹可以攤開"),
                Depth(at, 0, 0, 0),
            ];

        int fileCount = fvs.Sum(f => f.Files.Count);
        int sectionCount = fvs.Sum(f => f.Files.Sum(x => x.Sections.Count));

        var byType = fvs.SelectMany(f => f.Files)
            .SelectMany(x => x.Sections)
            .GroupBy(s => s.TypeName)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        // 巢狀與未展開的數量——**它們是這棵樹的「看不到的部分」**。
        int nested = fvs.SelectMany(f => f.Files)
            .SelectMany(x => x.Sections)
            .Count(s => s.TypeName is "FIRMWARE_VOLUME_IMAGE" or "COMPRESSION" or "GUID_DEFINED");

        string detail = string.Join("、", byType.Take(10).Select(g => $"{g.Key}×{g.Count()}"))
            + (byType.Count > 10 ? $"；等 {byType.Count} 種型別" : "");

        return
        [
            new HardwareFact(TreeKey, Category, "映像結構樹",
                $"{fvs.Count} 個 FV、{fileCount} 個檔案、{sectionCount} 個區段"
                + (sectionCount > 0 ? $"：{detail}" : ""),
                "個", Source, FactTrustLevel.Measured, false, at, sectionCount,
                FactAvailability.Present,
                "**計數不等於完整。**壓縮區段裡的巢狀 FV 不在本版範圍"
                + "（不遞迴、不解壓），所以這是「**看得見的三層有多大**」，"
                + "不是「這顆韌體有幾個區段」"
                + (nested > 0
                    ? $"——而這一層裡有 {nested} 個區段本身就是巢狀容器"
                      + "（壓縮／GUID_DEFINED／FV 映像），**它們的內容沒有被攤開**"
                    : "")),
            Depth(at, fvs.Count, fileCount, sectionCount),
        ];
    }

    private static HardwareFact Depth(DateTimeOffset at, int fv, int file, int section)
    {
        string shape = fv == 0 ? "（空）"
            : $"FV {fv} → 檔案 {(file == 0 ? 0 : (double)file / fv):0.#}/FV"
              + $" → 區段 {(file == 0 ? 0 : (double)section / file):0.#}/檔案";

        return new HardwareFact(DepthKey, Category, "映像結構樹的形狀", shape, "", Source,
            FactTrustLevel.Derived, false, at,
            // **沒有 FV 時這個數字是空的，不是 0。**
            // 第一版我傳了 section（＝0），而測試當場指出來——
            // 「這棵樹有 0 個區段」與「沒有樹可以數」是不同的斷言，
            // 而這正是本服務在 TreeKey 上已經處理過一次、卻在這裡漏掉的事。
            fv == 0 ? null : section,
            FactAvailability.Present,
            "**這個鍵回答的是「樹的形狀」而不是「樹的內容」**："
            + "平均每個 FV 有幾個檔案、每個檔案有幾個區段。"
            + "**平均值低不代表健康、高不代表有問題**——"
            + "它只是讓人一眼看出這棵樹是扁的還是深的。"
            + "**而沒有 FV 時這裡的數字留空而不是 0**："
            + "「這棵樹有 0 個區段」與「沒有樹可以數」是不同的斷言");
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [TreeKey, DepthKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
