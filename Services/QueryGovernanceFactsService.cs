namespace XinSpect;

/// <summary>
/// 查詢治理事實（Vol2 推進第 110–112 項）：QS-008 查詢歷史、QS-010 文法版本、UF-005 更新時間軸。
/// </summary>
/// <remarks>
/// <para><b>QS-008（L1）</b>：查詢歷史只留在記憶體、只記「查了什麼字串」不記結果，且永不落盤；
/// 標敏感讓匯出預設遮蔽。</para>
/// <para><b>QS-010</b>：查詢文法版本語意化（semver）——只有文法改變才動 major／minor，
/// 事實只是把版本與規則講清楚。</para>
/// <para><b>UF-005</b>：更新時間軸是<b>收口</b>——把既有的 wu.history.* 事實湊成時間軸摘要，
/// 不重讀 Windows Update（那條路徑已有專屬服務與測試）。</para>
/// </remarks>
public static class QueryGovernanceFactsService
{
    private const string CatQs = "查詢與智慧層";
    private const string CatUf = "更新深化";
    private const string CatPl = "平台與包裝";

    public const string HistoryKey = "qs.history";
    public const string GrammarKey = "qs.grammar";
    public const string TimelineKey = "uf.timeline";

    /// <summary>查詢文法版本：只在前述文法改變時遞增（semver）。</summary>
    public const string GrammarVersion = "1.0.0";

    /// <summary>QS-008：把查詢歷史攤成事實。沒查過就明說沒查過——不是「沒有隱私風險」。</summary>
    public static HardwareFact HistoryFact(DateTimeOffset at, IReadOnlyList<string> history)
    {
        if (history.Count == 0)
            return new HardwareFact(HistoryKey, CatQs, "查詢歷史",
                "", "", "QueryResultCache（記憶體，不落盤）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable,
                "本次執行還沒查詢過——歷史只留在記憶體、不落盤（L1：使用者查了什麼不寫檔）");

        return new HardwareFact(HistoryKey, CatQs, "查詢歷史",
            $"本次執行的 {history.Count} 次查詢（僅查詢字串，不含結果）：{string.Join(" → ", history.Take(10))}" +
            (history.Count > 10 ? $"…等 {history.Count} 次" : "") +
            "。界線：只在記憶體、不落盤、不進快照；標敏感讓匯出預設遮蔽。",
            "次", "QueryResultCache（記憶體環狀緩衝，上限 50）", FactTrustLevel.Reported, true, at, history.Count);
    }

    /// <summary>QS-010：文法版本事實。</summary>
    public static HardwareFact GrammarFact(DateTimeOffset at)
        => new(GrammarKey, CatQs, "查詢文法版本",
            $"查詢文法版本 {GrammarVersion}（semver）。規則：只有文法改變才動 major／minor，patch 只修文件與訊息；" +
            "版本字串由測試釘住（QueryAndTrustFactsTests 的版本斷言）。",
            "", "QueryGovernanceFactsService.GrammarVersion", FactTrustLevel.Derived, false, at);

    /// <summary>UF-005：由 wu.history.* 收口成時間軸摘要；沒有那些事實就明說沒有。</summary>
    public static HardwareFact TimelineFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        HardwareFact? Pick(string key) => facts.FirstOrDefault(f => f.Key == key);
        var latest = Pick("wu.history.latest");
        var total = Pick("wu.history.total");
        var d30 = Pick("wu.history.30d");
        var failed = Pick("wu.history.failed");
        if (latest is null && total is null && d30 is null && failed is null)
            return new HardwareFact(TimelineKey, CatUf, "更新時間軸",
                "", "", "wu.history.*（Windows Update 歷史）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable,
                "本輪沒有 Windows Update 歷史事實——時間軸需要 wu.history.* 先被讀到（沒讀到不等於沒有更新）");

        string brief = string.Join("・", new[]
        {
            latest is null ? null : $"最近 {latest.Value}",
            total is null ? null : $"總數 {total.Value}",
            d30 is null ? null : $"近 30 天 {d30.Value}",
            failed is null ? null : $"失敗 {failed.Value}",
        }.Where(s => s is not null));
        return new HardwareFact(TimelineKey, CatUf, "更新時間軸",
            brief + "。界線：這是既有更新歷史的彙總（收口），不是新的掃描；失敗次數是歷史痕跡不是目前狀態。",
            "", "wu.history.* 收口", FactTrustLevel.Derived, false, at);
    }

    /// <summary>PL-009：CLI 轉發單一實例——描述既有機制（訊號喚回主視窗），不改行為。
    /// 訊號是否還在由 QueryGovernanceFactsTests 掃 App.xaml.cs 釘住。</summary>
    public static HardwareFact ForwardFact(DateTimeOffset at)
        => new HardwareFact("pl.forward", CatPl, "CLI 轉發單一實例",
            "已實作單一實例機制：第二份實例以具名事件（Local\\XinSpect.SingleInstance…）請既有實例喚回主視窗。" +
            "界線：這是「喚回視窗」的轉發，不是把 CLI 參數交給既有實例處理（參數轉發不在本版宣稱範圍）。",
            "", "App.xaml.cs 具名事件", FactTrustLevel.Reported, false, at);
}
