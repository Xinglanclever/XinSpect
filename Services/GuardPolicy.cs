using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 守門方法論中樞（ULTIMATE-1000 階段一 P1-055、P1-061、P1-062、P1-063）。
///
/// <para>
/// <b>這一支存在的理由：</b>本專案已經有 80 多條守門測試。它們的品質差異極大——
/// 有的附正對照（該紅會紅）、有的只是「跑一次沒炸」；有的守得住它宣稱的事、
/// 有的守不住但沒人知道。這一支把<b>守門自己的規格</b>寫成資料，
/// 讓「這個守門守得住什麼」變成可稽核的問題。
/// </para>
///
/// <para>
/// <b>四件事互相扣在一起：</b>
/// ① 每個守門要宣告<b>模式</b>（它屬於哪一類防線）；
/// ② 要宣告<b>等級</b>（守得住什麼、<b>守不住什麼</b>——後者最常被省略）；
/// ③ 例外清單要有<b>上限</b>與理由；
/// ④ 遇到<b>誤報要修守門，不是放寬</b>。
/// 少掉任何一項，守門就會慢慢退化成儀式。
/// </para>
/// </summary>
public static class GuardPolicy
{
    /// <summary>
    /// 守門的五種模式（P1-055）。<b>每一種防的是不同種類的失敗</b>，
    /// 這也是為什麼「無理由者不加」——說不出模式的守門通常是儀式。
    /// </summary>
    public enum GuardMode
    {
        /// <summary>介面契約：某個介面／型別的使用者必須遵守的形狀。</summary>
        InterfaceContract,
        /// <summary>原始碼掃描：某種寫法不得出現在某個範圍的程式碼裡。</summary>
        SourceScan,
        /// <summary>政策釘死：某個政策值（清單、門檻、級別）必須維持。</summary>
        PolicyPin,
        /// <summary>性質／不變式：對任意輸入都必須成立的關係。</summary>
        Property,
        /// <summary>對帳：兩個獨立來源的說法必須一致。</summary>
        Reconciliation,
    }

    /// <summary>
    /// 守門的等級宣告（P1-062）。
    /// <b><see cref="DoesNotCatch"/> 是必填的</b>——說不出「守不住什麼」的守門，
    /// 會讓使用者以為它比實際更嚴密。
    /// </summary>
    /// <param name="Catches">這個守門守得住什麼。</param>
    /// <param name="DoesNotCatch">這個守門<b>守不住</b>什麼。必填，不得為空。</param>
    public sealed record GuardLevel(string Catches, string DoesNotCatch)
    {
        public bool IsDeclared =>
            !string.IsNullOrWhiteSpace(Catches) && !string.IsNullOrWhiteSpace(DoesNotCatch);
    }

    /// <summary>一條守門的完整宣告。</summary>
    /// <param name="Name">守門測試的名稱（要對得上真實的測試）。</param>
    /// <param name="Mode">五模式之一。</param>
    /// <param name="RealRisk">它防範的<b>真實</b>風險——不是「可能會錯」這種空話。</param>
    /// <param name="Level">等級宣告。</param>
    /// <param name="HasPositiveControl">是否附了「該紅會紅」的正對照。</param>
    public sealed record Guard(
        string Name, GuardMode Mode, string RealRisk, GuardLevel Level, bool HasPositiveControl);

    /// <summary>
    /// 檢查一條守門的宣告是否完備。
    /// <b>回傳的問題為空＝這條守門的規格是完整的。</b>
    /// </summary>
    public static IReadOnlyList<string> Validate(Guard guard)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(guard.Name))
            problems.Add("守門沒有名稱");
        if (string.IsNullOrWhiteSpace(guard.RealRisk) || guard.RealRisk.Trim().Length < 10)
            problems.Add($"{guard.Name}：沒有寫出它防範的真實風險（或寫得太短）");
        if (!guard.Level.IsDeclared)
            problems.Add($"{guard.Name}：等級宣告不完整——「守得住什麼」與「守不住什麼」都要寫");
        // 原始碼掃描與政策釘死這兩類，沒有正對照就可能是永遠綠的。
        if (guard.Mode is GuardMode.SourceScan or GuardMode.PolicyPin && !guard.HasPositiveControl)
            problems.Add($"{guard.Name}：{Describe(guard.Mode)}沒有附正對照——"
                       + "沒有「該紅會紅」的證明，它可能因為樣式失效而永遠是綠的");
        return problems;
    }

    /// <summary>
    /// 例外清單的上限檢查（P1-061）。
    /// <b>超過上限不是「再加一條就好」，是「先重新想一遍」。</b>
    /// </summary>
    public static IReadOnlyList<string> ValidateWhitelist(
        IReadOnlyDictionary<string, string> entries, int cap, int minReasonLength = 10)
    {
        var problems = new List<string>();
        if (entries.Count > cap)
            problems.Add($"例外清單已達 {entries.Count} 筆（上限 {cap}）——先重新檢視這些例外是不是該處理了，"
                       + "而不是提高上限");
        foreach (var (key, reason) in entries)
        {
            if (string.IsNullOrWhiteSpace(reason))
                problems.Add($"{key}：例外必須寫出理由");
            else if (reason.Trim().Length < minReasonLength)
                problems.Add($"{key}：理由太短（{reason}），要說得清楚為什麼可以例外");
        }
        return problems;
    }

    /// <summary>
    /// 誤報的處置原則（P1-063）。
    ///
    /// <para>
    /// <b>誤報要修守門，不是放寬守門。</b>放寬一次，那個守門就不再能證明它宣稱的事——
    /// 而且沒有人會記得它被放寬過。這條規則把「怎麼處置誤報」變成一個要回答的問題。
    /// </para>
    /// </summary>
    public enum FalsePositiveAction
    {
        /// <summary>修正守門的判斷邏輯（正確的處置）。</summary>
        FixGuard,
        /// <summary>放寬門檻或刪除斷言（錯誤的處置——它讓守門失去意義）。</summary>
        LoosenThreshold,
        /// <summary>加進白名單（只有在「該項確實不屬於這個守門的範圍」時才正確）。</summary>
        Whitelist,
        /// <summary>改測試而沒有改被測的程式碼（要能說出為什麼被測物是對的）。</summary>
        AdjustExpectation,
    }

    /// <summary>判定一次誤報處置是否可接受，以及理由要求。</summary>
    public static string? JudgeFalsePositive(FalsePositiveAction action, string justification)
    {
        if (action == FalsePositiveAction.LoosenThreshold)
            return "放寬門檻不是誤報的處置——它會讓守門失去它宣稱的能力，而且沒有人會記得它被放寬過。"
                 + "請改為修正判斷邏輯。";
        if (string.IsNullOrWhiteSpace(justification) || justification.Trim().Length < 20)
            return $"「{Describe(action)}」需要寫出理由（至少 20 字）："
                 + "沒有理由的處置與放寬無異。";
        return null;   // 可接受
    }

    public static string Describe(GuardMode mode) => mode switch
    {
        GuardMode.InterfaceContract => "介面契約",
        GuardMode.SourceScan => "原始碼掃描",
        GuardMode.PolicyPin => "政策釘死",
        GuardMode.Property => "性質／不變式",
        _ => "對帳",
    };

    public static string Describe(FalsePositiveAction action) => action switch
    {
        FalsePositiveAction.FixGuard => "修正守門",
        FalsePositiveAction.LoosenThreshold => "放寬門檻",
        FalsePositiveAction.Whitelist => "加入白名單",
        _ => "調整期待值",
    };
}
