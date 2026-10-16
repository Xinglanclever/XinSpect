using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 量測開機日誌的摘要與事件型別直方圖（ULTIMATE-1000 階段二 P2-071／P2-072）。
///
/// <para>
/// <b>這一項復用既有的 <see cref="Tpm2.WalkTcgLog"/>，不新增解碼器。</b>
/// 既有的 <c>tpm.tcg_log</c> 只說「N 個事件（PCR 覆蓋 …）」——
/// 它把事件數與 PCR 覆蓋講完了，但<b>沒有說那些事件是什麼型別</b>。
/// </para>
///
/// <para>
/// <b>而這裡有一個重要的空白，它要被說出來而不是被填補：</b>
/// TCG 的事件型別是 <c>EV_*</c> 常數（u32），而<b>本專案沒有任何地方收錄那張表</b>。
/// 我知道其中幾個編號，但<b>「我記得」不是出處</b>——
/// 而依本專案在 FIT 的 Key Manifest 上學到的教訓：
/// <b>填錯的編號會讓一個不存在的東西看起來存在，那比「未收錄」更糟。</b>
/// </para>
///
/// <para>
/// 所以直方圖<b>只列型別編號與事件數，不命名</b>。
/// 它仍然回答了「事件型別的分佈」這個問題——<b>只是不假裝我知道每個編號的意思。</b>
/// </para>
///
/// <para>
/// <b>界線：走訪截斷時，這裡的計數是「走到哪算到哪」。</b>
/// 那與「log 裡只有這些事件」是不同的斷言，而它必須出現在措辭裡。
/// </para>
/// </summary>
public static class TpmLogFactsService
{
    private const string Category = "信賴根";
    private const string Source = "TCG 事件 log（既有的 Tpm2.WalkTcgLog）";

    public const string SummaryKey = "tpm.log.summary";
    public const string HistogramKey = "tpm.log.histogram";

    /// <summary>直方圖最多列幾種型別——超過就只報種類數。</summary>
    private const int MaxTypesInText = 12;

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Tpm2.TcgLogWalk? walk)
    {
        if (walk is null)
            return Na(at, "沒有 log 走訪結果——本服務不自己去讀 TPM"
                + "（那條路徑有它自己的可及性事實：tpm.tcg_log）", FactAvailability.NotSupported);

        if (walk.Events.Count == 0)
            return
            [
                new HardwareFact(SummaryKey, Category, "量測開機日誌摘要", "", "筆", Source,
                    FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                    "**「log 裡沒有可解析事件」與「沒有 log」是不同的斷言**："
                    + "前者代表 log 讀到了但走訪不出任何東西（格式不符或全損毀）"
                    + (walk.Truncated ? "，而這次的走訪是**提前結束**的——那更可能是損毀" : "")),
                new HardwareFact(HistogramKey, Category, "開機事件型別直方圖", "", "種", Source,
                    FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                    "沒有事件就沒有分佈可言——**不填 0**，因為「0 種型別」與「數不出來」不同"),
            ];

        var byType = walk.Events.GroupBy(e => e.EventType)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToList();
        var pcrs = walk.Events.Select(e => e.PcrIndex).Distinct().OrderBy(x => x).ToList();

        string typeText = string.Join("、", byType.Take(MaxTypesInText)
            .Select(g => $"0x{g.Key:X8}×{g.Count()}"))
            + (byType.Count > MaxTypesInText ? $"；等 {byType.Count} 種" : "");

        string truncNote = walk.Truncated
            ? "。**走訪提前結束**（log 損毀或截斷）——上面的計數是「走到哪算到哪」，"
              + "而那與「log 裡只有這些事件」是**不同的斷言**"
            : "";

        return
        [
            new HardwareFact(SummaryKey, Category, "量測開機日誌摘要",
                $"{walk.Events.Count} 筆事件、覆蓋 {pcrs.Count} 個 PCR"
                + $"（{string.Join("、", pcrs.Take(12))}{(pcrs.Count > 12 ? "…" : "")}）"
                + $"，型別 {byType.Count} 種",
                "筆", Source, FactTrustLevel.Measured, false, at, walk.Events.Count,
                FactAvailability.Present,
                "**既有的 tpm.tcg_log 說的是「N 個事件」與 PCR 覆蓋；"
                + "這一把把「型別種類數」補上。**"
                + "而**事件數多不代表量測完整、少不代表有問題**——"
                + "不同平台的 log 粒度本來就不同" + truncNote),

            new HardwareFact(HistogramKey, Category, "開機事件型別直方圖", typeText,
                "種", Source, FactTrustLevel.Measured, false, at, byType.Count,
                FactAvailability.Present,
                "**只列型別編號與事件數，不命名。**"
                + "TCG 的事件型別是 EV_* 常數（u32），而**本專案沒有任何地方收錄那張表**。"
                + "我知道其中幾個編號，但**「我記得」不是出處**——"
                + "而依本專案在 FIT 的 Key Manifest 上學到的教訓："
                + "**填錯的編號會讓一個不存在的東西看起來存在，那比「未收錄」更糟。**"
                + "要補上型別名，需要先對準 TCG PC Client 規格的 EV_* 表出處"
                + "，再把它加成一張有出處的表（**那是一處改、多處受益**）"
                + truncNote),
        ];
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [SummaryKey, HistogramKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
