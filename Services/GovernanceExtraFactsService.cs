using System.Diagnostics.Eventing.Reader;

namespace XinSpect;

/// <summary>
/// 治理補充事實（Vol2 推進第 119–122 項）：DS-002 DHA 未施測聲明、DM-001 自動維護狀態史、
/// PL-005 綠色零寫入邊界、HM-002 裝置錯誤碼變更（兩份快照之間的差異）。
/// </summary>
/// <remarks>
/// <para><b>全部唯讀</b>：DHA 只做「未施測」的誠實聲明（TPM attest 需要管理員與受控環境，本版不假裝施測）；
/// 維護史只讀 TaskScheduler／維護事件；零寫入邊界是<b>宣告＋掃描守門</b>（由測試釘住沒有對系統路徑的寫入）；
/// 錯誤碼變更是兩份快照的差異，不是連續事件史。</para>
/// </remarks>
public static class GovernanceExtraFactsService
{
    private const string CatDs = "資料安全深化";
    private const string CatDm = "排程與維護";
    private const string CatPl = "平台與包裝";
    private const string CatHm = "硬體事件史";

    public const string DhaKey = "ds.dha";
    public const string MaintKey = "dm.maint.summary";
    public const string PortableKey = "pl.portable";
    public const string ProblemDeltaKey = "hm.prob.delta";

    private const string TaskLog = "Microsoft-Windows-TaskScheduler/Operational";

    public delegate IReadOnlyList<(DateTimeOffset When, int Id, string Task)>? TaskEventProbe(int windowDays);
    public delegate IReadOnlyList<(string Name, uint ProblemCode)>? PnpProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        TaskEventProbe? events = null,
        PnpProbe? pnp = null,
        IReadOnlyList<(string Name, uint ProblemCode)>? previousProblems = null)
    {
        return
        [
            DhaFact(at),
            MaintFact(at, events ?? ReadTaskEvents),
            PortableFact(at),
            ProblemDeltaFact(at, pnp ?? ReadPnp, previousProblems),
        ];
    }

    // ── DS-002 DHA 未施測聲明 ───────────────────────────────────────────

    private static HardwareFact DhaFact(DateTimeOffset at)
        => new(DhaKey, CatDs, "DHA 裝置健康證明",
            "本版未施測：DHA（Device Health Attestation）需要 TPM 2.0 attest 端點與管理服務（MDM／Intune）簽發，"
            + "本工具不假裝施測、不產生假的健康憑證。要看的是：TPM 版本（已收錄）、MDM 註冊狀態（zr.mdm）與 Secure Boot（韌體頁）。",
            "", "未施測聲明（無本地 DHA 驗證通路）", FactTrustLevel.Unknown, false, at, null,
            FactAvailability.NotSupported,
            "未施測：DHA 需要雲端管理服務簽發與受控環境；本工具唯讀且零網路，無法也不應代為施測，也不假裝施測");

    // ── DM-001 自動維護狀態史 ───────────────────────────────────────────

    public static HardwareFact MaintFact(DateTimeOffset at, TaskEventProbe probe)
    {
        IReadOnlyList<(DateTimeOffset When, int Id, string Task)>? events;
        bool failed = false;
        try { events = probe(7); }
        catch { events = null; failed = true; }

        if (events is null)
            return new HardwareFact(MaintKey, CatDm, "自動維護狀態史", "", "", TaskLog,
                FactTrustLevel.Unknown, false, at, null,
                failed ? FactAvailability.ReadError : FactAvailability.NotSupported,
                failed ? "維護事件查詢失敗——讀不到就是不猜"
                       : $"{TaskLog} 未啟用——「沒有紀錄」不等於「維護沒跑過」");

        var maint = events.Where(e =>
            e.Task.Contains("Maintenance", StringComparison.OrdinalIgnoreCase)
            || e.Task.Contains("TaskScheduler", StringComparison.OrdinalIgnoreCase)).ToList();
        int finished = maint.Count(e => e.Id == 201);
        var last = maint.OrderByDescending(e => e.When).FirstOrDefault();
        return new HardwareFact(MaintKey, CatDm, "自動維護狀態史",
            $"近 7 天維護相關事件 {maint.Count} 筆（動作完成 {finished} 筆）" +
            (last == default ? "；沒有維護事件" : $"；最近 {last.When:yyyy-MM-dd HH:mm} {(last.Id == 201 ? "完成" : "開始")}") +
            "。界線：事件是痕跡不是現況，完成事件也不代表維護成功（結果碼不在本項宣稱範圍）。",
            "筆", $"{TaskLog}（任務名含 Maintenance／TaskScheduler）", FactTrustLevel.Measured, false, at, maint.Count);
    }

    private static IReadOnlyList<(DateTimeOffset, int, string)>? ReadTaskEvents(int windowDays)
    {
        var since = DateTime.Now.AddDays(-Math.Max(1, windowDays));
        var query = new EventLogQuery(TaskLog, PathType.LogName,
            $"*[System[(EventID=200 or EventID=201) and TimeCreated[@SystemTime>='{since.ToUniversalTime():o}']]]");
        try
        {
            using var reader = new EventLogReader(query);
            var list = new List<(DateTimeOffset, int, string)>();
            for (int i = 0; i < 400; i++)
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

    // ── PL-005 綠色零寫入邊界 ───────────────────────────────────────────

    public static HardwareFact PortableFact(DateTimeOffset at)
        => new(PortableKey, CatPl, "綠色零寫入邊界",
            "邊界宣告：硬體事實的收集路徑<b>不寫任何檔案</b>；本程式的寫入只發生在使用者明示動作——"
            + "設定檔（%APPDATA%\\XinSpect）、審計日誌、時間膠囊／匯出檔（路徑由使用者指定）、以及明示授權的修復備份。"
            + "守門：Tests/PortableBoundaryTests 掃 Services/ 原始碼，禁止對系統目錄與其他使用者設定檔的寫入。",
            "", "原始碼掃描守門（PortableBoundaryTests）", FactTrustLevel.Derived, false, at);

    // ── HM-002 裝置錯誤碼變更（兩份快照）────────────────────────────────

    public static HardwareFact ProblemDeltaFact(DateTimeOffset at, PnpProbe probe,
        IReadOnlyList<(string Name, uint ProblemCode)>? previous)
    {
        IReadOnlyList<(string Name, uint ProblemCode)>? current;
        try { current = probe(); }
        catch { current = null; }
        if (current is null)
            return new HardwareFact(ProblemDeltaKey, CatHm, "裝置錯誤碼變更", "", "", "WMI Win32_PnPEntity",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, "PnP 問題碼列舉失敗——讀不到就是不猜");
        if (previous is null)
            return new HardwareFact(ProblemDeltaKey, CatHm, "裝置錯誤碼變更", "", "", "兩份 PnP 快照",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有上一份問題碼快照——變更需要兩份快照，本項不做連續事件史");

        var prev = previous.ToDictionary(p => p.Name, p => p.ProblemCode, StringComparer.OrdinalIgnoreCase);
        var added = current.Where(c => !prev.ContainsKey(c.Name)).Select(c => c.Name).ToList();
        var cleared = prev.Keys.Where(k => current.All(c => !string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))).ToList();
        var changed = current.Where(c => prev.TryGetValue(c.Name, out uint p) && p != c.ProblemCode)
            .Select(c => $"{c.Name}（{prev[c.Name]}→{c.ProblemCode}）").ToList();

        return new HardwareFact(ProblemDeltaKey, CatHm, "裝置錯誤碼變更",
            $"與上一份快照相比：新出現 {added.Count}・消失 {cleared.Count}・代碼改變 {changed.Count}" +
            (changed.Count > 0 ? "；改變：" + string.Join("、", changed.Take(5)) : "") +
            "。界線：兩份快照之間的差異，不是連續事件史；代碼改變是事實，根因不由此推斷。",
            "項", "兩份 PnP 快照（ConfigManagerErrorCode）", FactTrustLevel.Derived, false, at,
            added.Count + cleared.Count + changed.Count);
    }

    private static IReadOnlyList<(string, uint)>? ReadPnp()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity");
            var list = new List<(string, uint)>();
            foreach (System.Management.ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    uint code = o["ConfigManagerErrorCode"] is { } v ? System.Convert.ToUInt32(v) : 0u;
                    list.Add((o["Name"] as string ?? "(未命名)", code));
                }
            }
            return list.Count == 0 ? null : list;
        }
        catch { return null; }
    }
}
