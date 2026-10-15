using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 資料主權報告與報告分級（ULTIMATE-1000 階段一 P1-051、P1-054）。
///
/// <para>
/// <b>這兩項回答的是同一個問題的兩面：</b>
/// 「這份報告裡的東西，有多少離開了本機」（<c>data.egress.report</c>）與
/// 「這份報告本身屬於哪一級」（<c>report.class</c>）。
/// 前者是對外的資料流，後者是這份文件自己的敏感度。
/// </para>
///
/// <para>
/// <b>為什麼報告分級不能讓使用者自己選：</b>
/// 讓使用者選「這份報告要多敏感」等於讓他決定要不要隱藏證據。
/// 分級必須<b>由所含事實推導</b>——一份含 L2 欄位的報告就是 L2 報告，
/// 不管作者覺得它重不重要。
/// </para>
/// </summary>
public static class DataSovereigntyReport
{
    public const string EgressReportKey = "data.egress.report";
    public const string ReportClassKey = "report.class";

    private const string Category = "資料邊界";
    private const string Source = "資料主權";

    /// <summary>產生 <c>data.egress.report</c>：逐類列出資料的去向。</summary>
    public static HardwareFact ToEgressReportFact(DateTimeOffset at, int totalFacts,
        int stayedLocal, int leftMachine, IReadOnlyList<string> leftDestinations)
    {
        if (totalFacts == 0)
            return new HardwareFact(EgressReportKey, Category, "資料主權",
                "本輪沒有產生任何事實", "筆", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有資料可以申報去向");

        if (totalFacts != stayedLocal + leftMachine)
            // 分母對不上就是缺陷，不是「其他」——寧可標失敗也不要報一個假的總數。
            return new HardwareFact(EgressReportKey, Category, "資料主權",
                $"分母不符：總數 {totalFacts} ≠ 留本機 {stayedLocal} ＋ 離開本機 {leftMachine}", "筆",
                Source, FactTrustLevel.Derived, false, at, null, FactAvailability.ReadError,
                "部分事實的去向未被歸類——不能報一個對不上的總數");

        if (leftMachine == 0)
            return new HardwareFact(EgressReportKey, Category, "資料主權",
                $"{totalFacts} 筆事實全部留在本機，沒有任何一筆離開", "筆",
                Source, FactTrustLevel.Derived, false, at, 0, FactAvailability.Present,
                "本專案立場是零出網；這把鍵申報的是實際結果，不是承諾");

        return new HardwareFact(EgressReportKey, Category, "資料主權",
            $"{leftMachine}/{totalFacts} 筆事實離開本機"
            + (leftDestinations.Count > 0 ? "（去向：" + string.Join("、", leftDestinations.Take(5)) + "）" : ""),
            "筆", Source, FactTrustLevel.Derived, false, at, leftMachine,
            FactAvailability.Unknown,
            $"有 {leftMachine} 筆資料離開本機——這與零出網立場不符，必須是使用者主動觸發且留有紀錄的");
    }

    /// <summary>
    /// 產生 <c>report.class</c>：報告等級<b>由所含事實推導</b>。
    ///
    /// <para>
    /// 推導規則是取<b>最高</b>敏感度（不是平均、不是多數）：
    /// 一份報告裡只要有一筆 L2，整份就是 L2——因為報告是以整體被傳遞的，
    /// 讀者不會只讀 L0 那幾頁。
    /// </para>
    /// </summary>
    public static HardwareFact ToReportClassFact(DateTimeOffset at,
        IReadOnlyCollection<HardwareFact> facts, SensitivityPolicy.Level declaredClass)
    {
        if (facts.Count == 0)
            return new HardwareFact(ReportClassKey, Category, "報告分級",
                "這份報告沒有內容", "級", Source, FactTrustLevel.Derived, false, at,
                null, FactAvailability.NotApplicable, "沒有事實可以推導分級");

        // 由內容推導，不採用作者宣告——除非兩者一致。
        var derived = facts.Select(SensitivityPolicy.Classify)
            .OrderByDescending(l => (int)l).First();

        if (derived == declaredClass)
            return new HardwareFact(ReportClassKey, Category, "報告分級",
                $"這份報告是 {derived} 級（由所含事實推導，與宣告一致）", "級",
                Source, FactTrustLevel.Derived, false, at, (int)derived,
                FactAvailability.Present, null);

        // 不一致時以內容為準，並把落差說出來——降級宣告是這條規則要防的事。
        bool understated = (int)derived > (int)declaredClass;
        return new HardwareFact(ReportClassKey, Category, "報告分級",
            $"這份報告是 {derived} 級（由所含事實推導；宣告為 {declaredClass} 級）", "級",
            Source, FactTrustLevel.Derived, false, at, (int)derived,
            FactAvailability.ReadError,
            understated
                ? $"宣告的等級低於內容所含的最高敏感度——報告以整體被傳遞，讀者不會只讀 L0 那幾頁"
                : $"宣告的等級高於內容所含的最高敏感度——過度宣告會讓真正需要保護的報告失去辨識度");
    }
}
