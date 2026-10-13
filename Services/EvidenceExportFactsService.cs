using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace XinSpect;

/// <summary>
/// 證據與匯出（Vol2 第 410–419 項）：EV-001 PDF 報告（<c>ev.pdf</c>）、EV-002 證據包匯出（<c>ev.pack</c>）、
/// EV-003 報告差分頁（<c>ev.diff</c>）、EV-005 查詢聚合擴充（<c>ev.query.agg</c>）、EV-006 CLI watch（<c>ev.watch</c>）、
/// EV-007 快照排程器（<c>ev.schedule</c>，寫入經閘門）、EV-008 機隊並排（<c>ev.fleet</c>）、
/// EV-009 規則包版本對帳（<c>ev.rulever</c>）、EV-010 CSV/MD 匯出（<c>ev.csv</c>）、EV-011 自訂儀表板（<c>ev.dash</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>紅線：</b>① 匯出<b>只產生內容不落地、不上傳</b>（上傳通路依資料主權不實作）；
/// ② 證據包的 manifest 必須<b>可獨立驗證</b>（雜湊涵蓋內容）；③ 排程建立經 <c>WriteGate</c>；
/// ④ CSV/MD 敏感值預設遮蔽；⑤ 差分／並排是<b>呈現</b>不是判決；⑥ 讀不到一律三態。
/// </para>
/// </remarks>
public static class EvidenceExportFactsService
{
    private const string Cat = "證據匯出";

    public const string PdfKey = "ev.pdf";
    public const string PackKey = "ev.pack";
    public const string DiffKey = "ev.diff";
    public const string QueryAggKey = "ev.query.agg";
    public const string WatchKey = "ev.watch";
    public const string ScheduleKey = "ev.schedule";
    public const string FleetKey = "ev.fleet";
    public const string RuleVerKey = "ev.rulever";
    public const string CsvKey = "ev.csv";
    public const string DashKey = "ev.dash";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    public delegate (bool FontEmbedded, int Pages)? PdfProbe();
    public delegate (int Files, string ManifestHash)? PackProbe();
    public delegate (int Added, int Removed, int Changed)? DiffProbe();
    public delegate (int Clauses, string[] SupportedOps)? QueryAggProbe();
    public delegate (int IntervalSeconds, int Iterations)? WatchProbe();
    public delegate (bool ViaWriteGate, string Schedule)? ScheduleProbe();
    public delegate (int Machines, int Metrics)? FleetProbe();
    public delegate (string BuiltinVersion, int Rules)? RuleVerProbe();
    public delegate (int Rows, int Redacted)? CsvProbe();
    public delegate (int Keys, bool AllResolvable)? DashProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        PdfProbe? pdf = null, PackProbe? pack = null, DiffProbe? diff = null, QueryAggProbe? queryAgg = null,
        WatchProbe? watch = null, ScheduleProbe? schedule = null, FleetProbe? fleet = null,
        RuleVerProbe? ruleVer = null, CsvProbe? csv = null, DashProbe? dash = null)
        =>
        [
            PdfFact(at, pdf), PackFact(at, pack), DiffFact(at, diff), QueryAggFact(at, queryAgg),
            WatchFact(at, watch), ScheduleFact(at, schedule), FleetFact(at, fleet),
            RuleVerFact(at, ruleVer), CsvFact(at, csv), DashFact(at, dash),
        ];

    // ── EV-001 PDF 報告 ────────────────────────────────────────────────

    public static HardwareFact PdfFact(DateTimeOffset at, PdfProbe? probe = null)
    {
        (bool FontEmbedded, int Pages)? s;
        try { s = (probe ?? (() => (false, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, PdfKey, Cat, "PDF 報告", FactAvailability.ReadError, "PDF 產生狀態不可讀");

        var (font, pages) = s.Value;
        return new HardwareFact(PdfKey, Cat, "PDF 報告",
            $"頁數 {pages}、中文字型嵌入={(font ? "是" : "否")}。" +
            "<b>中文字型嵌入是工程量</b>：不嵌入會在他機顯示成方框——這是原生 PDF 的主要成本。" +
            "本項<b>只產生內容不落檔</b>（HTML 報告已是既有出口；PDF 是新增呈現層）。不列路徑。",
            "頁", "原生 PDF 產生（唯讀）", FactTrustLevel.Reported, false, at, pages);
    }

    // ── EV-002 證據包匯出 ──────────────────────────────────────────────

    public static HardwareFact PackFact(DateTimeOffset at, PackProbe? probe = null)
    {
        (int Files, string ManifestHash)? s;
        try { s = (probe ?? (() => (0, "")))(); }
        catch { s = null; }

        if (s is null || s.Value.Files == 0)
            return Unavailable(at, PackKey, Cat, "證據包匯出", FactAvailability.NotApplicable,
                "沒有可打包的內容——不建構空包");

        var (files, hash) = s.Value;
        return new HardwareFact(PackKey, Cat, "證據包匯出",
            $"含 {files} 個檔案（JSON＋HTML＋審計鏈＋manifest）、manifest 雜湊 {Prefix(hash)}。" +
            "<b>manifest 可獨立驗證</b>：雜湊涵蓋內容，接收方不需本工具即可驗證完整性；" +
            "本包<b>只產生不落地、不上傳</b>。",
            "檔", "證據包建構（唯讀）", FactTrustLevel.Reported, false, at, files);
    }

    // ── EV-003 報告差分頁 ──────────────────────────────────────────────

    public static HardwareFact DiffFact(DateTimeOffset at, DiffProbe? probe = null)
    {
        (int Added, int Removed, int Changed)? s;
        try { s = (probe ?? (() => (0, 0, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, DiffKey, Cat, "報告差分頁", FactAvailability.ReadError, "差分計算失敗");

        var (added, removed, changed) = s.Value;
        int total = added + removed + changed;
        return new HardwareFact(DiffKey, Cat, "報告差分頁",
            total == 0 ? "兩份快照無差異（0 是值）。"
                : $"新增 {added}、移除 {removed}、變更 {changed} 項。",
            "項", "時間膠囊差異（呈現層）", FactTrustLevel.Derived, false, at, total);
    }

    // ── EV-005 查詢聚合擴充 ────────────────────────────────────────────

    public static HardwareFact QueryAggFact(DateTimeOffset at, QueryAggProbe? probe = null)
    {
        (int Clauses, string[] SupportedOps)? s;
        try { s = (probe ?? (() => (0, [])))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, QueryAggKey, Cat, "查詢聚合擴充", FactAvailability.ReadError, "查詢聚合不可讀");

        var (clauses, ops) = s.Value;
        return new HardwareFact(QueryAggKey, Cat, "查詢聚合擴充",
            $"聚合子句 {clauses} 個、算子 {string.Join("、", ops)}。" +
            "<b>不破壞既有解析</b>：聚合是既有查詢語言的加法擴充，" +
            "舊語法不變；聚合結果是<b>導出數</b>不是新事實（count/avg 不取代原始值）。",
            "子句", "查詢引擎（唯讀）", FactTrustLevel.Reported, false, at, clauses);
    }

    // ── EV-006 CLI watch ───────────────────────────────────────────────

    public static HardwareFact WatchFact(DateTimeOffset at, WatchProbe? probe = null)
    {
        (int IntervalSeconds, int Iterations)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null || s.Value.IntervalSeconds <= 0)
            return new HardwareFact(WatchKey, Cat, "CLI watch", "", "", "CLI（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "未啟用週期收集——watch 需使用者明確指定間隔，預設不跑");

        var (interval, iters) = s.Value;
        return new HardwareFact(WatchKey, Cat, "CLI watch",
            $"間隔 {interval} 秒、{iters} 次迭代。" +
            "<b>資源上限聲明</b>：週期收集會持續佔用 CPU 與記憶體；interval 過短會讓收集本身" +
            "成為負載來源。watch 是<b>使用者啟動</b>的，不是常駐。",
            "秒", "CLI watch（使用者啟動）", FactTrustLevel.Reported, false, at, interval);
    }

    // ── EV-007 快照排程器 ──────────────────────────────────────────────

    public static HardwareFact ScheduleFact(DateTimeOffset at, ScheduleProbe? probe = null)
    {
        (bool ViaWriteGate, string Schedule)? s;
        try { s = (probe ?? (() => (false, "—")))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, ScheduleKey, Cat, "快照排程器", FactAvailability.ReadError, "排程狀態不可讀");

        var (viaGate, schedule) = s.Value;
        return new HardwareFact(ScheduleKey, Cat, "快照排程器",
            $"排程「{schedule}」、經寫入閘門={(viaGate ? "是" : "否")}。" +
            "<b>排程建立經 WriteGate</b>：建立工作排程是系統寫入，必須留痕（誰、何時、寫了什麼）；" +
            "未經閘門的寫入一律拒絕。本項只陳述狀態，不建立排程。",
            "", "工作排程（經寫入閘門）", FactTrustLevel.Reported, false, at, viaGate ? 1 : 0);
    }

    // ── EV-008 機隊並排 ────────────────────────────────────────────────

    public static HardwareFact FleetFact(DateTimeOffset at, FleetProbe? probe = null)
    {
        (int Machines, int Metrics)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null || s.Value.Machines < 2)
            return new HardwareFact(FleetKey, Cat, "機隊並排", "", "", "RunOnSnapshots（呈現層）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "少於兩台機器——機隊並排需要至少兩台，單機不假裝有機隊");

        var (machines, metrics) = s.Value;
        return new HardwareFact(FleetKey, Cat, "機隊並排",
            $"{machines} 台 × {metrics} 項指標。" +
            "後端 <c>RunOnSnapshots</c> 已存在，本項是視覺化；機器識別只用匿名雜湊，" +
            "<b>並排是呈現不是排名</b>。",
            "台", "RunOnSnapshots（呈現層）", FactTrustLevel.Derived, false, at, machines);
    }

    // ── EV-009 規則包版本對帳 ──────────────────────────────────────────

    public static HardwareFact RuleVerFact(DateTimeOffset at, RuleVerProbe? probe = null)
    {
        (string BuiltinVersion, int Rules)? s;
        try { s = (probe ?? (() => ("—", 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, RuleVerKey, Cat, "規則包版本對帳", FactAvailability.ReadError, "規則包狀態不可讀");

        var (version, rules) = s.Value;
        return new HardwareFact(RuleVerKey, Cat, "規則包版本對帳",
            $"builtin.json 版本 {version}、規則 {rules} 條。" +
            "<b>變更守門</b>：規則包版本是公開契約——規則增刪要有版本號與變更說明，" +
            "否則下游會拿到行為已變但版本沒變的規則。",
            "條", "規則包（唯讀）", FactTrustLevel.Reported, false, at, rules);
    }

    // ── EV-010 CSV/MD 匯出 ─────────────────────────────────────────────

    public static HardwareFact CsvFact(DateTimeOffset at, CsvProbe? probe = null)
    {
        (int Rows, int Redacted)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, CsvKey, Cat, "CSV/MD 匯出", FactAvailability.ReadError, "匯出狀態不可讀");

        var (rows, redacted) = s.Value;
        return new HardwareFact(CsvKey, Cat, "CSV/MD 匯出",
            $"可匯出 {rows} 列、其中 {redacted} 個敏感值<b>已遮蔽</b>。" +
            "格式往返：CSV 有引號與換行轉義，MD 表格有管線字元轉義——兩者都要往返測試。" +
            "<b>只產生內容不落檔</b>。",
            "列", "表格匯出（唯讀）", FactTrustLevel.Reported, false, at, rows);
    }

    // ── EV-011 自訂儀表板 ──────────────────────────────────────────────

    public static HardwareFact DashFact(DateTimeOffset at, DashProbe? probe = null)
    {
        (int Keys, bool AllResolvable)? s;
        try { s = (probe ?? (() => (0, false)))(); }
        catch { s = null; }

        if (s is null || s.Value.Keys == 0)
            return Unavailable(at, DashKey, Cat, "自訂儀表板", FactAvailability.NotApplicable,
                "未選擇任何鍵——空儀表板不建構");

        var (keys, resolvable) = s.Value;
        return new HardwareFact(DashKey, Cat, "自訂儀表板",
            $"選定 {keys} 個鍵、全部可解析={(resolvable ? "是" : "否")}。" +
            "<b>鍵引用有效性</b>：儀表板引用的事實鍵必須存在（不存在的鍵會顯示空白而非 0）——" +
            "配置校驗在建立時就擋，不留到執行期。",
            "鍵", "儀表板配置（唯讀）", FactTrustLevel.Reported, false, at, keys);
    }

    // ──────────────────────────── helper ────────────────────────────

    private static string Prefix(string hash) => string.IsNullOrEmpty(hash) ? "—" : hash[..Math.Min(12, hash.Length)] + "…";

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
