using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;

namespace XinSpect;

/// <summary>
/// 維護與排程事實（Vol2 推進第 104–108 項）：DM-002 Storage Sense、DM-004 維護視窗、
/// DM-005 清理處理器、DM-006 任務運行時間線、DM-008 維護摘要。
/// </summary>
/// <remarks>
/// <para><b>全部唯讀</b>：只讀登錄設定與 Task Scheduler 事件（200＝動作開始、201＝動作完成）；
/// 不觸發任何維護工作、不刪任何檔案。</para>
/// <para><b>界線</b>：維護視窗衝突要「任務清單 × 視窗」才有意義，本項只陳述視窗設定本身；
/// 任務事件是痕跡，不是現況；摘要只彙總本組，不是整個系統的維護判決。</para>
/// </remarks>
public static class MaintenanceDashboardFactsService
{
    private const string Cat = "排程與維護";
    private const string TaskLog = "Microsoft-Windows-TaskScheduler/Operational";

    public const string SenseKey = "dm.sense";
    public const string ConflictKey = "dm.conflict";
    public const string CleanKey = "dm.clean";
    public const string TimelineKey = "dm.timeline.summary";
    public const string DashKey = "dm.dash";

    public delegate object? ValueProbe(string path, string valueName);
    public delegate IReadOnlyList<string>? SubKeysProbe(string path);
    public delegate IReadOnlyList<(DateTimeOffset When, int Id, string Task)>? TaskEventProbe(int windowHours);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        ValueProbe? values = null,
        SubKeysProbe? subKeys = null,
        TaskEventProbe? events = null)
    {
        var v = values ?? ReadValue;
        var facts = new List<HardwareFact>(5)
        {
            SenseFact(at, v),
            ConflictFact(at, v),
            CleanFact(at, subKeys ?? ReadSubKeys),
        };
        facts.AddRange(TimelineFacts(at, events ?? ReadTaskEvents));
        facts.Add(DashFact(at, facts));
        return facts;
    }

    // ── DM-002 Storage Sense ────────────────────────────────────────────

    private static HardwareFact SenseFact(DateTimeOffset at, ValueProbe v)
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy";
        object? on;
        object? cadence;
        try
        {
            on = v(path, "01");
            cadence = v(path, "2048");
        }
        catch { return new HardwareFact(SenseKey, Cat, "Storage Sense 狀態", "", "", @"HKCU\...\StoragePolicy",
            FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, "Storage Sense 登錄讀取失敗——不推測"); }

        if (on is null)
            return new HardwareFact(SenseKey, Cat, "Storage Sense 狀態",
                "未設定（Storage Sense 預設關閉；使用者可自行開啟）——0 是值、不是缺席。",
                "", @"HKCU\...\StoragePolicy", FactTrustLevel.Reported, false, at, 0,
                FactAvailability.NotApplicable, "Storage Sense 從未被設定過（未啟用不是錯誤）");

        int state = on is int i ? i : int.TryParse(on.ToString(), out var p) ? p : -1;
        return new HardwareFact(SenseKey, Cat, "Storage Sense 狀態",
            $"Storage Sense {(state == 1 ? "已啟用" : "已停用")}（01={on}）" +
            (cadence is null ? "" : $"・清理節奏碼 {cadence}") +
            "。界線：只陳述設定；實際清了什麼要看維護事件與清理處理器。",
            "", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy",
            FactTrustLevel.Reported, false, at, state);
    }

    // ── DM-004 維護視窗 ────────────────────────────────────────────────

    private static HardwareFact ConflictFact(DateTimeOffset at, ValueProbe v)
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance";
        object? wake;
        object? delay;
        try
        {
            wake = v(path, "WakeUpTime");
            delay = v(path, "RandomDelay");
        }
        catch { return new HardwareFact(ConflictKey, Cat, "維護視窗衝突", "", "", @"HKLM\...\Schedule\Maintenance",
            FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, "維護視窗登錄讀取失敗——不推測"); }

        if (wake is null && delay is null)
            return new HardwareFact(ConflictKey, Cat, "維護視窗衝突",
                "維護視窗未設定（系統預設：每日 02:00、隨機延遲預設值）——0 是值、不是缺席。",
                "", @"HKLM\...\Schedule\Maintenance", FactTrustLevel.Reported, false, at, 0,
                FactAvailability.NotApplicable, "未見 WakeUpTime／RandomDelay 覆寫（用系統預設）");

        return new HardwareFact(ConflictKey, Cat, "維護視窗衝突",
            $"維護視窗：WakeUpTime={wake ?? "（未設）"}・RandomDelay={delay ?? "（未設）"}。" +
            "界線：是否與其他排程衝突要「任務清單 × 視窗」交叉才算——本項只陳述視窗設定。",
            "", @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance",
            FactTrustLevel.Reported, false, at);
    }

    // ── DM-005 清理處理器 ──────────────────────────────────────────────

    private static HardwareFact CleanFact(DateTimeOffset at, SubKeysProbe probe)
    {
        IReadOnlyList<string>? handlers;
        try { handlers = probe(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches"); }
        catch { handlers = null; }
        if (handlers is null)
            return new HardwareFact(CleanKey, Cat, "磁碟清理處理器", "", "", @"HKLM\...\VolumeCaches",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, "VolumeCaches 列舉失敗——讀不到就是不猜");

        return new HardwareFact(CleanKey, Cat, "磁碟清理處理器",
            $"已註冊 {handlers.Count} 個清理處理器（VolumeCaches 子鍵）。" +
            "界線：只數「有哪些清理器被註冊」，不執行清理、也不判斷該不該清；預設勾選狀態在子鍵值裡（本項不解析）。",
            "個", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches",
            FactTrustLevel.Measured, false, at, handlers.Count);
    }

    private static IReadOnlyList<string>? ReadSubKeys(string path)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            return key?.GetSubKeyNames().ToList() ?? [];
        }
        catch { return null; }
    }

    // ── DM-006 任務運行時間線 ──────────────────────────────────────────

    private static IReadOnlyList<HardwareFact> TimelineFacts(DateTimeOffset at, TaskEventProbe probe)
    {
        IReadOnlyList<(DateTimeOffset When, int Id, string Task)>? events;
        bool failed = false;
        try { events = probe(24); }
        catch { events = null; failed = true; }

        if (events is null)
            return [new HardwareFact(TimelineKey, Cat, "任務運行時間線", "", "", TaskLog,
                FactTrustLevel.Unknown, false, at, null,
                failed ? FactAvailability.ReadError : FactAvailability.NotSupported,
                failed ? "任務事件查詢失敗——讀不到就是不猜"
                       : $"{TaskLog} 未啟用或不存在——「沒有紀錄」不等於「沒有任務跑過」")];

        var ordered = events.OrderByDescending(e => e.When).ToList();
        int started = ordered.Count(e => e.Id == 200);
        int finished = ordered.Count(e => e.Id == 201);
        string recent = ordered.Count == 0
            ? "近 24 小時沒有 200／201 事件"
            : "最近：" + string.Join("、", ordered.Take(5).Select(e => $"{e.When:HH:mm} {(e.Id == 200 ? "開始" : "完成")} {e.Task}"));
        return [new HardwareFact(TimelineKey, Cat, "任務運行時間線",
            $"近 24 小時：開始 {started}、完成 {finished}・{recent}。" +
            "界線：事件是痕跡不是現況（完成事件不代表成功——結果碼要看事件內容，本項只數時間線）。",
            "筆", $"{TaskLog} 事件 200/201", FactTrustLevel.Measured, false, at, started + finished)];
    }

    private static IReadOnlyList<(DateTimeOffset, int, string)>? ReadTaskEvents(int windowHours)
    {
        var since = DateTime.Now.AddHours(-Math.Max(1, windowHours));
        var query = new EventLogQuery(TaskLog, PathType.LogName,
            $"*[System[(EventID=200 or EventID=201) and TimeCreated[@SystemTime>='{since.ToUniversalTime():o}']]]");
        try
        {
            using var reader = new EventLogReader(query);
            var list = new List<(DateTimeOffset, int, string)>();
            for (int i = 0; i < 300; i++)
            {
                using var evt = reader.ReadEvent();
                if (evt is null) break;
                string task = evt.Properties.Count > 0 ? evt.Properties[0].Value?.ToString() ?? "(未命名)" : "(未命名)";
                list.Add((new DateTimeOffset(evt.TimeCreated ?? DateTime.Now), evt.Id, task));
            }
            return list;
        }
        catch (EventLogNotFoundException) { return null; }
        catch { return null; }
    }

    // ── DM-008 維護摘要（分母不含自己）────────────────────────────────

    private static HardwareFact DashFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        var dm = facts.Where(f => f.Category == Cat).ToList();
        int present = dm.Count(f => f.Availability == FactAvailability.Present);
        int na = dm.Count(f => f.Availability == FactAvailability.NotApplicable);
        int other = dm.Count - present - na;
        return new HardwareFact(DashKey, Cat, "維護摘要",
            $"維護面 {dm.Count} 項：可陳述 {present}・不適用 {na}・其他 {other}。" +
            "界線：這是可用性計數，不是「維護做得好不好」的判決。",
            "項", "本組事實的可用性（輸入不含自己）", FactTrustLevel.Derived, false, at, present);
    }

    private static object? ReadValue(string path, string valueName)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path)
                        ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
        if (key is null) return null;   // 鍵不存在＝未設定（事實），不是讀不到
        return key.GetValue(valueName);
    }
}
