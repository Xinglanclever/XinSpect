namespace XinSpect;

/// <summary>
/// 效能預算定義（V7 WP43／A54）：預算是<b>測試</b>不是文件——超標就紅燈。
/// 量測必須可重複、不得依賴外部狀態：因此三個執行期指標（冷啟動／常駐記憶體／CPU 閒置）
/// 標 <see cref="RuntimeOnly"/>——由 SelfTelemetry 在應用程式內記錄，單元測試不量它們
/// （單元測試裡量這三個必然不可重複，硬塞進去反而違反誠實）；可重複的兩項
/// （全套掃描編排、報告產生）由 <c>PerformanceBudgetTests</c> 真量真擋。
/// 門檻照 V7 A54 原值：3 秒／300 MB／1%／60 秒／5 秒——先量現況，超標就修程式，不放寬門檻。
/// </summary>
public sealed record PerfBudget(
    string MetricId,
    string DisplayName,
    double Threshold,
    string Unit,
    string MeasureNote,
    bool RuntimeOnly)
{
    public static readonly PerfBudget ColdStart = new("cold-start", "冷啟動", 3, "秒", "首次視窗可見", RuntimeOnly: true);
    public static readonly PerfBudget ResidentMemory = new("resident-memory", "常駐記憶體", 300, "MB", "閒置 60 秒後", RuntimeOnly: true);
    public static readonly PerfBudget IdleCpu = new("idle-cpu", "CPU 閒置", 1, "%", "閒置 60 秒平均", RuntimeOnly: true);
    public static readonly PerfBudget FullScan = new("full-scan", "全套掃描（編排）", 60, "秒",
        "所有已落地的事實收集——可重複量測的部分是編排時間（假讀取器），真實硬體讀取時間由 SelfTelemetry 記錄", RuntimeOnly: false);
    public static readonly PerfBudget HtmlReport = new("html-report", "報告產生", 5, "秒", "HTML 單檔", RuntimeOnly: false);

    public static readonly IReadOnlyList<PerfBudget> All =
        [ColdStart, ResidentMemory, IdleCpu, FullScan, HtmlReport];
}
