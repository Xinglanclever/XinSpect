using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 韌體風險陳述（ULTIMATE-1000 階段二 P2-099–P2-104；鍵 <c>fw.risk.facts.*</c>）。
///
/// <para>
/// <b>這六把為什麼拖到最後：</b>它們被歸在「不缺能力、缺規則」那一群——能力是「讀得到哪些韌體事實」，
/// 缺的是「分數由哪些事實組成、門檻怎麼定」。**沒有出處的權重表就是一套沒有出處的常數**
/// （本專案在 <c>UNSOURCED-CONSTANTS.md</c> 記過四次同型的病），所以這一版<b>不發明權重</b>，
/// 改用兩個已經在版控裡的來源：
/// </para>
/// <list type="number">
/// <item><b>構成</b>＝<see cref="FactRelationRules.All"/>：每一條規則自己就宣告了它用哪些鍵、
/// 以及為什麼可以這樣比（<see cref="FactRelationRule.Explanation"/>）。「分數由哪些事實組成」這件事
/// 早就寫在規則集裡了——本服務只把它攤開，不另立第二份清單（兩份清單遲早不一樣）。</item>
/// <item><b>等級</b>＝由該輪的對帳結果推導，界線<b>只有「有沒有」</b>：矛盾 ≥1 就是「有矛盾項」、
/// 缺口 ≥1 就不是「全部一致」、全部都無法驗證就是「無法評估」。<b>沒有加權、沒有分數、沒有可調門檻</b>
/// ——所以也沒有「門檻該定多少」這個需要產品決定的問題。</item>
/// </list>
///
/// <para>
/// <b>等級描述的是「這批證據能陳述到什麼程度」，不是機器的安全狀態。</b>矛盾與缺口各自陳述
/// （見 <see cref="GapKey"/>）：缺口不當 0、不當「沒問題」，也不計入等級推導——
/// 缺的是「不知道」，不是「沒有」。本分級的定義屬本專案政策 v<see cref="PolicyVersion"/>，
/// 不是外部安全標準。
/// </para>
///
/// <para>
/// <b>這一組不新增任何讀取路徑。</b>輸入是同一輪已經收集好的事實與同一份對帳結果；
/// 本服務只做純計算與序列化（有測試掃原始碼釘住這件事）。
/// </para>
/// </summary>
public static class FirmwareRiskFactsService
{
    public const string PartsKey = "fw.risk.facts.parts";
    public const string LevelKey = "fw.risk.facts.level";
    public const string GapKey = "fw.risk.facts.gap";
    public const string BaselineKey = "fw.risk.facts.baseline";
    public const string DeltaKey = "fw.risk.facts.delta";
    public const string ReportKey = "fw.risk.facts.report";

    /// <summary>
    /// 分級政策版本。定義（等級的界線、構成的來源）一改就要動它——
    /// 基線的內容雜湊含這一版號，所以「同一份基線」與「同一個政策」是可以分開追的。
    /// </summary>
    public const string PolicyVersion = "1.0";

    /// <summary>報告要引用的審計鏈互驗鍵（P1-072）。</summary>
    public const string ChainVerifyKey = "ev.chain.verify";

    private const string Category = "韌體安全";
    private const string PolicyNote = "分級定義屬本專案政策 v" + PolicyVersion + "（不是外部安全標準）";

    /// <summary>
    /// 產生構成／等級／缺口／基線／報告五把（差異那一把由「時間膠囊比較」產生，見 <see cref="Delta"/>）。
    /// <paramref name="rows"/> 是同一輪的對帳結果（呼叫端算一次就共用，不重評）；
    /// <paramref name="facts"/> 是同一輪的輸入事實（僅用於基線內容與審計鏈狀態，不新增讀取）。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(
        DateTimeOffset at,
        IReadOnlyList<FactRelationRow> rows,
        IReadOnlyList<HardwareFact> facts,
        HardwareFact? chainVerify = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(facts);

        return
        [
            PartsFact(at, rows),
            LevelFact(at, rows),
            GapFact(at, rows),
            BaselineFact(at, rows, facts),
            ReportFact(at, facts, chainVerify),
        ];
    }

    /// <summary>參與風險陳述的鍵＝規則集的輸入鍵聯集＋本組自己的六把鍵（差異比對用這一份）。</summary>
    public static IReadOnlyList<string> WatchKeys()
        => FactRelationRules.All.SelectMany(r => r.InputKeys)
            .Concat([PartsKey, LevelKey, GapKey, BaselineKey, DeltaKey, ReportKey])
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // ── P2-099 構成 ───────────────────────────────────────────────────────

    private static HardwareFact PartsFact(DateTimeOffset at, IReadOnlyList<FactRelationRow> rows)
    {
        int consistent = rows.Count(r => r.Relation == FactRelation.Consistent);
        int contradicts = rows.Count(r => r.Relation == FactRelation.Contradicts);
        int unverifiable = rows.Count - consistent - contradicts;
        int keys = WatchKeys().Count - 6;   // 只算規則集的輸入鍵（本組自己的六把不算「輸入」）

        string detail = string.Join("、", rows.Select(r => $"{r.RuleId}＝{RelationText(r.Relation)}"));
        string value =
            $"構成 {rows.Count} 條對帳規則（來源：{nameof(FactRelationRules)}.All，每條規則自帶出處理由與參與鍵）："
            + $"一致 {consistent}、矛盾 {contradicts}、無法驗證 {unverifiable}；參與鍵 {keys} 把（清單與逐鍵狀態在基線內容裡）。"
            + $"逐條：{detail}。"
            + "構成由規則集決定，不由本服務另立一份清單——兩份清單遲早會不一樣。"
            + $"逐條的比對理由與缺鍵見 reconcile.<規則 id>；{PolicyNote}。";

        return new HardwareFact(PartsKey, Category, "韌體風險陳述構成", value, "", "對帳規則集（FactRelationRules.All）",
            FactTrustLevel.Derived, false, at);
    }

    // ── P2-100 等級（分級但不下判決）──────────────────────────────────────

    private static HardwareFact LevelFact(DateTimeOffset at, IReadOnlyList<FactRelationRow> rows)
    {
        int consistent = rows.Count(r => r.Relation == FactRelation.Consistent);
        int contradicts = rows.Count(r => r.Relation == FactRelation.Contradicts);
        int unverifiable = rows.Count - consistent - contradicts;

        string level = LevelToken(rows.Count, contradicts, unverifiable);
        string value =
            $"等級：{level}（一致 {consistent}、矛盾 {contradicts}、無法驗證 {unverifiable}；共 {rows.Count} 條規則）。"
            + "界線：等級只用「有沒有」判定——有矛盾 ≥1 就是「有矛盾項」、缺口 ≥1 就不是「全部一致」、"
            + "全部都無法驗證就是「無法評估」；沒有加權、沒有分數、沒有可調的門檻"
            + "（分數需要權重，而權重是產品決定——未定之前不輸出，免得變成一套沒有出處的常數）。"
            + "它描述的是「這批證據能陳述到什麼程度」，不是機器的安全狀態，也不下「安全／不安全」的判決。"
            + $"矛盾與缺口的內容另見 {GapKey} 與 {PartsKey}；{PolicyNote}。";

        return new HardwareFact(LevelKey, Category, "韌體風險等級陳述", value, "", PolicyNote,
            FactTrustLevel.Derived, false, at);
    }

    /// <summary>等級字串。四個值都是「有沒有」的直述，沒有一個是程度副詞。</summary>
    public static string LevelToken(int ruleCount, int contradicts, int unverifiable)
    {
        if (ruleCount == 0 || unverifiable == ruleCount) return "無法評估";
        if (contradicts > 0) return "有矛盾項";
        if (unverifiable > 0) return "部分未定";
        return "全部一致";
    }

    // ── P2-101 缺口 ───────────────────────────────────────────────────────

    private static HardwareFact GapFact(DateTimeOffset at, IReadOnlyList<FactRelationRow> rows)
    {
        var gaps = rows.Where(r => r.Relation == FactRelation.Unverifiable).ToList();
        string value = gaps.Count == 0
            ? $"本輪沒有未取得項：{rows.Count} 條規則全部可判定。"
              + "界線：缺項若出現會被列在這裡，而且不會被當成 0 或「沒問題」，也不計入等級的「全部一致」——"
              + "缺的是「不知道」，不是「沒有」。"
            : $"未取得 {gaps.Count} 條（共 {rows.Count} 條）："
              + string.Join("；", gaps.Select(g => $"{g.RuleId}（{g.Reason}）"))
              + "。這些不計入等級推導，也不被當成 0 或「沒問題」——缺的是「不知道」，不是「沒有」。"
              + "缺鍵與原因取自對帳引擎（同一輪的輸入事實三態）。";

        return new HardwareFact(GapKey, Category, "韌體風險缺口申報", value, "", "對帳引擎的三態輸入",
            FactTrustLevel.Derived, false, at, gaps.Count);
    }

    // ── P2-102 基線（可離線比對）──────────────────────────────────────────

    /// <summary>
    /// 基線內容：canonical JSON（規則依 id 排序、參與鍵依鍵名排序）。
    /// <b>不含時間、不含機器識別、不含路徑</b>——所以同一台機器同一輪重跑會得到同一份內容與同一個雜湊，
    /// 而「有東西變了」這件事才對得上。
    /// </summary>
    public static string BaselineJson(IReadOnlyList<FactRelationRow> rows, IReadOnlyList<HardwareFact> facts)
    {
        var ruleById = FactRelationRules.All.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var byKey = facts.GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var rules = rows.OrderBy(r => r.RuleId, StringComparer.Ordinal)
            .Select(r => new
            {
                id = r.RuleId,
                relation = r.Relation.ToString(),
                keys = ruleById.TryGetValue(r.RuleId, out var rule) ? rule.InputKeys.ToArray() : [],
            })
            .ToArray();

        var keys = WatchKeys().OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => byKey.TryGetValue(k, out var f)
                ? new { key = k, availability = f.Availability.ToString(), value = f.Value }
                : new { key = k, availability = "Missing", value = "" })
            .ToArray();

        var payload = new
        {
            policy = PolicyVersion,
            level = LevelToken(rows.Count, rows.Count(r => r.Relation == FactRelation.Contradicts),
                rows.Count(r => r.Relation == FactRelation.Unverifiable)),
            gapCount = rows.Count(r => r.Relation == FactRelation.Unverifiable),
            rules,
            keys,
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>基線內容雜湊（小寫十六進位 SHA-256；對 canonical JSON 的 UTF-8 位元組計算）。</summary>
    public static string ContentHash(string canonicalJson)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));

    private static HardwareFact BaselineFact(DateTimeOffset at, IReadOnlyList<FactRelationRow> rows, IReadOnlyList<HardwareFact> facts)
    {
        string json = BaselineJson(rows, facts);
        string hash = ContentHash(json);
        string value =
            $"基線格式 v1（政策 {PolicyVersion}）：內容雜湊 sha256:{hash}。"
            + "內容＝逐條規則結果（id／關係／參與鍵）＋參與鍵的可用性與值＋等級與缺口計數，鍵與規則皆排序；"
            + "不含時間、機器識別與路徑，所以同一台機器同一輪重跑會得到同一個雜湊。"
            + $"可離線比對：兩次基線的雜湊不同即代表這批結果有變（逐鍵差異見 {DeltaKey}）。"
            + "完整內容隨本程式的事實匯出（--json）一起出去；第三方用同一份輸入即可重算雜湊"
            + "（重算函式是本專案原始碼，純函式、不讀硬體）。";

        return new HardwareFact(BaselineKey, Category, "韌體基線（可離線比對）", value, "", "本輪事實的 canonical JSON",
            FactTrustLevel.Derived, false, at);
    }

    // ── P2-103 差異（由時間膠囊比較產生）──────────────────────────────────

    /// <summary>
    /// 兩次掃描之間的差異陳述，<b>只比對參與風險陳述的鍵</b>（<see cref="WatchKeys"/>）。
    ///
    /// <para>
    /// 跨機器直接拒比（<c>NotApplicable</c>）：兩台電腦的差異不是「變更」，把它們混在一起
    /// 會產出一份看起來很認真、實際上沒有意義的清單。
    /// </para>
    /// <para>
    /// <b>差異本身不是故障，也不建因果</b>：這裡只說「哪一把鍵從什麼變成什麼」，
    /// 中間發生了什麼事（更新、重置、驅動換版）不在這一條的範圍。
    /// </para>
    /// </summary>
    public static HardwareFact Delta(HardwareSnapshot before, HardwareSnapshot after, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var watch = WatchKeys();
        var diff = HardwareSnapshotService.Diff(before, after);
        if (!diff.IsSameMachine)
            return new HardwareFact(DeltaKey, Category, "韌體變更報告", "不同機器的比較沒有意義", "", "時間膠囊逐鍵差分",
                FactTrustLevel.Derived, false, at, null, FactAvailability.NotApplicable,
                "兩份快照的匿名機器識別不同——為避免把兩台電腦的差異誤認成硬體變更，本次不做差異陳述");

        var changes = diff.Changes
            .Where(c => c.Kind != SnapshotChangeKind.Unchanged && watch.Contains(c.Key, StringComparer.Ordinal))
            .ToList();

        string value;
        if (changes.Count == 0)
        {
            value = $"兩次掃描之間，韌體風險相關的 {watch.Count} 把鍵沒有差異（逐鍵比對）。"
                + "界線：某來源這次讀不到時會列為消失，不以舊值填補；沒有差異不等於沒有變更，只等於這兩次比對的鍵都一樣。";
        }
        else
        {
            int changed = changes.Count(c => c.Kind == SnapshotChangeKind.Changed);
            int added = changes.Count(c => c.Kind == SnapshotChangeKind.Added);
            int removed = changes.Count(c => c.Kind == SnapshotChangeKind.Removed);
            value = $"兩次掃描之間（限韌體風險相關鍵，共比對 {watch.Count} 把）：變更 {changed}、新增 {added}、消失 {removed}。"
                + string.Join("；", changes.Select(c => $"{c.Key}＝{Describe(c.Previous)} → {Describe(c.Current)}"))
                + "。差異本身不是故障，也不建因果——更新的、被重置的、讀不到的都會長成差異。";
        }

        return new HardwareFact(DeltaKey, Category, "韌體變更報告", value, "", "時間膠囊逐鍵差分（HardwareSnapshotService.Diff）",
            FactTrustLevel.Derived, false, at, changes.Count);
    }

    private static string Describe(HardwareFact? fact)
        => fact is null ? "（無）"
        : fact.Availability == FactAvailability.Present ? fact.Value
        : $"{fact.Value}（{fact.Availability}：{fact.UnavailableReason ?? "原因不明"}）";

    // ── P2-104 第三方複核報告 ─────────────────────────────────────────────

    /// <summary>
    /// 交第三方複核的報告：沿用既有的 <see cref="HtmlReportService"/>（自足單檔、尾端嵌可重算的 SHA-256）。
    /// 這一支刻意是薄殼——報告格式只有一份，多開一種格式就會有兩份會漂移的實作。
    /// </summary>
    public static string BuildThirdPartyReport(IReadOnlyList<EvidenceFactRow> rows, DateTimeOffset generatedAtUtc)
        => HtmlReportService.Build(rows, "曦覽 XinSpect ・ 韌體風險陳述（交第三方複核）", generatedAtUtc);

    private static HardwareFact ReportFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts, HardwareFact? chainVerify)
    {
        var chain = chainVerify ?? facts.FirstOrDefault(f => f.Key == ChainVerifyKey);
        string chainText = chain is null
            ? $"審計鏈狀態：未取得（本輪事實集沒有 {ChainVerifyKey}）——報告會如實標示未取得，不以「沒有問題」代替。"
            : $"審計鏈狀態：{chain.Value}（{chain.Availability}）。";

        string value =
            "報告＝自足單檔 HTML（無外部資源、離線可開），尾端嵌對內容計算的 SHA-256，"
            + "可在沒有本程式的機器上用任何工具重算驗證。"
            + $"內容：構成規則逐條結果＋等級＋缺口＋矛盾清單＋基線雜湊＋政策版本 {PolicyVersion}＋審計鏈狀態；"
            + "報告不自動下「好／壞」總結，讀不到的列如實顯示原因。"
            + chainText
            + "依賴 P1-072：審計鏈與 manifest 必須互相印證——單獨一項通過不足以證明報告未被改動。";

        return new HardwareFact(ReportKey, Category, "韌體稽核報告輸出", value, "", "HtmlReportService（自足單檔＋內嵌雜湊）",
            FactTrustLevel.Derived, false, at);
    }

    private static string RelationText(FactRelation relation) => relation switch
    {
        FactRelation.Consistent => "一致",
        FactRelation.Contradicts => "矛盾",
        _ => "無法驗證",
    };
}
