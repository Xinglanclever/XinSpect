using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 六態的覆蓋申報與原因查詢（ULTIMATE-1000 階段一 P1-014、P1-017、P1-020、P1-021）。
///
/// <para>
/// <b>為什麼這四項會綁在一起：</b>它們問的是同一個問題的三個面向——
/// 「這一輪的事實分別落在哪些狀態」（<c>state.count</c>）、
/// 「某筆事實為什麼落在那個狀態」（<c>fact.why_unreadable</c>）、
/// 「這個狀態能不能往上傳播」（<c>state.lattice</c>）。
/// 三者的資料來源都是同一份 <see cref="HardwareFact"/> 清單。
/// </para>
///
/// <para>
/// <b>為什麼 P1-017 的 <c>fact.readerror.reason</c> 不是「多加一欄」：</b>
/// <c>ReadError</c> 若沒有原因，就與「不適用」在報告上長得一樣——
/// 使用者看到一片「讀取失敗」卻不知道能修什麼。原因不是裝飾，是這個狀態唯一的用途。
/// </para>
/// </summary>
public static class FactStateReport
{
    // ── P1-020 逐態統計 ────────────────────────────────────────────────

    public const string CountKey = "state.count";
    public const string LatticeKey = "state.lattice";
    public const string ReadErrorReasonKey = "fact.readerror.reason";
    public const string WhyUnreadableKey = "fact.why_unreadable";

    /// <summary>逐態計數。**六態全部都要有鍵**，沒出現的狀態記 0——「0 筆」與「不存在」不同。</summary>
    public static IReadOnlyDictionary<FactAvailability, int> CountByState(IEnumerable<HardwareFact> facts)
    {
        var counts = FactStateLattice.LatticeOrder.ToDictionary(s => s, _ => 0);
        foreach (var f in facts)
        {
            // 未收錄的狀態歸到最弱那一格，但不靜默併入——由 ValidateCounts 指出來。
            if (counts.ContainsKey(f.Availability)) counts[f.Availability]++;
        }
        return counts;
    }

    /// <summary>產生 <c>state.count</c>：一整輪掃描的狀態分布。</summary>
    public static HardwareFact ToCountFact(DateTimeOffset at, IReadOnlyCollection<HardwareFact> facts)
    {
        if (facts.Count == 0)
            return new HardwareFact(CountKey, "狀態", "狀態分布",
                "這一輪沒有任何事實", "筆", "狀態覆蓋申報", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有任何事實可以統計");

        var counts = CountByState(facts);
        // 只列非零的狀態，但「哪些狀態沒出現」由計數字典保證（六態全在，值為 0）。
        string detail = string.Join("、", counts.Where(kv => kv.Value > 0)
            .Select(kv => $"{FactStateLattice.Describe(kv.Key)} {kv.Value}"));

        bool anyBlocked = counts.Where(kv => FactStateLattice.IsBlocked(kv.Key)).Sum(kv => kv.Value) > 0;
        bool anyUnconfirmed = counts[FactAvailability.Unknown] > 0;

        // 有「讀不到」或「未確認」時，這把鍵本身不該標成 Present——
        // 那會讓「狀態分布」看起來一切正常，而它正是用來指出不正常的那把鍵。
        var availability = anyBlocked || anyUnconfirmed
            ? FactAvailability.Unknown
            : FactAvailability.Present;

        return new HardwareFact(CountKey, "狀態", "狀態分布", detail, "筆",
            "狀態覆蓋申報", FactTrustLevel.Derived, false, at,
            facts.Count, availability,
            availability == FactAvailability.Present ? null
                : anyBlocked ? "本輪有讀取受阻的事實——那些欄位不能用來下結論"
                             : "本輪有未確認的事實");
    }

    // ── P1-017 讀取失敗要帶原因 ─────────────────────────────────────────

    /// <summary>
    /// 產生 <c>fact.readerror.reason</c>：把本輪所有 <c>ReadError</c> 的原因列出來。
    /// 沒有的狀態回「不適用」而不是空字串——空字串會被讀成「沒有失敗」，
    /// 而實際上可能是「根本沒讀」。
    /// </summary>
    public static HardwareFact ToReadErrorReasonFact(DateTimeOffset at, IReadOnlyCollection<HardwareFact> facts)
    {
        var errors = facts.Where(f => f.Availability == FactAvailability.ReadError).ToList();
        if (errors.Count == 0)
            return new HardwareFact(ReadErrorReasonKey, "狀態", "讀取失敗原因",
                "本輪沒有讀取失敗的事實", "筆", "狀態覆蓋申報", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有 ReadError 可以列原因");

        int withoutReason = errors.Count(f => string.IsNullOrWhiteSpace(f.UnavailableReason));
        string detail = string.Join("；", errors.Select(f =>
            $"{f.Key}：{(string.IsNullOrWhiteSpace(f.UnavailableReason) ? "（沒有留下原因）" : f.UnavailableReason)}"));

        return new HardwareFact(ReadErrorReasonKey, "狀態", "讀取失敗原因", detail, "筆",
            "狀態覆蓋申報", FactTrustLevel.Derived, false, at,
            errors.Count,
            // 有失敗卻沒原因，比有失敗更糟：那正是本專案最不該出現的東西。
            withoutReason == 0 ? FactAvailability.Present : FactAvailability.ReadError,
            withoutReason == 0 ? null : $"{withoutReason} 筆讀取失敗沒有附原因");
    }

    // ── P1-021 「為什麼是 Unknown」可查詢 ───────────────────────────────

    /// <summary>
    /// 產生 <c>fact.why_unreadable</c>：把「不可用的事實」逐筆給出可讀的解釋。
    /// 這一項的用途是回答使用者的「為什麼這裡是空的」——所以每一筆都要有句子，
    /// 不能只給狀態代碼。
    /// </summary>
    public static HardwareFact ToWhyUnreadableFact(DateTimeOffset at, IReadOnlyCollection<HardwareFact> facts)
    {
        var unusable = facts.Where(f => f.Availability != FactAvailability.Present).ToList();
        if (unusable.Count == 0)
            return new HardwareFact(WhyUnreadableKey, "狀態", "不可用原因查詢",
                "本輪所有事實都可用", "筆", "狀態覆蓋申報", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有不可用的事實要解釋");

        string detail = string.Join("；", unusable.Select(f =>
        {
            string why = string.IsNullOrWhiteSpace(f.UnavailableReason)
                ? FactStateLattice.Explain(f.Availability)
                : $"{FactStateLattice.Explain(f.Availability)}（{f.UnavailableReason}）";
            return $"{f.Key}：{why}";
        }));

        // 這一項永遠標 Present：它本身是「我查得到」的證明。
        // 若標成 Unknown，就變成「不確定能不能解釋為什麼不確定」，那沒有意義。
        return new HardwareFact(WhyUnreadableKey, "狀態", "不可用原因查詢", detail, "筆",
            "狀態覆蓋申報", FactTrustLevel.Derived, false, at, unusable.Count,
            FactAvailability.Present, null);
    }

    // ── P1-014 狀態格傳播規則成文 ───────────────────────────────────────

    /// <summary>
    /// 產生 <c>state.lattice</c>：把六態的偏序與傳播規則本身變成一筆事實。
    ///
    /// <para>
    /// <b>為什麼傳播規則要變成事實而不是只寫在註解裡：</b>規則寫在註解裡，
    /// 使用者看不到，於是「為什麼這張卡片說未確認、那張說讀取失敗」只能猜。
    /// 把它輸出成事實，報告就能自己回答這個問題。
    /// </para>
    /// </summary>
    public static HardwareFact ToLatticeFact(DateTimeOffset at)
    {
        var order = FactStateLattice.LatticeOrder;
        string detail = string.Join(" ＞ ", order.Select(s => FactStateLattice.Describe(s)));
        return new HardwareFact(LatticeKey, "狀態", "狀態格與傳播規則",
            $"{detail}；衍生事實取來源中最弱者", "級", "狀態格", FactTrustLevel.Derived, false, at,
            order.Count, FactAvailability.Present, null);
    }

    /// <summary>這一輪的四把狀態鍵。</summary>
    public static IReadOnlyList<HardwareFact> All(DateTimeOffset at, IReadOnlyCollection<HardwareFact> facts) =>
    [
        ToCountFact(at, facts),
        ToReadErrorReasonFact(at, facts),
        ToWhyUnreadableFact(at, facts),
        ToLatticeFact(at),
    ];

    /// <summary>
    /// 申報的自我檢查：逐態計數的總和必須等於事實總數。
    /// 不等時代表有狀態沒被歸類——那是缺陷，不是「其他」。
    /// </summary>
    public static IReadOnlyList<string> ValidateCounts(IReadOnlyCollection<HardwareFact> facts)
    {
        var problems = new List<string>();
        var counts = CountByState(facts);
        int sum = counts.Values.Sum();
        if (sum != facts.Count)
            problems.Add($"逐態計數總和 {sum} 不等於事實總數 {facts.Count}——有狀態沒被歸類");

        foreach (var f in facts.Where(f => f.Availability == FactAvailability.ReadError
                                        && string.IsNullOrWhiteSpace(f.UnavailableReason)))
            problems.Add($"{f.Key} 是 ReadError 但沒有附原因");
        return problems;
    }
}
