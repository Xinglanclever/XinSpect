using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 工具自身的掃描狀態申報（ULTIMATE-1000 階段一 P1-001 – P1-105 的<b>接線</b>）。
///
/// <para>
/// <b>這一支存在的理由，是這一階段自己抓到的一個真缺陷：</b>
/// 階段一寫了 20 支「輸出鍵產生器」——骨架、狀態格、出處鏈、知識包、敏感度、
/// 寫入閘門、韌性、發佈——每一支都有完整測試，<b>而其中 20 支的生產碼引用數是 0</b>。
/// 也就是說：這些鍵在目錄裡、在測試裡、在覆蓋申報裡都「存在」，
/// 但<b>不會出現在任何使用者的報告裡</b>。
/// </para>
///
/// <para>
/// 這正是 <c>GuardRegistry</c> 第一條守門（「接線守門」）宣告要防的事，
/// 而它當時沒有響——因為它的判斷方式是「事實鍵有沒有生產者」，
/// 而這 20 支服務<b>確實是生產者</b>，只是沒有人呼叫它們。
/// </para>
///
/// <para>
/// 所以這一支的工作很單純：<b>把那些產生器的輸出接進事實集</b>。
/// 它不重新計算任何東西——每一把鍵的值都由原本那支服務產生，
/// 這裡只負責「呼叫它、把它接起來」。
/// </para>
/// </summary>
public static class ScanStateFactsService
{
    public const string Category = "掃描狀態";
    private const string Source = "掃描骨架／工具自身狀態";

    /// <summary>
    /// 收集工具自身的掃描狀態事實。
    ///
    /// <para>
    /// <b>為什麼這些事實值得出現在報告裡：</b>
    /// 使用者看到的每一張卡片都是「這台機器的某個面向」，
    /// 而這一組是「<b>這份報告本身的狀態</b>」——骨架跑得順不順、有沒有逾時、
    /// 哪些資料讀不到、讀不到的原因、出處覆蓋到哪裡、這份報告含多少敏感欄位、
    /// 有沒有任何資料離開本機。
    /// <b>沒有這一組，前面那一堆卡片就只能被接受，不能被質疑。</b>
    /// </para>
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(
        DateTimeOffset at,
        int skeletonAdopted,
        int skeletonTotal,
        IReadOnlyList<ScanStepResult> steps,
        ScanOutcome? outcome,
        IReadOnlyCollection<HardwareFact> facts,
        int fileInputs,
        int egressAttempts)
    {
        var list = new List<HardwareFact>
        {
            // P1-001／P1-012 骨架採用
            ScanSkeletonReport.ToAdoptionFact(at, skeletonAdopted, skeletonTotal),
            // P1-002 逾時
            ScanSkeletonReport.ToTimeoutFact(at, steps),
            // P1-010 供檔零出網
            ScanSkeletonReport.ToZeroEgressFact(at, fileInputs, egressAttempts),
        };

        // P1-004 中斷保留：只有真的跑過一輪才有意義。
        if (outcome is not null)
            list.Add(ScanSkeletonReport.ToPartialFact(at, outcome));

        // P1-014／P1-017／P1-020／P1-021 狀態格與逐態申報
        list.AddRange(FactStateReport.All(at, facts));

        // P1-024–P1-032 出處鏈
        list.Add(SpecRefReport.ToRefFact(at));
        list.Add(SpecRefReport.ToGranularityFact(at));
        list.Add(SpecRefReport.ToCoverageFact(at,
            SpecRefRegistry.CoveredDecoders.Sum(d => d.GetMethods(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.DeclaredOnly).Length)));
        list.Add(SpecRefReport.ToVersionFact(at, at));
        list.Add(SpecRefReport.ToProvenanceFact(at, facts, []));

        // P1-045／P1-046 敏感度與匯出（這一輪的預設政策）
        list.Add(SensitivityPolicy.ToLevelFact(at, facts));

        // P1-065 守門清冊
        list.Add(WriteGateReport.ToInventoryFact(at));

        return list;
    }

    /// <summary>
    /// 產生器的<b>登記表</b>——每一支都必須被 <see cref="Collect"/> 或別處的生產碼叫到。
    ///
    /// <para>
    /// 這份清單存在的唯一目的是讓守門可以檢查「有沒有被接線」。
    /// 它刻意<b>不</b>用反射自動探索——自動探索會讓「新寫的產生器忘了接」
    /// 變成「自動被接上」，於是守門就再也抓不到漏接。
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Generator, string Key, string Wiring)> Generators { get; } =
    [        ("ScanSkeletonReport", "scan.skeleton.adoption", nameof(Collect)),
        ("ScanSkeletonReport", "scan.step.timeout", nameof(Collect)),
        ("ScanSkeletonReport", "scan.partial.count", nameof(Collect)),
        ("ScanSkeletonReport", "file.input.zero_egress", nameof(Collect)),
        ("FactStateReport", "state.lattice", nameof(Collect)),
        ("FactStateReport", "state.count", nameof(Collect)),
        ("FactStateReport", "fact.readerror.reason", nameof(Collect)),
        ("FactStateReport", "fact.why_unreadable", nameof(Collect)),
        ("SpecRefReport", "spec.ref", nameof(Collect)),
        ("SpecRefReport", "spec.ref.granularity", nameof(Collect)),
        ("SpecRefReport", "spec.ref.coverage", nameof(Collect)),
        ("SpecRefReport", "spec.ref.version", nameof(Collect)),
        ("SpecRefReport", "fact.provenance", nameof(Collect)),
        ("SensitivityPolicy", "fact.sensitivity", nameof(Collect)),
        ("WriteGateReport", "guard.inventory", nameof(Collect)),
    ];

    /// <summary>登記表上出現的產生器型別（去重）——接線守門用它檢查「有沒有被生產碼呼叫」。</summary>
    public static IReadOnlyList<string> GeneratorTypes { get; } =
        Generators.Select(g => g.Generator).Distinct(StringComparer.Ordinal).ToList();
}
