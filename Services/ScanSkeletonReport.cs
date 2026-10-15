using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 掃描骨架的觀測輸出（ULTIMATE-1000 階段一 P1-001、P1-002、P1-004、P1-010、P1-012）。
///
/// <para>
/// <b>為什麼骨架的觀測要獨立成一把鍵而不是散在紀錄裡：</b>
/// 骨架本身不產事實（事實來自各域服務），但「骨架這一輪做了什麼」必須可以被看見——
/// 否則「骨架有沒有被採用」「有幾步逾時」「中斷時保住了幾筆」都只能靠日誌猜。
/// </para>
///
/// <para>
/// <b>這裡的每一把鍵都遵守同一條規矩：沒有發生的時候回「不適用」，不回 0。</b>
/// 「沒有逾時」與「根本沒跑」在報告上必須長得不一樣。
/// </para>
/// </summary>
public static class ScanSkeletonReport
{
    public const string AdoptionKey = "scan.skeleton.adoption";
    public const string TimeoutKey = "scan.step.timeout";
    public const string PartialKey = "scan.partial.count";
    public const string ZeroEgressKey = "file.input.zero_egress";

    // ── P1-001 / P1-012 骨架採用數 ──────────────────────────────────────

    /// <summary>採用統計：實際實作骨架的來源數 vs 全體事實服務數。</summary>
    public static HardwareFact ToAdoptionFact(DateTimeOffset at, int adopted, int total)
    {
        if (total == 0)
            return new HardwareFact(AdoptionKey, "掃描骨架", "骨架採用數",
                "沒有可統計的來源", "個", "骨架相容性報告", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "這一輪沒有任何來源可統計");

        string detail = adopted == 0
            ? $"0/{total} 已實作骨架（既有服務尚未套用，見 P1-012）"
            : $"{adopted}/{total} 已實作骨架";

        // 採用數本身不是「成功或失敗」，但「一個都沒採用」是一個需要被看見的事實——
        // 標成 Present 會讓它看起來像達標。這裡用 Unknown 表達「有值但不能當成綠燈」。
        return new HardwareFact(AdoptionKey, "掃描骨架", "骨架採用數", detail, "個",
            "骨架相容性報告", FactTrustLevel.Derived, false, at, adopted,
            adopted == total ? FactAvailability.Present : FactAvailability.Unknown,
            adopted == total ? null : $"{total - adopted} 個來源尚未實作骨架");
    }

    /// <summary>從掃描結果算採用數：有跑過骨架的來源即算採用。</summary>
    public static HardwareFact ToAdoptionFact(DateTimeOffset at, IEnumerable<ScanOutcome> outcomes, int total)
    {
        var list = outcomes.ToList();
        return ToAdoptionFact(at, list.Count(o => o.Steps.Count > 0), total == 0 ? list.Count : total);
    }

    // ── P1-002 逾時統計 ────────────────────────────────────────────────

    /// <summary>逾時統計。沒有宣告上界的步驟也會被列出——那正是需要看見的事。</summary>
    public static HardwareFact ToTimeoutFact(DateTimeOffset at, IEnumerable<ScanStepResult> steps)
    {
        var list = steps.ToList();
        if (list.Count == 0)
            return new HardwareFact(TimeoutKey, "掃描骨架", "步驟逾時",
                "這一輪沒有步驟紀錄", "步", "掃描骨架", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有步驟可以統計");

        var timedOut = list.Where(s => s.Detail is not null && s.Detail.Contains("超過", StringComparison.Ordinal)
                                       && s.Detail.Contains("上界", StringComparison.Ordinal)).ToList();
        var undeclared = list.Where(s => s.Detail is not null
                                         && s.Detail.Contains("未宣告", StringComparison.Ordinal)).ToList();
        var slowest = list.OrderByDescending(s => s.Elapsed).First();

        string detail = timedOut.Count == 0
            ? $"沒有步驟逾時；最慢的一步「{slowest.Name}」{slowest.Elapsed.TotalMilliseconds:0} 毫秒"
            : $"{timedOut.Count}/{list.Count} 步逾時：" +
              string.Join("、", timedOut.Select(s => $"{s.Name}（{s.Elapsed.TotalMilliseconds:0} 毫秒）"));

        // 逾時與「用了未宣告的預設上界」是兩件事，兩者都要能被看見：
        // 前者是這次真的等不下去，後者是設計上的疏漏（上界沒宣告）——
        // 一個逾時步驟可能同時屬於兩者，所以不算互斥。
        string? reason =
            timedOut.Count > 0
                ? (undeclared.Count > 0
                    ? $"{timedOut.Count} 步超過其上界，其中 {undeclared.Count} 步用的是未宣告的預設上界"
                    : $"{timedOut.Count} 步超過其宣告上界")
                : (undeclared.Count > 0 ? $"{undeclared.Count} 步使用了未宣告的預設上界" : null);

        return new HardwareFact(TimeoutKey, "掃描骨架", "步驟逾時", detail, "步",
            "掃描骨架", FactTrustLevel.Derived, false, at, timedOut.Count,
            timedOut.Count == 0 ? FactAvailability.Present : FactAvailability.ReadError,
            reason);
    }

    // ── P1-004 中斷保留 ────────────────────────────────────────────────

    /// <summary>
    /// 中斷時保留的事實數。這一項的重點是<b>把「被中斷」與「跑完但沒東西」分開</b>：
    /// 兩者在數字上都是「少量事實」，但意義完全相反。
    /// </summary>
    public static HardwareFact ToPartialFact(DateTimeOffset at, ScanOutcome outcome)
    {
        var completed = outcome.Steps.Where(s => s.Outcome == ScanStepOutcome.Completed).ToList();
        if (outcome.Steps.Count == 0)
            return new HardwareFact(PartialKey, "掃描骨架", "中斷保留事實數",
                "這一輪沒有步驟紀錄", "步", "掃描骨架", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有步驟可以統計");

        if (!outcome.WasCancelled)
            return new HardwareFact(PartialKey, "掃描骨架", "中斷保留事實數",
                $"未被中斷（完成 {completed.Count}/{outcome.Steps.Count} 步）", "步",
                "掃描骨架", FactTrustLevel.Derived, false, at, completed.Count,
                FactAvailability.NotApplicable, "本輪沒有發生中斷");

        return new HardwareFact(PartialKey, "掃描骨架", "中斷保留事實數",
            $"被中斷；已完成的 {completed.Count} 步結果保留、其餘 "
            + $"{outcome.Steps.Count - completed.Count} 步記為取消", "步",
            "掃描骨架", FactTrustLevel.Derived, false, at, completed.Count,
            completed.Count == 0 ? FactAvailability.ReadError : FactAvailability.Unknown,
            completed.Count == 0 ? "被中斷且沒有任何步驟完成——這一輪沒有可用的事實"
                                 : "被中斷但保留了已完成步驟的事實");
    }

    // ── P1-010 供檔零出網 ──────────────────────────────────────────────

    /// <summary>
    /// 供檔零出網模式的宣告。這一項是<b>產品立場</b>的機械化表達：
    /// 使用者提供的檔案只在本機解析，不得有任何出網通路。
    ///
    /// <para>
    /// <b>為什麼要輸出成事實：</b>立場寫在文件裡，使用者看不到；寫成事實，
    /// 報告自己就會說「這份分析的輸入沒有離開本機」。
    /// </para>
    /// </summary>
    public static HardwareFact ToZeroEgressFact(DateTimeOffset at, int fileInputs, int egressAttempts)
    {
        if (fileInputs == 0)
            return new HardwareFact(ZeroEgressKey, "資料邊界", "供檔零出網",
                "本輪沒有使用者供檔", "個", "供檔邊界", FactTrustLevel.Reported, false, at,
                0, FactAvailability.NotApplicable, "沒有供檔可以檢查");

        if (egressAttempts > 0)
            return new HardwareFact(ZeroEgressKey, "資料邊界", "供檔零出網",
                $"{fileInputs} 個供檔、出現 {egressAttempts} 次出網嘗試", "個",
                "供檔邊界", FactTrustLevel.Reported, false, at, egressAttempts,
                FactAvailability.ReadError,
                "供檔路徑上出現出網嘗試——零出網是產品立場，不是可調參數");

        return new HardwareFact(ZeroEgressKey, "資料邊界", "供檔零出網",
            $"{fileInputs} 個供檔，全部只在本機解析", "個", "供檔邊界",
            FactTrustLevel.Reported, false, at, 0, FactAvailability.Present, null);
    }
}
