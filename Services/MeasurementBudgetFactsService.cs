namespace XinSpect;

/// <summary>
/// 逐域量測預算（U10 第三圈 MT-0002，第 313 輪）：每個即時量測域，把「視窗、換算、精度由誰決定」寫出來。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要有這一頁：</b>「CPU 使用率 63%」和「記憶體可用 18,204 MB」看起來一樣可信，
/// 但一個是 120 ms 差分、另一個是 OS 的瞬時快照；一個的誤差由排程決定，另一個由 OS 對
/// 「可用」的定義決定。使用者要知道自己能信到哪裡，就得先看到這三件事。
/// </para>
/// <para>
/// <b>不編造數字：</b>由驅動決定的精度，本機就寫「由驅動決定」並明白宣告不給誤差上限——
/// 這比隨手寫一個 ±0.5 °C 誠實得多。程式自己能決定的（差分窗、換算式、Stopwatch 頻率）才寫死。
/// </para>
/// <para>
/// 純陳述：這支不做 I/O、不讀硬體，所以表格內容可以逐列入測試對帳。
/// </para>
/// </remarks>
public static class MeasurementBudgetFactsService
{
    /// <summary>本輪量測預算的總覽（靜態鍵）。</summary>
    public const string SummaryKey = "mt.budget.summary";

    /// <summary>逐域預算家族：<c>mt.budget.{域}</c>（成員＝這一版真的有預算陳述的域）。</summary>
    public const string FamilyPrefix = "mt.budget.";

    /// <summary>精度由本機程式決定（我們自己取樣、自己換算）。</summary>
    public const string OwnerSelf = "本機";

    /// <summary>精度由驅動／晶片決定（我們只能照抄它報的值）。</summary>
    public const string OwnerDriver = "驅動";

    private const string Category = "量測嚴謹度";
    private const string Source = "本版對該域的取樣方式自述";

    /// <summary>一個量測域的預算陳述。</summary>
    /// <param name="Id">域代號，會併進 <see cref="FamilyPrefix"/> 成為事實鍵。</param>
    /// <param name="Name">給人看的名稱。</param>
    /// <param name="Window">取樣視窗：這個數字代表哪一段時間。</param>
    /// <param name="Formula">換算式：從讀到的原始量到畫面上的數字做了什麼。</param>
    /// <param name="Precision">精度／解析度，以及它的限制。</param>
    /// <param name="PrecisionOwner">精度由誰決定：<see cref="OwnerSelf"/> 或 <see cref="OwnerDriver"/>。</param>
    public sealed record BudgetRow(
        string Id, string Name, string Window, string Formula, string Precision, string PrecisionOwner);

    /// <summary>
    /// 預算表。每一列都要能被指回程式：視窗取自常數、換算取自同一支轉換、精度歸屬要說清楚。
    /// </summary>
    public static IReadOnlyList<BudgetRow> Rows { get; } =
    [
        new("cpu.util", "CPU 使用率",
            $"兩次 GetSystemTimes 之間的差分，窗長 {SystemUtilSampler.DifferentialWindowMs} ms",
            "100 × ((核心時間 + 使用者時間) − 閒置時間) ÷ (核心時間 + 使用者時間)",
            "時間戳單位 100 ns；但這個百分比的代表性由那 120 ms 內全系統的排程決定——它是一段窗的比值，不是瞬時值",
            OwnerSelf),
        new("mem.available", "可用實體記憶體",
            "瞬時快照（沒有平均窗）",
            "GlobalMemoryStatusEx 的 ullAvailPhys ÷ 1,048,576（顯示為 MB）",
            "1 MB 級（整數截斷）；「可用」含可立刻回收的待寫出頁面，這個定義由 OS 決定，不是本機選的",
            OwnerDriver),
        new("mt.cost.step", "逐收集步驟的讀取代價",
            "該步驟自己的壁鐘時間（含等待 I/O，不是 CPU 時間）",
            "Stopwatch.GetElapsedTime 取總秒數後 × 1000（毫秒）",
            "計時器頻率由 Stopwatch.Frequency 決定；數字含排程與 I/O 抖動，單次讀值不代表常態",
            OwnerSelf),
        new("mt.repeat.key", "逐鍵重複性",
            "同一把鍵連量 5 輪、總預算 3 秒（每輪含 120 ms 差分窗）",
            "逐輪樣本取最小／最大／平均，另算跨度（最大−最小）與首末漂移（末−首）",
            "樣本數是 5，不是統計量：跨度與漂移只能說明「這 5 次之間變了多少」",
            OwnerSelf),
        new("sensor.temperature", "感測器溫度",
            "每次刷新一次快照（感測器儀表板每 5 秒一輪），不做平均",
            "驅動回報的攝氏值直接用，本機不換算",
            "解析度與準確度都由驅動與晶片決定；本機不給誤差上限，也不替它換算成別的標度",
            OwnerDriver),
        new("sensor.fan", "風扇轉速",
            "每次刷新一次快照（同上），不做平均",
            "驅動回報的 RPM 直接用",
            "由驅動的脈衝計數窗決定；低轉速或停轉時常回 0，那是回報值不是缺值",
            OwnerDriver),
        new("sensor.clock", "時脈",
            "每次刷新一次快照（同上），不做平均",
            "驅動回報的 MHz 直接用",
            "由驅動決定它報的是即時有效時脈還是倍頻乘積；本機無法從外部驗證",
            OwnerDriver),
        new("sensor.power", "功耗",
            "每次刷新一次快照（同上），不做平均",
            "驅動回報的瓦數直接用",
            "多數平台是驅動依電壓與電流估算的值，不是實測瓦數；誤差由驅動／平台決定",
            OwnerDriver),
    ];

    /// <summary>把預算表攤成事實；最後一筆是總覽。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at)
    {
        var facts = new List<HardwareFact>(Rows.Count + 1);
        foreach (var row in Rows.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            string value =
                $"視窗：{row.Window}；換算：{row.Formula}；精度：{row.Precision}（由{row.PrecisionOwner}決定）";
            facts.Add(new HardwareFact(FamilyPrefix + row.Id, Category, "預算：" + row.Name, value, "", Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null));
        }

        facts.Add(SummaryFact(at));
        return facts;
    }

    private static HardwareFact SummaryFact(DateTimeOffset at)
    {
        int self = Rows.Count(r => r.PrecisionOwner == OwnerSelf);
        int driver = Rows.Count(r => r.PrecisionOwner == OwnerDriver);

        string value =
            $"{Rows.Count} 個即時量測域有預算陳述：其中 {self} 個域的視窗與換算由本機決定"
            + $"（{string.Join("、", Rows.Where(r => r.PrecisionOwner == OwnerSelf).Select(r => r.Id))}），"
            + $"{driver} 個域的精度由驅動決定（{string.Join("、", Rows.Where(r => r.PrecisionOwner == OwnerDriver).Select(r => r.Id))}）"
            + "——後者本機不給誤差上限。沒有列在這裡的量測域，就是本版還沒有寫出預算的域。";

        return new HardwareFact(SummaryKey, Category, "量測預算", value, "域", Source,
            FactTrustLevel.Reported, false, at, Rows.Count, FactAvailability.Present, null);
    }
}
