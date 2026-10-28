namespace XinSpect;

/// <summary>
/// 收集步驟的讀取代價（U10 第三圈 MT-0004，第 311 輪）：回答「這一頁為什麼慢」。
/// </summary>
/// <remarks>
/// <para>
/// <b>代價是步驟級，不是逐鍵——這是刻意的取捨，而且要說出來。</b>
/// 一個 <c>LoadX()</c> 可能生產數十把鍵（掃一次 WMI 就有整組欄位），把毫秒攤到每把鍵上
/// 會得到一串看起來很精確、實際上沒有意義的數字。這裡只報「哪個步驟花了多久」，
/// 由鍵反推步驟是讀者的事。
/// </para>
/// <para>
/// <b>失敗的步驟也要有座號：</b>一個步驟拋例外不代表它沒花時間——它可能讀了 8 秒才失敗。
/// 失敗列保留毫秒並標「失敗」，原因寫在該步驟自己的事實裡（各服務的三態），不重複編造。
/// </para>
/// </remarks>
public static class MeasurementCostFactsService
{
    /// <summary>本輪收集步驟的總覽（靜態鍵）。</summary>
    public const string SummaryKey = "mt.cost.summary";

    /// <summary>逐步驟代價家族：<c>mt.cost.step.0001</c>…（依耗時由大到小）。</summary>
    public const string StepPrefix = "mt.cost.step.";

    /// <summary>最多列幾個步驟（超過由總覽說明還有幾個）。</summary>
    public const int MaxStepFacts = 40;

    private const string Category = "量測嚴謹度";
    private const string Source = "本輪收集計時（不需額外硬體）";

    /// <summary>把一輪的步驟代價投影成事實；不做任何 I/O。</summary>
    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<EvidenceLabService.CollectionStep> steps, DateTimeOffset at)
        => Collect(steps, at, MaxStepFacts);

    /// <summary>同上，可指定明細上限（測試用）。</summary>
    public static IReadOnlyList<HardwareFact> Collect(
        IReadOnlyList<EvidenceLabService.CollectionStep> steps, DateTimeOffset at, int maxStepFacts)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (maxStepFacts < 1) throw new ArgumentOutOfRangeException(nameof(maxStepFacts));

        if (steps.Count == 0)
        {
            return
            [
                new HardwareFact(SummaryKey, Category, "收集步驟代價", "", "毫秒", Source,
                    FactTrustLevel.Reported, false, at, null, FactAvailability.NotApplicable,
                    "這一輪沒有記錄到任何收集步驟——「沒有步驟」不是「步驟很快」。")
            ];
        }

        var ordered = steps
            .Select((s, i) => (Step: s, Order: i))
            .OrderByDescending(x => x.Step.Milliseconds)
            .ThenBy(x => x.Order)
            .ToList();

        var facts = new List<HardwareFact> { SummaryFact(steps, ordered, maxStepFacts, at) };

        int index = 0;
        foreach (var (step, _) in ordered.Take(maxStepFacts))
        {
            string key = StepPrefix + (++index).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            // 六態契約：已讀到的事實不得再帶「讀不到的原因」。失敗这件事寫在值裡，
            // 至於「為什麼失敗」——那是該步驟自己的事實要回答的，不在這裡重複編造。
            string value = step.Milliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " 毫秒"
                + (step.Succeeded ? "" : "（失敗：時間照記，原因見該步驟自己的事實）");
            facts.Add(new HardwareFact(key, Category, "步驟：" + step.Name, value, "毫秒", Source,
                FactTrustLevel.Reported, false, at, step.Milliseconds, FactAvailability.Present, null));
        }

        return facts;
    }

    private static HardwareFact SummaryFact(
        IReadOnlyList<EvidenceLabService.CollectionStep> steps,
        List<(EvidenceLabService.CollectionStep Step, int Order)> ordered,
        int maxStepFacts,
        DateTimeOffset at)
    {
        double total = steps.Sum(static s => s.Milliseconds);
        int failed = steps.Count(static s => !s.Succeeded);

        string slowest = string.Join("、", ordered.Take(3).Select(static x =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} {1:0.0} ms", x.Step.Name, x.Step.Milliseconds)));

        string value = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0} 個收集步驟、總計 {1:0.0} 毫秒；最慢：{2}",
            steps.Count, total, slowest);

        if (failed > 0)
            value += "；其中 " + failed + " 個步驟失敗（時間照記，原因在各步驟自己的事實裡）";
        if (steps.Count > maxStepFacts)
            value += "；逐步驟明細只列前 " + maxStepFacts + " 個，另有 " + (steps.Count - maxStepFacts) + " 個未列出";

        // 這一句是這一組存在的理由：不把步驟代價冒充成逐鍵代價。
        value += "。逐鍵代價未量：一個步驟可能生產數十把鍵，攤出來只會得到一串沒有意義的精確數字";

        return new HardwareFact(SummaryKey, Category, "收集步驟代價", value, "毫秒", Source,
            FactTrustLevel.Reported, false, at, total, FactAvailability.Present, null);
    }
}
