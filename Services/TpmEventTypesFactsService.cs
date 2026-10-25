using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>
/// TCG log 逐事件型別的統計（U10-FW-0075，鍵 <c>tpm.ev.types</c>＋家族 <c>tpm.ev.type.{code}</c>）。
///
/// <para>
/// <b>回收既有的走訪結果，不重讀 log</b>：輸入與 <see cref="TpmLogFactsService"/> 是同一份
/// <see cref="Tpm2.TcgLogWalk"/>（呼叫端把同一個走訪結果分派給兩個消費者）——
/// 本服務不碰 TBS、不建立內容、不呼叫 <c>WalkTcgLog</c> 第二次。
/// 既有的 <c>tpm.log.histogram</c> 在<b>一把鍵的文字裡</b>列型別分佈；這一項把「這次開機的 log 裡
/// 出現了哪些型別、各幾筆」攤成<b>可單獨選取與匯出的鍵</b>：彙總一把、逐型別各一把。
/// </para>
///
/// <para>
/// <b>命名紀律（沿用 <see cref="TpmLogFactsService"/> 學到的那一課）：</b>
/// TCG 的事件型別是 EV_* 常數（u32），而<b>本專案沒有任何地方收錄那張表</b>——
/// 「我記得 EV_POST_CODE 是 0x01」不是出處，填錯的編號會讓一個不存在的東西看起來存在。
/// 所以這裡<b>只列型別編號與事件數，不命名</b>；鍵名的 <c>{code}</c> 用十進位
/// （與 tpm.pcr.* 的索引同一風格），值裡並列十六進位原碼供對表。
/// </para>
///
/// <para>
/// <b>界線：</b>家族成員＝<b>log 裡實際出現的型別</b>（不是 TCG 規格的全表）——
/// 沒出現的型別沒有鍵，而「沒出現」不帶任何判斷（不同平台的 log 粒度本來就不同）。
/// 走訪截斷時所有計數是「走到哪算到哪」，那與「log 裡只有這些」是不同的斷言，寫進值。
/// </para>
/// </summary>
public static class TpmEventTypesFactsService
{
    private const string Category = "信賴根";
    private const string Source = "TCG 事件 log（既有的 Tpm2.WalkTcgLog 結果，不重讀）";

    public const string TypesKey = "tpm.ev.types";

    /// <summary>逐型別明細的<strong>家族前綴</strong>——成員＝這次 log 裡實際出現的型別碼，編譯期列不完。</summary>
    private const string EventTypePrefix = "tpm.ev.type.";

    /// <summary>逐型別鍵最多出幾把——超過時彙總明說「等 N 種」，不假裝列完。</summary>
    private const int MaxTypeKeys = 16;

    /// <summary>彙總文字裡最多列幾種。</summary>
    private const int MaxTypesInText = 8;

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Tpm2.TcgLogWalk? walk)
    {
        if (walk is null)
            return [new HardwareFact(TypesKey, Category, "事件型別統計", "", "種", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "沒有 log 走訪結果——本服務**不重讀 log**（tpm.tcg_log 有它自己的可及性事實，"
                + "同一份走訪不許走兩次）；讀不到 log＝如實標，不是「型別分佈是空的」")];

        if (walk.Events.Count == 0)
            return [new HardwareFact(TypesKey, Category, "事件型別統計", "", "種", Source,
                FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                "**log 讀到了但走訪不出任何事件**（格式不符或全損毀）——沒有事件就沒有型別分佈，"
                + "**不填 0**：那是確定的「數不出來」，與「0 種型別」不同")];

        var byType = walk.Events.GroupBy(e => e.EventType)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToList();

        string truncNote = walk.Truncated
            ? "。**走訪提前結束**（log 損毀或截斷）——下面的計數是「走到哪算到哪」，"
              + "與「log 裡只有這些」是**不同的斷言**"
            : "";

        string listing = string.Join("、", byType.Take(MaxTypesInText).Select(g => $"0x{g.Key:X8}×{g.Count()}"))
            + (byType.Count > MaxTypesInText ? $"；等 {byType.Count} 種" : "");

        var facts = new List<HardwareFact>(Math.Min(byType.Count, MaxTypeKeys) + 1)
        {
            new(TypesKey, Category, "事件型別統計",
                $"{walk.Events.Count} 筆事件、型別 {byType.Count} 種：{listing}。"
                + "**只列型別編號，不命名**——本專案沒有 EV_* 對照表的出處（同一紀律見 tpm.log.histogram）。"
                + "逐型別計數見 tpm.ev.type.* 家族（成員＝log 裡實際出現的型別）" + truncNote,
                "種", Source, FactTrustLevel.Measured, false, at, byType.Count,
                FactAvailability.Present, null),
        };

        foreach (var g in byType.Take(MaxTypeKeys))
        {
            facts.Add(new HardwareFact(EventTypePrefix + g.Key.ToString(CultureInfo.InvariantCulture),
                Category, $"事件型別 0x{g.Key:X8}",
                $"0x{g.Key:X8}：{g.Count()} 筆（PCR {string.Join("、", g.Select(e => e.PcrIndex).Distinct().OrderBy(x => x))}）"
                + (walk.Truncated ? "——截斷走訪下的計數**走到哪算到哪**" : ""),
                "筆", Source, FactTrustLevel.Measured, false, at, g.Count(),
                FactAvailability.Present, null));
        }
        return facts;
    }
}
