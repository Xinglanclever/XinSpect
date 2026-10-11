using System.Diagnostics.Eventing.Reader;

namespace XinSpect;

/// <summary>
/// 任務歷史痕跡（Vol2 FS-010）：<c>Microsoft-Windows-TaskScheduler/Operational</c> 的
/// 工作登錄（106）與刪除（102）事件，聚成時間窗內的事實。
/// </summary>
/// <remarks>
/// <para><b>這是痕跡不是現況</b>：「最近 7 天有 3 個工作被登錄」講的是<b>發生過什麼</b>，
/// 不是「現在有幾個工作」——現況在排程工作頁。兩者混為一談會讓「0 筆痕跡」被讀成「沒有工作」。</para>
/// <para><b>誠實界線</b>：這個記錄通道在許多機器上<b>預設關閉</b>（未啟用時查得到 0 筆而不是錯誤）；
/// 因此查詢失敗標 <c>ReadError</c>、通道不存在或未啟用標 <c>NotSupported</c>，
/// 兩者都不會寫成「0 筆事件」——「沒開記錄」與「沒發生過」是完全不同的兩件事。</para>
/// </remarks>
public static class TaskHistoryFactsService
{
    private const string Category = "檔案鑑識";
    private const string LogName = "Microsoft-Windows-TaskScheduler/Operational";

    /// <summary>一條任務痕跡事件。</summary>
    public readonly record struct TaskTrace(DateTimeOffset When, int EventId, string TaskName)
    {
        /// <summary>106＝工作已登錄、102＝工作已刪除（Task Scheduler Operational 的標準事件碼）。</summary>
        public string Kind => EventId switch { 106 => "登錄", 102 => "刪除", _ => $"事件{EventId}" };
    }

    /// <summary>
    /// 收集任務痕跡。<paramref name="probe"/> 回傳時間窗內事件（預設查真實事件記錄）；
    /// 回傳 null 表示<b>查不到通路</b>（記錄未啟用／不存在），與「回傳空清單」意義不同。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<TaskTrace>?>? probe = null, int windowDays = 7)
    {
        IReadOnlyList<TaskTrace>? traces;
        bool readFailed = false;
        try { traces = (probe ?? (() => Fetch(windowDays)))(); }
        catch { traces = null; readFailed = true; }

        if (traces is null)
        {
            var availability = readFailed ? FactAvailability.ReadError : FactAvailability.NotSupported;
            string reason = readFailed
                ? "Task Scheduler 操作記錄查詢失敗"
                : $"{LogName} 未啟用或不存在——「沒開記錄」不等於「沒發生過任務變更」";
            return
            [
                Na(at, "fs.taskhist.count", "任務痕跡筆數", reason, availability),
                Na(at, "fs.taskhist.last", "最近任務痕跡", reason, availability),
                Na(at, "fs.taskhist.summary", "任務痕跡摘要", reason, availability),
            ];
        }

        var ordered = traces.OrderByDescending(t => t.When).ToList();
        var last = ordered.FirstOrDefault();
        int registered = ordered.Count(t => t.EventId == 106);
        int deleted = ordered.Count(t => t.EventId == 102);

        return
        [
            new HardwareFact("fs.taskhist.count", Category, "任務痕跡筆數",
                ordered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), $"筆（近 {windowDays} 天）",
                $"{LogName} 事件 106/102", FactTrustLevel.Measured, false, at, ordered.Count),
            new HardwareFact("fs.taskhist.last", Category, "最近任務痕跡",
                last == default
                    ? $"（近 {windowDays} 天無 106/102 事件——記錄是開著的，只是沒發生）"
                    : $"{last.When:yyyy-MM-dd HH:mm} {last.Kind} {last.TaskName}",
                "", $"{LogName} 事件 106/102", FactTrustLevel.Measured, false, at),
            new HardwareFact("fs.taskhist.summary", Category, "任務痕跡摘要",
                $"近 {windowDays} 天：登錄 {registered} 筆、刪除 {deleted} 筆"
                + (ordered.Count > 0 ? "；最近幾筆：" + string.Join("、", ordered.Take(5).Select(t => $"{t.When:MM-dd HH:mm} {t.Kind} {t.TaskName}")) : ""),
                "", $"{LogName} 事件 106/102（痕跡非現況；工作現況見排程工作頁）", FactTrustLevel.Measured, false, at),
        ];
    }

    /// <summary>真實通路：讀近 N 天的 106／102 事件。記錄不存在時回 null（由呼叫端標 NotSupported）。</summary>
    public static IReadOnlyList<TaskTrace>? Fetch(int windowDays)
    {
        var since = DateTime.Now.AddDays(-Math.Max(1, windowDays));
        var query = new EventLogQuery(LogName, PathType.LogName,
            $"*[System[(EventID=106 or EventID=102) and TimeCreated[@SystemTime>='{since.ToUniversalTime():o}']]]");
        try
        {
            using var reader = new EventLogReader(query);
            var list = new List<TaskTrace>();
            for (int i = 0; i < 500; i++) // 上限 500 筆：痕跡是給人看的摘要，不是全量傾印
            {
                using var evt = reader.ReadEvent();
                if (evt is null) break;
                string task = evt.Properties.Count > 0 ? evt.Properties[0].Value?.ToString() ?? "(未命名)" : "(未命名)";
                list.Add(new TaskTrace(new DateTimeOffset(evt.TimeCreated ?? DateTime.Now), evt.Id, task));
            }
            return list;
        }
        catch (EventLogNotFoundException) { return null; }
        catch { return null; }
    }

    private static HardwareFact Na(DateTimeOffset at, string key, string name, string reason, FactAvailability availability)
        => new(key, Category, name, "", "", LogName, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
