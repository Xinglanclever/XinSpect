namespace XinSpect;

/// <summary>
/// 匯出收尾與使用體驗（Vol2 第 420–429 項）：EV-012 匯出驗證 CLI（<c>ev.verify</c>）、
/// EV-013 審計鏈時間軸（<c>ev.audit.timeline</c>）、EV-014 schema 遷移器（<c>ev.migrate</c>）、
/// EV-015 命名查詢視圖（<c>ev.namedquery</c>，寫入經閘門）、EV-016 報告浮水印（<c>ev.watermark</c>）、
/// EV-017 多語系報告（<c>ev.i18n</c>）、EV-018 .xinsig 密封格式（<c>ev.xinsig</c>）、EV-020 統一匯入框架（<c>ev.import</c>）、
/// UX-002 資料年齡徽章（<c>ux.age</c>）、UX-009 崩潰報告頁（<c>ux.crashreport</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>紅線：</b>① 匯出驗證是<b>唯讀檢查</b>不修改 bundle；② 審計鏈與 EvidenceTimeline 並存不重疊；
/// ③ schema 遷移<b>不可逆要聲明</b>；④ 命名查詢／浮水印寫入經 WriteGate；⑤ 匯入值<b>Unknown 起步、不覆蓋本機值</b>；
/// ⑥ 崩潰報告<b>如實不粉飾</b>；⑦ 資料年齡如實（不美化為「即時」）。
/// </para>
/// </remarks>
public static class ExportUxFactsService
{
    private const string CatEv = "證據匯出";
    private const string CatUx = "使用體驗";

    public const string VerifyKey = "ev.verify";
    public const string AuditTimelineKey = "ev.audit.timeline";
    public const string MigrateKey = "ev.migrate";
    public const string NamedQueryKey = "ev.namedquery";
    public const string WatermarkKey = "ev.watermark";
    public const string I18nReportKey = "ev.i18n";
    public const string XinSigKey = "ev.xinsig";
    public const string ImportKey = "ev.import";
    public const string AgeKey = "ux.age";
    public const string CrashReportKey = "ux.crashreport";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    public delegate (bool SchemaValid, string? Error) VerifyProbe();
    public delegate (int Entries, bool HashChained)? AuditTimelineProbe();
    public delegate (int FromVersion, int ToVersion, bool Reversible)? MigrateProbe();
    public delegate (int Profiles, bool ViaWriteGate)? NamedQueryProbe();
    public delegate (bool HasWatermark, string Text)? WatermarkProbe();
    public delegate (int Languages, double CoveragePercent)? I18nProbe();
    public delegate (bool Signed, bool Verifiable)? XinSigProbe();
    public delegate (int Sources, int ImportedFacts)? ImportProbe();
    public delegate (double AgeMinutes, bool IsLive)? AgeProbe();
    public delegate (int Crashes, bool Honest)? CrashReportProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        VerifyProbe? verify = null, AuditTimelineProbe? auditTimeline = null, MigrateProbe? migrate = null,
        NamedQueryProbe? namedQuery = null, WatermarkProbe? watermark = null, I18nProbe? i18n = null,
        XinSigProbe? xinSig = null, ImportProbe? import = null, AgeProbe? age = null, CrashReportProbe? crash = null)
        =>
        [
            VerifyFact(at, verify), AuditTimelineFact(at, auditTimeline), MigrateFact(at, migrate),
            NamedQueryFact(at, namedQuery), WatermarkFact(at, watermark), I18nReportFact(at, i18n),
            XinSigFact(at, xinSig), ImportFact(at, import), AgeFact(at, age), CrashReportFact(at, crash),
        ];

    // ── EV-012 匯出驗證 CLI ────────────────────────────────────────────

    public static HardwareFact VerifyFact(DateTimeOffset at, VerifyProbe? probe = null)
    {
        (bool SchemaValid, string? Error)? s;
        try { s = (probe ?? (() => (false, null)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, VerifyKey, CatEv, "匯出驗證 CLI", FactAvailability.ReadError, "驗證狀態不可讀");

        var (valid, err) = s.Value;
        return new HardwareFact(VerifyKey, CatEv, "匯出驗證 CLI",
            valid ? "schema 檢查通過（唯讀檢查：匯出 bundle 結構與規格一致；通過代表格式正確，不代表內容正確）。"
                  : $"schema 檢查未通過：{err ?? "未知"}。" +
                    "<b>唯讀檢查</b>：驗證不修改 bundle，只報結構是否合規；" +
                    "通過代表格式正確，不代表內容正確。",
            "", "schema 檢查（唯讀）", valid ? FactTrustLevel.Measured : FactTrustLevel.Unknown,
            false, at, valid ? 1 : 0,
            valid ? FactAvailability.Present : FactAvailability.Unknown,
            valid ? null : "schema 檢查未通過");
    }

    // ── EV-013 審計鏈時間軸 ────────────────────────────────────────────

    public static HardwareFact AuditTimelineFact(DateTimeOffset at, AuditTimelineProbe? probe = null)
    {
        (int Entries, bool HashChained)? s;
        try { s = (probe ?? (() => (0, false)))(); }
        catch { s = null; }

        if (s is null || s.Value.Entries == 0)
            return Unavailable(at, AuditTimelineKey, CatEv, "審計鏈時間軸", FactAvailability.NotApplicable,
                "沒有審計條目——不畫空時間軸");

        var (entries, chained) = s.Value;
        return new HardwareFact(AuditTimelineKey, CatEv, "審計鏈時間軸",
            $"審計條目 {entries} 筆、雜湊鏈={(chained ? "完整" : "斷裂或未啟用")}。" +
            "<b>與 EvidenceTimeline 並存不重疊</b>：Timeline 是事件呈現，" +
            "審計鏈是不可否認中繼資料——兩者刻意分開，不互相取代。",
            "筆", "AuditLogService（唯讀）", FactTrustLevel.Reported, false, at, entries);
    }

    // ── EV-014 schema 遷移器 ───────────────────────────────────────────

    public static HardwareFact MigrateFact(DateTimeOffset at, MigrateProbe? probe = null)
    {
        (int FromVersion, int ToVersion, bool Reversible)? s;
        try { s = (probe ?? (() => (0, 0, false)))(); }
        catch { s = null; }

        if (s is null || s.Value.ToVersion == 0)
            return Unavailable(at, MigrateKey, CatEv, "schema 遷移器", FactAvailability.NotApplicable,
                "沒有可遷移的 schema——不需遷移不代表已是最新");

        var (from, to, reversible) = s.Value;
        return new HardwareFact(MigrateKey, CatEv, "schema 遷移器",
            $"v{from}→v{to}、可逆={(reversible ? "是" : "否")}。" +
            "<b>升級不可逆要聲明</b>：schema 升級若丟棄舊欄位，降版會讀不回來——" +
            "本項明示可逆性，不假裝升級無痛。",
            "", "schema 遷移（唯讀宣告）", FactTrustLevel.Reported, false, at, to);
    }

    // ── EV-015 命名查詢視圖 ────────────────────────────────────────────

    public static HardwareFact NamedQueryFact(DateTimeOffset at, NamedQueryProbe? probe = null)
    {
        (int Profiles, bool ViaWriteGate)? s;
        try { s = (probe ?? (() => (0, false)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, NamedQueryKey, CatEv, "命名查詢視圖", FactAvailability.ReadError, "查詢視圖不可讀");

        var (profiles, gate) = s.Value;
        return new HardwareFact(NamedQueryKey, CatEv, "命名查詢視圖",
            $"已存查詢視圖 {profiles} 個、寫入經閘門={(gate ? "是" : "否")}。" +
            "查詢視圖是<b>使用者資產</b>——持久化是寫入，經 WriteGate 留痕；" +
            "視圖不改變查詢語意，只記住條件。",
            "個", "查詢視圖（經寫入閘門）", FactTrustLevel.Reported, false, at, profiles);
    }

    // ── EV-016 報告浮水印 ──────────────────────────────────────────────

    public static HardwareFact WatermarkFact(DateTimeOffset at, WatermarkProbe? probe = null)
    {
        (bool HasWatermark, string Text)? s;
        try { s = (probe ?? (() => (false, "")))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, WatermarkKey, CatEv, "報告浮水印", FactAvailability.ReadError, "浮水印狀態不可讀");

        var (has, text) = s.Value;
        return new HardwareFact(WatermarkKey, CatEv, "報告浮水印",
            has ? $"已設浮水印「{text}」（送審正式化）。" : "未設浮水印。",
            "", "報告渲染（唯讀）", FactTrustLevel.Reported, false, at, has ? 1 : 0);
    }

    // ── EV-017 多語系報告 ──────────────────────────────────────────────

    public static HardwareFact I18nReportFact(DateTimeOffset at, I18nProbe? probe = null)
    {
        (int Languages, double CoveragePercent)? s;
        try { s = (probe ?? (() => (1, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, I18nReportKey, CatEv, "多語系報告", FactAvailability.ReadError, "報告語言狀態不可讀");

        var (langs, cov) = s.Value;
        return new HardwareFact(I18nReportKey, CatEv, "多語系報告",
            $"語言 {langs} 種、翻譯覆蓋率 {cov:0.#}%。" +
            "<b>翻譯覆蓋率守門</b>：未收錄的字串會回退原文（不是顯示空白）——" +
            "覆蓋率低不代表報告不可用，但要如實標示，不假裝全翻譯。",
            "種", "報告語言（唯讀）", FactTrustLevel.Reported, false, at, cov);
    }

    // ── EV-018 .xinsig 密封格式 ────────────────────────────────────────

    public static HardwareFact XinSigFact(DateTimeOffset at, XinSigProbe? probe = null)
    {
        (bool Signed, bool Verifiable)? s;
        try { s = (probe ?? (() => (false, false)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, XinSigKey, CatEv, ".xinsig 密封格式", FactAvailability.ReadError, "密封狀態不可讀");

        var (signed, verifiable) = s.Value;
        return new HardwareFact(XinSigKey, CatEv, ".xinsig 密封格式",
            $"密封 zip＋簽章={(signed ? "有" : "無")}、內建驗證={(verifiable ? "有" : "無")}。" +
            "<b>格式 spec 公開</b>：接收方不需本工具即可驗證；簽章證明內容完整與來源，" +
            "不證明內容正確。",
            "", "密封格式（唯讀）", FactTrustLevel.Reported, false, at, signed ? 1 : 0);
    }

    // ── EV-020 統一匯入框架 ────────────────────────────────────────────

    public static HardwareFact ImportFact(DateTimeOffset at, ImportProbe? probe = null)
    {
        (int Sources, int ImportedFacts)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null || s.Value.Sources == 0)
            return Unavailable(at, ImportKey, CatEv, "統一匯入框架", FactAvailability.NotApplicable,
                "沒有可匯入的來源（HWiNFO／AIDA64 CSV）——不從缺來源推論");

        var (sources, facts) = s.Value;
        return new HardwareFact(ImportKey, CatEv, "統一匯入框架",
            $"來源 {sources} 個、匯入事實 {facts} 筆。" +
            "<b>匯入值 Unknown 起步、不覆蓋本機值</b>：外部工具的數字來源可疑，" +
            "一律標未確認；與本機實測衝突時<b>保留兩者</b>讓人判讀，不靜默覆蓋。",
            "筆", "外部 CSV 匯入（唯讀）", FactTrustLevel.Reported, false, at, facts);
    }

    // ── UX-002 資料年齡徽章 ────────────────────────────────────────────

    public static HardwareFact AgeFact(DateTimeOffset at, AgeProbe? probe = null)
    {
        (double AgeMinutes, bool IsLive)? s;
        try { s = (probe ?? (() => (0, false)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, AgeKey, CatUx, "資料年齡", FactAvailability.ReadError, "資料年齡不可讀");

        var (age, live) = s.Value;
        return new HardwareFact(AgeKey, CatUx, "資料年齡",
            $"資料年齡 {age:0.#} 分鐘、即時={(live ? "是" : "否（來自快照或快取）")}。" +
            "<b>如實不美化</b>：快照或快取的資料不是即時值——徽章明確標示年齡，" +
            "不讓舊值冒充現況。0 分鐘是值（剛取得）。",
            "分鐘", "事實時戳（唯讀）", FactTrustLevel.Measured, false, at, age);
    }

    // ── UX-009 崩潰報告頁 ──────────────────────────────────────────────

    public static HardwareFact CrashReportFact(DateTimeOffset at, CrashReportProbe? probe = null)
    {
        (int Crashes, bool Honest)? s;
        try { s = (probe ?? (() => (0, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, CrashReportKey, CatUx, "崩潰報告頁", FactAvailability.ReadError, "崩潰報告不可讀");

        var (crashes, honest) = s.Value;
        return new HardwareFact(CrashReportKey, CatUx, "崩潰報告頁",
            crashes == 0
                ? "沒有偵測到本工具的異常結束（0 是值）。"
                : $"偵測到 {crashes} 次異常結束，下次啟動呈現。<b>如實不粉飾</b>：" +
                  (honest ? "報告含發生時間與版本，不美化為「已修復」。" : "報告可能未如實——需人工核對。"),
            "次", "啟動異常偵測（唯讀）", FactTrustLevel.Reported, false, at, crashes,
            FactAvailability.Present, null);
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
