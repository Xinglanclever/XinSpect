using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 出網許可清單與觸發宣告（ULTIMATE-1000 階段一 P1-049、P1-050）。
///
/// <para>
/// <b>本專案的立場是「零出網」，而這一組是那個立場的機械化表達：</b>
/// 不是「我們盡量不出網」，而是「<b>任何</b>出網通路都必須先寫進許可清單、
/// 而且必須由使用者主動觸發」。清單空的時候，唯一的正確行為是——不出網。
/// </para>
///
/// <para>
/// <b>為什麼要宣告「觸發」而不只是「有沒有出網」：</b>
/// 使用者真正需要知道的是「什麼情況下這台機器會連出去」。
/// 只報「本次沒有出網」是不夠的——那對明天可能發生的出網毫無預警。
/// <see cref="TriggerKind"/> 把觸發種類寫成資料，於是「會不會自己連出去」
/// 變成一個可以回答的問題，而不是一句保證。
/// </para>
/// </summary>
public static class EgressPolicy
{
    public const string AllowlistKey = "egress.allowlist";
    public const string TriggerKey = "egress.trigger";

    private const string Category = "資料邊界";
    private const string Source = "出網政策";

    /// <summary>
    /// 出網通路的種類。<b>只有 <see cref="UserInitiated"/> 是允許的</b>——
    /// 其餘三種的存在是為了讓「不該有」這件事可以被表達，而不是為了真的去用。
    /// </summary>
    public enum TriggerKind
    {
        /// <summary>使用者主動觸發（按下按鈕、帶了參數）。唯一允許的觸發方式。</summary>
        UserInitiated,
        /// <summary>背景排程——不允許：使用者不在場時的出網無法被同意。</summary>
        Scheduled,
        /// <summary>啟動時自動——不允許：使用者還沒看到任何畫面就已經連出去了。</summary>
        OnStartup,
        /// <summary>回應遠端請求（推播、長連線）——不允許：那等於接受外部指令。</summary>
        RemoteInitiated,
    }

    /// <summary>一筆出網許可。沒有登記在清單裡的通路一律不得出網。</summary>
    public sealed record AllowEntry(string Purpose, TriggerKind Trigger, string Justification)
    {
        public bool IsPermitted => Trigger == TriggerKind.UserInitiated
                                   && !string.IsNullOrWhiteSpace(Justification);
    }

    /// <summary>產生 <c>egress.allowlist</c>：清單內容與每一筆的許可判定。</summary>
    public static HardwareFact ToAllowlistFact(DateTimeOffset at, IReadOnlyList<AllowEntry> entries)
    {
        if (entries.Count == 0)
            return new HardwareFact(AllowlistKey, Category, "出網許可清單",
                "清單為空——本版沒有任何出網通路", "條", Source, FactTrustLevel.Reported,
                false, at, 0, FactAvailability.Present,
                "空清單是常態也是目標：沒有任何功能會連出去");

        var notPermitted = entries.Where(e => !e.IsPermitted).ToList();
        if (notPermitted.Count == 0)
            return new HardwareFact(AllowlistKey, Category, "出網許可清單",
                $"{entries.Count} 條許可，全部由使用者主動觸發", "條", Source,
                FactTrustLevel.Reported, false, at, entries.Count, FactAvailability.Present, null);

        // 有「非使用者主動」的登記＝這是一個必須被看見的狀態，不是可以靜默接受的設定。
        return new HardwareFact(AllowlistKey, Category, "出網許可清單",
            $"{entries.Count} 條登記，其中 {notPermitted.Count} 條不是使用者主動觸發（"
            + string.Join("、", notPermitted.Select(e => $"{e.Purpose}={Describe(e.Trigger)}")) + "）",
            "條", Source, FactTrustLevel.Reported, false, at, entries.Count - notPermitted.Count,
            FactAvailability.ReadError,
            "非使用者主動的出網通路不得存在——零出網是產品立場，不是可調參數");
    }

    /// <summary>
    /// 產生 <c>egress.trigger</c>：這一輪實際上發生過的觸發。
    ///
    /// <para>
    /// <b>「沒有觸發」與「沒有登記」是兩件事：</b>前者是清單有東西但都沒被觸發，
    /// 後者是根本沒有出網通路。混為一談會讓「本輪沒有出網」看起來像保證，
    /// 而它其實只是「這次沒按那個按鈕」。
    /// </para>
    /// </summary>
    public static HardwareFact ToTriggerFact(DateTimeOffset at, int registeredChannels,
        IReadOnlyList<TriggerKind> observed)
    {
        if (observed.Count == 0)
            return new HardwareFact(TriggerKey, Category, "出網觸發",
                registeredChannels == 0
                    ? "本版沒有任何出網通路（清單為空）"
                    : $"本輪沒有觸發；已登記 {registeredChannels} 條通路但都未被使用", "次",
                Source, FactTrustLevel.Derived, false, at, 0, FactAvailability.Present,
                registeredChannels == 0 ? null : "沒有觸發不等於沒有通路——那些通路仍然登記著");

        var nonUser = observed.Where(t => t != TriggerKind.UserInitiated).ToList();
        string detail = $"{observed.Count} 次觸發（"
            + string.Join("、", observed.GroupBy(t => t).Select(g => $"{Describe(g.Key)} {g.Count()}")) + "）";

        if (nonUser.Count == 0)
            return new HardwareFact(TriggerKey, Category, "出網觸發", detail, "次",
                Source, FactTrustLevel.Derived, false, at, observed.Count,
                FactAvailability.Present, null);

        return new HardwareFact(TriggerKey, Category, "出網觸發", detail, "次",
            Source, FactTrustLevel.Derived, false, at, observed.Count,
            FactAvailability.ReadError,
            $"有 {nonUser.Count} 次出網不是使用者主動觸發——唯一允許的觸發方式是被按下按鈕");
    }

    public static string Describe(TriggerKind kind) => kind switch
    {
        TriggerKind.UserInitiated => "使用者主動",
        TriggerKind.Scheduled => "背景排程",
        TriggerKind.OnStartup => "啟動時自動",
        _ => "遠端發起",
    };
}
