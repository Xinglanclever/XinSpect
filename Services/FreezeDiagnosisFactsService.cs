namespace XinSpect;

/// <summary>
/// 「電腦為什麼當」的事件面（Vol 2 批次 D／SG-011）：把非預期關機、藍屏、WHEA 硬體錯誤、
/// 顯示驅動逾時四類事件收成事實，並附上最近幾筆的時間與種類。
/// <para>
/// <b>與既有功能的關係：</b>「可靠性歷史」頁（<see cref="ReliabilityHistoryService"/>）早就用同一批事件
/// 畫時間軸，但那些資料沒有事實鍵、進不了 CLI／報告／時間膠囊。這一支補的是<b>事實層</b>：
/// 同一批事件、同一組來源，輸出成可比較的事實。
/// </para>
/// <para>
/// <b>界線：</b>①事件是<b>觀察</b>不是診斷——非預期關機可能是停電、電源、硬體、驅動；
/// ②WHEA 是硬體主動報告的錯誤，與當機的因果需人工判讀；③本項<b>不評級</b>，
/// 也不把「沒有事件」寫成「沒問題」（記錄可能被清、也可能只是還沒發生）。
/// </para>
/// </summary>
public static class FreezeDiagnosisFactsService
{
    public const string Category = "情境包";
    public const string SummaryKey = "sg.freeze";

    private const string Source = "事件記錄 System ＋ WHEA-Logger";
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);
    private const int RecentCount = 5;

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<AnnotatedEvent>?>? probe = null, TimeSpan? window = null)
    {
        TimeSpan span = window ?? DefaultWindow;
        IReadOnlyList<AnnotatedEvent>? events;
        try { events = (probe ?? BaselineLearningService.RealEvents(span))(); }
        catch { events = null; }

        if (events is null)
            return
            [
                new HardwareFact(SummaryKey, Category, "電腦為什麼當", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    "事件記錄查詢失敗（頻道不存在或權限不足）——讀不到就是不猜，不畫成 0 次"),
            ];

        var annotations = EventAnnotationLayer.FromEvents(events);
        var byKind = annotations.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());

        string kinds = byKind.Count == 0
            ? "四類事件都沒有（記錄可能被清理，或這段期間真的都沒發生）"
            : string.Join("、", byKind.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value} 次"));

        // 最近幾筆串在同一個值裡（不另立鍵）：這一版刻意不新增動態家族，
        // 而「最近五筆事件」的資訊量不需要五把鍵。
        var recent = annotations.OrderByDescending(a => a.At).Take(RecentCount).ToList();
        string recentText = recent.Count == 0
            ? "—"
            : string.Join("；", recent.Select(a => $"{a.At.ToLocalTime():yyyy-MM-dd HH:mm} {a.Kind}"));

        return
        [
            new HardwareFact(SummaryKey, Category, "電腦為什麼當",
                $"視窗 {span.TotalDays:0} 天：{kinds}・可標註 {annotations.Count} 筆・最近：{recentText}。" +
                "事件是觀察不是診斷——非預期關機可能是停電、電源、硬體或驅動；" +
                "WHEA 是硬體主動報告的錯誤，與當機的因果需人工判讀。本項不做紅黃判決。",
                "次", Source, FactTrustLevel.Reported, false, at, annotations.Count),
        ];
    }
}
