using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text.Json;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 維護與裝置健康事實組（Vol2 推進第 32–41 項）：IN-008 設定自我修復（先備份）、NG-003 檢查進度記憶、
/// NG-004 診斷報告整合、HM-001／002／008 裝置健康、FS-008 BITS 佇列、FS-011 安裝痕跡、
/// UF-006 更新來源、UF-009 服務堆疊版本。
/// </summary>
/// <remarks>
/// <para><b>啟動路徑全程唯讀</b>：唯一的寫入是使用者明示觸發的 <see cref="RepairConfig"/>（先備份再重建），
/// 收集本身不動任何檔。</para>
/// <para><b>界線（寫在值裡）</b>：裝置「未知／錯誤碼」是現況快照，缺驅動不等於故障；
/// 事件窗講的是「發生過什麼」不是「現在有什麼」；更新來源講「從哪裡更新」不是「有沒有更新」；
/// 報告整合與進度記憶只陳述本工具知道什麼，不給健康分數。</para>
/// <para><b>隱私</b>：安裝痕跡的產品名稱是軟體清單層（L1），值標 sensitive——匯出預設遮蔽；
/// BITS 只報事件計數，不讀下載內容與 URL。</para>
/// </remarks>
public static class MaintenanceFactsService
{
    private const string CatSelf = "自我完整性";
    private const string CatNav = "診斷導航";
    private const string CatDev = "硬體事件史";
    private const string CatFs = "檔案鑑識";
    private const string CatUf = "更新深化";

    public const string CfgRepairKey = "in.cfg.repair";
    public const string ProgressKey = "ng.progress";
    public const string ReportKey = "ng.report";
    public const string TreeKey = "hm.tree";
    public const string UnknownKey = "hm.unknown.count";
    public const string UnknownListKey = "hm.unknown.list";
    public const string ProbKey = "hm.prob.summary";
    public const string BitsCountKey = "fs.bits.count";
    public const string BitsSummaryKey = "fs.bits.summary";
    public const string InstCountKey = "fs.insthist.count";
    public const string InstSummaryKey = "fs.insthist.summary";
    public const string SourceKey = "uf.source";
    public const string ServicingKey = "uf.servicing";

    private const string BitsLogName = "Microsoft-Windows-Bits-Client/Operational";
    private const string MsiProviderName = "MsiInstaller";
    private const string MsiLogName = "Application";
    /// <summary>CM_PROB_PHANTOM（不在場的幽靈裝置）＝45（DevCon／CM_PROB 對照表）。</summary>
    public const uint CmProbNotConnected = 45;

    // ── 注入接縫 ─────────────────────────────────────────────────────────

    /// <summary>PnP 裝置一筆（名稱＋Config Manager 錯誤碼）；在場與否由錯誤碼 45 分辨。</summary>
    public sealed record PnpDevice(string Name, uint? ProblemCode)
    {
        public bool IsPresent => ProblemCode is not CmProbNotConnected;
    }

    public delegate IReadOnlyList<PnpDevice> PnpProbe();

    /// <summary>事件記錄一筆。</summary>
    public readonly record struct TraceEvent(DateTimeOffset When, int Id, string Text);

    /// <summary>事件窗讀取：回 null＝通道不存在／未啟用（與「回空清單＝沒發生過」意義不同）。</summary>
    public delegate IReadOnlyList<TraceEvent>? EventProbe(string channel, int windowDays);

    /// <summary>登錄值讀取（值名 → 物件；讀不到回 null）。</summary>
    public delegate object? RegistryProbe(string valueName);

    /// <summary>設定檔狀態（路徑、存在、JSON 是否合法、錯誤附註）。</summary>
    public sealed record ConfigState(string Path, bool Exists, bool JsonValid, string? ErrorNote);

    // ── 收集入口 ─────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        PnpProbe? pnp = null,
        EventProbe? bitsProbe = null,
        EventProbe? msiProbe = null,
        RegistryProbe? registry = null,
        ConfigState? config = null,
        IReadOnlyList<AuditEntry>? audit = null)
    {
        var facts = new List<HardwareFact>(11)
        {
            CfgRepairFact(at, config ?? ReadConfigState(SettingsService.FilePath)),
            ProgressFact(at, audit ?? LoadAuditSafe()),
        };
        facts.AddRange(DeviceFacts(at, pnp ?? ReadPnp));
        facts.AddRange(BitsFacts(at, bitsProbe ?? ReadEvents));
        facts.AddRange(InstallerFacts(at, msiProbe ?? ReadEvents));
        facts.Add(UpdateSourceFact(at, registry ?? ReadRegistry));
        facts.Add(ServicingFact(at, registry ?? ReadRegistry));
        return facts;
    }

    // ── IN-008 設定自我修復 ──────────────────────────────────────────────

    /// <summary>讀設定檔狀態：不存在／合法／損毀／讀不到（權限）四種分開，不混為「沒問題」。</summary>
    public static ConfigState ReadConfigState(string path)
    {
        try
        {
            if (!File.Exists(path)) return new ConfigState(path, Exists: false, JsonValid: false, ErrorNote: null);
        }
        catch (Exception ex)
        {
            return new ConfigState(path, Exists: false, JsonValid: false, ErrorNote: "存在性檢查失敗：" + ex.Message);
        }

        string? text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { return new ConfigState(path, Exists: true, JsonValid: false, ErrorNote: "讀取失敗：" + ex.Message); }

        try
        {
            using var _ = JsonDocument.Parse(text);
            return new ConfigState(path, Exists: true, JsonValid: true, ErrorNote: null);
        }
        catch (JsonException ex)
        {
            return new ConfigState(path, Exists: true, JsonValid: false, ErrorNote: ex.Message);
        }
    }

    private static HardwareFact CfgRepairFact(DateTimeOffset at, ConfigState c)
    {
        if (!c.Exists)
            return Na(at, CfgRepairKey, CatSelf, "設定自我修復",
                c.ErrorNote ?? $"設定檔「{c.Path}」不存在（全程使用預設值）——沒有東西可以修，不是錯誤",
                FactAvailability.NotApplicable);

        if (c.ErrorNote is not null && c.ErrorNote.StartsWith("讀取失敗", StringComparison.Ordinal))
            return Na(at, CfgRepairKey, CatSelf, "設定自我修復",
                $"設定檔存在但{c.ErrorNote}——修復需要先讀到，不假裝可修", FactAvailability.ReadError);

        if (c.JsonValid)
            return new HardwareFact(CfgRepairKey, CatSelf, "設定自我修復",
                "設定檔 JSON 合法——無需修復。修復入口只在損毀時可用，啟動路徑絕不重寫。", "", "settings.json 解析",
                FactTrustLevel.Measured, false, at);

        return new HardwareFact(CfgRepairKey, CatSelf, "設定自我修復",
            $"設定檔 JSON 損毀（{c.ErrorNote ?? "解析失敗"}）。修復路徑＝使用者明示觸發：先備份留證" +
            "（settings.json.corrupt-<時間戳>），再以預設重建；本版啟動路徑不自動覆蓋。",
            "", "settings.json 解析", FactTrustLevel.Unknown, false, at, null,
            FactAvailability.Unknown, "設定檔損毀——既有值不予採信，等明示修復");
    }

    /// <summary>IN-008 的修復動作（唯一寫入路徑）：先把損毀原檔改名留證，再寫出最小合法 JSON。</summary>
    public static (bool Done, string Summary, string? Failure) RepairConfig(string? path = null)
    {
        string file = path ?? SettingsService.FilePath;
        var state = ReadConfigState(file);
        if (!state.Exists) return (false, "", "設定檔不存在——沒有東西可以修。");
        if (state.JsonValid) return (false, "", "設定檔 JSON 合法——不需要修復（不做無謂重寫）。");

        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string backup = file + ".corrupt-" + stamp;
            File.Copy(file, backup, overwrite: false);   // 同名備份已存在時直接失敗：證據不覆蓋
            File.WriteAllText(file, "{}");               // {}＝全部欄位回到預設值，之後由設定頁正常寫回
            return (true, $"已把損毀原檔留證為 {Path.GetFileName(backup)}，並以預設值重建設定檔——原檔內容未刪除。", null);
        }
        catch (Exception ex)
        {
            return (false, "", $"修復未完成（{ex.GetType().Name}：{ex.Message}）——原檔保持原狀。");
        }
    }

    // ── NG-003 檢查進度記憶 ──────────────────────────────────────────────

    private static IReadOnlyList<AuditEntry>? LoadAuditSafe()
    {
        try { return AuditLogService.Load(AuditLogService.DefaultPath); }
        catch { return null; }
    }

    /// <summary>進度記憶＝審計日誌裡「本工具做過什麼」的計數（append-only、鏈可驗）。</summary>
    public static HardwareFact ProgressFact(DateTimeOffset at, IReadOnlyList<AuditEntry>? audit)
    {
        if (audit is null)
            return Na(at, ProgressKey, CatNav, "檢查進度記憶",
                "審計日誌讀取失敗——無法回答「做過哪些檢查」（讀不到不等於沒做過）", FactAvailability.ReadError);
        if (audit.Count == 0)
            return Na(at, ProgressKey, CatNav, "檢查進度記憶",
                "審計日誌存在但 0 筆——沒有任何明示觸發的動作記錄（0 筆痕跡不等於硬體檢查沒跑過：唯讀收集刻意不進鏈）",
                FactAvailability.NotApplicable);

        var scopes = audit.GroupBy(e => e.Scope, StringComparer.Ordinal)
            .Select(g => (Scope: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count).ToList();
        var last = audit[^1];
        return new HardwareFact(ProgressKey, CatNav, "檢查進度記憶",
            $"審計記錄 {audit.Count} 筆、{scopes.Count} 個範圍；最近一筆 {last.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm}（{last.Action}／{last.Scope}）" +
            "・前幾個範圍：" + string.Join("、", scopes.Take(6).Select(x => $"{x.Scope}×{x.Count}")) +
            "。界線：這記的是「本工具做過什麼」，不是「硬體好不好」。",
            "筆", "審計日誌（append-only、雜湊鏈可驗）", FactTrustLevel.Derived, false, at, audit.Count);
    }

    // ── NG-004 診斷報告整合 ──────────────────────────────────────────────

    /// <summary>
    /// 報告整合：把「前面各組事實的可用性分布」攤成一欄。輸入刻意不含自己（與 cap.*／ng.coverage／qs.range 同款），
    /// 否則报告會把自己的存在算進分母。
    /// </summary>
    public static HardwareFact ReportFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        int total = facts.Count;
        int present = facts.Count(f => f.Availability == FactAvailability.Present);
        var others = facts.Where(f => f.Availability != FactAvailability.Present)
            .GroupBy(f => f.Availability).OrderByDescending(g => g.Count()).ToList();
        string distribution = others.Count == 0
            ? "全部可採信"
            : string.Join("、", others.Select(g => $"{g.Key} {g.Count()} 項"));
        int contradictions = facts.Count(f => f.Category == "交叉對帳" && f.Value.StartsWith("矛盾", StringComparison.Ordinal));

        return new HardwareFact(ReportKey, CatNav, "診斷報告整合",
            $"已收集 {total} 項事實：可採信 {present} 項・{distribution}；交叉對帳矛盾 {contradictions} 條。" +
            "界線：本欄只陳述「現在知道什麼、還不知道什麼」，沒有健康分數，也不把未知讀成正常。",
            "項", "全部事實組的可用性分布（輸入不含自己）", FactTrustLevel.Derived, false, at, total,
            total == 0 ? FactAvailability.Unknown : FactAvailability.Present,
            total == 0 ? "事實集是空的——還沒收集，不是「一切都好」" : null);
    }

    // ── HM-001／002／008 裝置健康 ────────────────────────────────────────

    private static IReadOnlyList<HardwareFact> DeviceFacts(DateTimeOffset at, PnpProbe probe)
    {
        IReadOnlyList<PnpDevice> devices;
        try
        {
            devices = probe();
        }
        catch (Exception ex)
        {
            string why = "PnP 裝置列舉失敗（" + ex.GetType().Name + "）——讀不到就是不猜，不讀成「沒有裝置」";
            return
            [
                Na(at, TreeKey, CatDev, "裝置樹健康總覽", why, FactAvailability.ReadError),
                Na(at, UnknownKey, CatDev, "未知裝置數", why, FactAvailability.ReadError),
                Na(at, UnknownListKey, CatDev, "未知裝置清單", why, FactAvailability.ReadError),
                Na(at, ProbKey, CatDev, "裝置錯誤碼摘要", why, FactAvailability.ReadError),
            ];
        }

        if (devices.Count == 0)
        {
            string why = "PnP 列舉回空清單——一支系统在用的機器不會沒有 PnP 裝置，這裡按讀取失敗處理";
            return
            [
                Na(at, TreeKey, CatDev, "裝置樹健康總覽", why, FactAvailability.ReadError),
                Na(at, UnknownKey, CatDev, "未知裝置數", why, FactAvailability.ReadError),
                Na(at, UnknownListKey, CatDev, "未知裝置清單", why, FactAvailability.ReadError),
                Na(at, ProbKey, CatDev, "裝置錯誤碼摘要", why, FactAvailability.ReadError),
            ];
        }

        int present = devices.Count(d => d.IsPresent);
        var unknown = devices.Where(d => d.IsPresent &&
            (string.IsNullOrWhiteSpace(d.Name)
             || d.Name.Contains("Unknown", StringComparison.OrdinalIgnoreCase)
             || d.Name.Contains("未知", StringComparison.Ordinal))).ToList();
        var problem = devices.Where(d => d.IsPresent && d.ProblemCode is { } c && c != 0).ToList();

        return
        [
            new HardwareFact(TreeKey, CatDev, "裝置樹健康總覽",
                $"列舉 {devices.Count} 個 PnP 裝置（在場 {present}、不在場 {devices.Count - present}）" +
                $"・有錯誤碼 {problem.Count} 個・名稱未知 {unknown.Count} 個。" +
                "界線：這是數量總覽，不是健康分數；樹狀逐項檢視在裝置診斷頁。",
                "個", "WMI Win32_PnPEntity", FactTrustLevel.Reported, false, at, devices.Count),
            new HardwareFact(UnknownKey, CatDev, "未知裝置數",
                $"{unknown.Count} 個在場裝置名稱未知／未辨識——缺驅動≠故障，只是 Windows 還不認得它。",
                "個", "WMI Win32_PnPEntity（Name）", FactTrustLevel.Reported, false, at, unknown.Count),
            new HardwareFact(UnknownListKey, CatDev, "未知裝置清單",
                unknown.Count == 0
                    ? "沒有在場的未知裝置（0 是值，不是缺席）"
                    : string.Join("；", unknown.Take(8).Select(d => d.Name)) +
                      (unknown.Count > 8 ? $"；等 {unknown.Count - 8} 項" : ""),
                "", "WMI Win32_PnPEntity（Name）", FactTrustLevel.Reported, false, at, unknown.Count),
            new HardwareFact(ProbKey, CatDev, "裝置錯誤碼摘要",
                problem.Count == 0
                    ? "沒有在場裝置回報非 0 的 Config Manager 錯誤碼（0 是值，不是缺席）"
                    : string.Join("；", problem.GroupBy(d => d.ProblemCode!.Value)
                        .OrderByDescending(g => g.Count())
                        .Select(g => $"代碼 {g.Key}×{g.Count()}（{string.Join("、", g.Take(3).Select(d => d.Name))}）")) +
                      "。代碼逐項意義見裝置診斷頁（DeviceDiagnosticRules 的對照表）；錯誤碼是事實不是故障判決。",
                "", "WMI Win32_PnPEntity（ConfigManagerErrorCode）", FactTrustLevel.Reported, false, at, problem.Count),
        ];
    }

    private static IReadOnlyList<PnpDevice> ReadPnp()
    {
        var list = new List<PnpDevice>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity");
        foreach (ManagementObject o in searcher.Get())
        {
            using (o)
            {
                uint? code = o["ConfigManagerErrorCode"] is { } v && v is not null ? Convert.ToUInt32(v) : null;
                list.Add(new PnpDevice(o["Name"] as string ?? "(未命名)", code));
            }
        }
        return list;
    }

    // ── FS-008 BITS 佇列 ─────────────────────────────────────────────────

    private static IReadOnlyList<HardwareFact> BitsFacts(DateTimeOffset at, EventProbe probe)
    {
        IReadOnlyList<TraceEvent>? events;
        bool failed = false;
        try { events = probe(BitsLogName, 7); }
        catch { events = null; failed = true; }

        if (events is null)
        {
            string why = failed
                ? $"{BitsLogName} 查詢失敗——讀不到就是不猜"
                : $"{BitsLogName} 通道不存在——「沒有這個記錄通道」不等於「這台機器沒用過 BITS 下載」";
            return
            [
                Na(at, BitsCountKey, CatFs, "BITS 佇列痕跡", why, failed ? FactAvailability.ReadError : FactAvailability.NotSupported),
                Na(at, BitsSummaryKey, CatFs, "BITS 佇列摘要", why, failed ? FactAvailability.ReadError : FactAvailability.NotSupported),
            ];
        }

        var ordered = events.OrderByDescending(e => e.When).ToList();
        int Of(int id) => ordered.Count(e => e.Id == id);
        // 59=傳輸完成、3=開始傳輸、4=暫停、10=建立、15=取消、16=錯誤、19=結束（BITS-Client Operational 標準事件碼）
        string summary = $"近 7 天：開始 {Of(3)}、完成 {Of(59)}、暫停 {Of(4)}、錯誤 {Of(16)}、取消 {Of(15)}" +
            "。界線：這是事件痕跡不是現況佇列（工作完成就离开佇列）；本項不讀下載內容與目標 URL。";
        return
        [
            new HardwareFact(BitsCountKey, CatFs, "BITS 佇列痕跡",
                ordered.Count.ToString(CultureInfo.InvariantCulture), "筆（近 7 天）",
                $"{BitsLogName}", FactTrustLevel.Measured, false, at, ordered.Count),
            new HardwareFact(BitsSummaryKey, CatFs, "BITS 佇列摘要", summary, "",
                $"{BitsLogName} 事件 3/4/15/16/59", FactTrustLevel.Measured, false, at),
        ];
    }

    // ── FS-011 安裝痕跡時間線 ────────────────────────────────────────────

    private static IReadOnlyList<HardwareFact> InstallerFacts(DateTimeOffset at, EventProbe probe)
    {
        IReadOnlyList<TraceEvent>? events;
        bool failed = false;
        try { events = probe(MsiLogName, 90); }
        catch { events = null; failed = true; }

        if (events is null)
        {
            string why = failed
                ? $"{MsiLogName} 記錄查詢失敗——讀不到就是不猜"
                : $"{MsiLogName} 記錄不可用（被清空或無權限）——查不到安裝事件不等於沒裝過軟體";
            return
            [
                Na(at, InstCountKey, CatFs, "安裝痕跡筆數", why, failed ? FactAvailability.ReadError : FactAvailability.NotSupported),
                Na(at, InstSummaryKey, CatFs, "安裝痕跡摘要", why, failed ? FactAvailability.ReadError : FactAvailability.NotSupported),
            ];
        }

        var ordered = events.OrderByDescending(e => e.When).ToList();
        int Of(int id) => ordered.Count(e => e.Id == id);
        string Kind(int id) => id switch { 1033 => "安裝", 1034 => "移除", 1035 => "修復", 1036 => "升級", _ => $"事件{id}" };
        string summary = $"近 90 天：安裝 {Of(1033)}、移除 {Of(1034)}、修復 {Of(1035)}、升級 {Of(1036)}" +
            (ordered.Count > 0 ? "；最近：" + string.Join("、", ordered.Take(5).Select(e => string.Join(" ", e.When.ToString("yyyy-MM-dd"), Kind(e.Id), ProductLabel(e.Text)))) : "") +
            "。界線：痕跡不是現況（移除後不會留下安裝記錄），產品名稱屬軟體清單（匯出預設遮蔽）。";
        return
        [
            new HardwareFact(InstCountKey, CatFs, "安裝痕跡筆數",
                ordered.Count.ToString(CultureInfo.InvariantCulture), "筆（近 90 天）",
                $"{MsiLogName}／{MsiProviderName} 事件 1033-1036", FactTrustLevel.Measured, false, at, ordered.Count),
            // 值內含產品名稱：L1 敏感，走既有遮蔽政策（匯出預設遮）
            new HardwareFact(InstSummaryKey, CatFs, "安裝痕跡摘要", summary, "",
                $"{MsiLogName}／{MsiProviderName} 事件 1033-1036", FactTrustLevel.Measured, true, at),
        ];
    }

    private static IReadOnlyList<TraceEvent>? ReadEvents(string channel, int windowDays)
    {
        var since = DateTime.Now.AddDays(-Math.Max(1, windowDays));
        string predicate = channel == MsiLogName
            ? $"*[System[Provider[@Name='{MsiProviderName}'] and (EventID=1033 or EventID=1034 or EventID=1035 or EventID=1036) and TimeCreated[@SystemTime>='{since.ToUniversalTime():o}']]]"
            : $"*[System[TimeCreated[@SystemTime>='{since.ToUniversalTime():o}']]]";
        var query = new EventLogQuery(channel, PathType.LogName, predicate);
        try
        {
            using var reader = new EventLogReader(query);
            var list = new List<TraceEvent>();
            for (int i = 0; i < 400; i++)   // 上限：痕跡是摘要不是傾印
            {
                using var evt = reader.ReadEvent();
                if (evt is null) break;
                string text = channel == MsiLogName
                    ? (evt.Properties.Count > 0 ? evt.Properties[0].Value?.ToString() ?? "(未命名)" : "(未命名)")
                    : "(BITS 工作)";
                text = ProductLabel(text);
                list.Add(new TraceEvent(new DateTimeOffset(evt.TimeCreated ?? DateTime.Now), evt.Id, text));
            }
            return list;
        }
        catch (EventLogNotFoundException) { return null; }   // 通道不存在：交呼叫端標 NotSupported
    }

    // ── UF-006／UF-009 更新面 ────────────────────────────────────────────

    private static HardwareFact UpdateSourceFact(DateTimeOffset at, RegistryProbe registry)
    {
        object? useWsus = registry("UseWUServer");
        object? blocked = registry("DisableWindowsUpdateAccess");
        int? wsus = useWsus is int w ? w : null;
        int? block = blocked is int b ? b : null;
        (string value, FactAvailability state) kind = block == 1
            ? ("更新被群組原則停用（DisableWindowsUpdateAccess=1）——來源是「沒有對外來源」，不是「沒有更新可用」",
               FactAvailability.NotApplicable)
            : wsus == 1
                ? ("WSUS（內部更新伺服器）——由群組原則指定，更新來源歸組織管理", FactAvailability.Present)
                : wsus is null
                    ? ("Windows Update（公用服務）——登錄未設定 UseWUServer，即預設對外來源", FactAvailability.Present)
                    : ("Windows Update（公用服務）（UseWUServer=" + wsus + "）", FactAvailability.Present);

        return new HardwareFact(SourceKey, CatUf, "更新來源",
            kind.value + "。界線：這一欄講「更新從哪裡來」，不講「有沒有待裝更新」。", "",
            @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate（群組原則）",
            FactTrustLevel.Reported, false, at, null, kind.state);
    }

    private static HardwareFact ServicingFact(DateTimeOffset at, RegistryProbe registry)
    {
        if (registry("ServicingStackVersion") is not string s || string.IsNullOrWhiteSpace(s))
            return Na(at, ServicingKey, CatUf, "服務堆疊版本",
                @"HKLM\...\Component Based Servicing\Version 讀不到——不推測 SSU 版本", FactAvailability.NotSupported);

        return new HardwareFact(ServicingKey, CatUf, "服務堆疊版本",
            $"CBS 服務堆疊版本 {s}（登錄值名稱即版本）。界線：只陳述版本，不判「是不是最新」。", "",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Version",
            FactTrustLevel.Measured, false, at);
    }

    private static object? ReadRegistry(string valueName) => valueName switch
    {
        "UseWUServer" => Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "UseWUServer", null),
        "DisableWindowsUpdateAccess" => Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "DisableWindowsUpdateAccess", null),
        "ServicingStackVersion" => ReadCbsVersion(),
        _ => null,
    };

    /// <summary>CBS\Version 的子值「名稱」就是服務堆疊版本（值資料是 component 目錄路徑，刻意不採用）。</summary>
    private static string? ReadCbsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Version");
            return key?.GetValueNames().Cast<string>().FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>產品識別遮罩：事件屬性裡的產品 GUID 不進值（只留可讀名稱段）。</summary>
    internal static string ProductLabel(string text) =>
        string.IsNullOrWhiteSpace(text) || text.StartsWith("{", StringComparison.Ordinal)
            ? "(產品 GUID，名稱未提供)" : text;

    private static HardwareFact Na(DateTimeOffset at, string key, string category, string name,
        string reason, FactAvailability availability)
        => new(key, category, name, "", "", "維護與裝置健康（唯讀）", FactTrustLevel.Unknown, false, at,
            null, availability, reason);
}
