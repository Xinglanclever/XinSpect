using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 規格引用與出處鏈（ULTIMATE-1000 階段一 P1-024 – P1-031）。
///
/// <para>
/// <b>這一支不重新發明覆蓋面資料。</b><see cref="SpecRefRegistry"/> 已經有 46 個解碼器註冊、
/// <c>MethodsMissingRefs()</c> 與 <c>AllReferences()</c> 兩個查詢介面——缺的是
/// 「把那些資料變成事實輸出」，於是覆蓋面的實情只有開發者看得到，使用者看不到。
/// </para>
///
/// <para>
/// <b>這一組的六把鍵對應六個不同的問題，不能合併：</b>
/// </para>
/// <list type="bullet">
/// <item><c>spec.ref</c>——有幾條引用、涵蓋哪些解碼器。</item>
/// <item><c>spec.ref.granularity</c>——引用夠不夠細（位移級 vs 只寫「某規格」）。</item>
/// <item><c>spec.ref.coverage</c>——逐解碼器列出有引用與缺口。</item>
/// <item><c>spec.ref.version</c>——引用的規格版本與抓取日期。</item>
/// <item><c>spec.ref.unit</c>——換算係數的出處（單位換算也要有依據）。</item>
/// <item><c>fact.provenance</c>——值可回溯到出處的位置。</item>
/// </list>
/// </summary>
public static class SpecRefReport
{
    public const string RefKey = "spec.ref";
    public const string GranularityKey = "spec.ref.granularity";
    public const string CoverageKey = "spec.ref.coverage";
    public const string VersionKey = "spec.ref.version";
    public const string UnitKey = "spec.ref.unit";
    public const string ProvenanceKey = "fact.provenance";
    public const string ConflictKey = "spec.ref.conflict";
    public const string ExportProvenanceKey = "export.provenance";

    /// <summary>
    /// 位移／位元級引用的特徵：出現 <c>@數字</c>、<c>offset</c>、<c>位元組</c>、<c>bit</c>、
    /// <c>§數字.數字</c> 這類可以定位到具體位置的描述。
    /// <b>這是啟發式判斷，不是判決</b>——所以它只用來標「可能不夠細」，並把原文附上讓使用者自己看。
    /// </summary>
    private static readonly string[] GranularityMarkers =
        ["@", "offset", "位元組", "bit", "位元", "§", "Table", "表 ", "欄位"];

    /// <summary>引用是否細到可以定位。純啟發式：沒有任何定位標記的引用視為「只寫到規格層級」。</summary>
    public static bool IsGranular(string reference) =>
        GranularityMarkers.Any(m => reference.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>產生 <c>spec.ref</c>：引用總數與涵蓋的解碼器數。</summary>
    public static HardwareFact ToRefFact(DateTimeOffset at)
    {
        var refs = SpecRefRegistry.AllReferences();
        int decoders = SpecRefRegistry.CoveredDecoders.Length;

        if (refs.Count == 0)
            return new HardwareFact(RefKey, "出處鏈", "規格引用",
                "沒有任何引用", "條", "SpecRef 登錄", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "登錄中沒有任何引用條目");

        return new HardwareFact(RefKey, "出處鏈", "規格引用",
            $"{refs.Count} 條引用、涵蓋 {decoders} 個解碼器", "條",
            "SpecRef 登錄", FactTrustLevel.Derived, false, at, refs.Count,
            FactAvailability.Present, null);
    }

    /// <summary>
    /// 產生 <c>spec.ref.granularity</c>：不夠細的引用逐條列出。
    /// 這一項的價值在於「位移級出處不得只寫『某規格』」——沒有這把鍵，那句話只是口號。
    /// </summary>
    public static HardwareFact ToGranularityFact(DateTimeOffset at)
    {
        var refs = SpecRefRegistry.AllReferences();
        var coarse = refs.Where(r => !IsGranular(r.Reference)).ToList();

        if (refs.Count == 0)
            return new HardwareFact(GranularityKey, "出處鏈", "引用解析度",
                "沒有引用可以檢查", "條", "SpecRef 登錄", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "登錄中沒有任何引用條目");

        if (coarse.Count == 0)
            return new HardwareFact(GranularityKey, "出處鏈", "引用解析度",
                $"{refs.Count} 條引用全部可定位到欄位或位移", "條",
                "SpecRef 登錄", FactTrustLevel.Derived, false, at, 0, FactAvailability.Present, null);

        return new HardwareFact(GranularityKey, "出處鏈", "引用解析度",
            $"{coarse.Count}/{refs.Count} 條引用只寫到規格層級：" +
            string.Join("；", coarse.Take(8).Select(c => $"{c.Member}：{Trim(c.Reference)}")),
            "條", "SpecRef 登錄", FactTrustLevel.Derived, false, at, coarse.Count,
            FactAvailability.Unknown,
            $"{coarse.Count} 條引用缺少可定位的位置描述（可能是規格本身只提供到這一級，需人工確認）");
    }

    /// <summary>產生 <c>spec.ref.coverage</c>：逐解碼器列出引用數與缺口。</summary>
    public static HardwareFact ToCoverageFact(DateTimeOffset at, int totalPublicMethods)
    {
        var missing = SpecRefRegistry.MethodsMissingRefs();
        int decoders = SpecRefRegistry.CoveredDecoders.Length;

        if (totalPublicMethods == 0)
            return new HardwareFact(CoverageKey, "出處鏈", "出處覆蓋",
                "沒有可統計的公開解碼方法", "個", "SpecRef 登錄", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "這一輪沒有解碼方法可統計");

        int covered = totalPublicMethods - missing.Count;
        if (missing.Count == 0)
            return new HardwareFact(CoverageKey, "出處鏈", "出處覆蓋",
                $"{decoders} 個解碼器、{covered}/{totalPublicMethods} 個公開方法都有引用", "個",
                "SpecRef 登錄", FactTrustLevel.Derived, false, at, covered,
                FactAvailability.Present, null);

        return new HardwareFact(CoverageKey, "出處鏈", "出處覆蓋",
            $"{decoders} 個解碼器、{covered}/{totalPublicMethods} 有引用；缺：" +
            string.Join("、", missing.Take(10)), "個",
            "SpecRef 登錄", FactTrustLevel.Derived, false, at, covered,
            FactAvailability.ReadError, $"{missing.Count} 個公開解碼方法沒有 SpecRef");
    }

    /// <summary>
    /// 產生 <c>spec.ref.version</c>：引用裡有標出版本／年份的條數。
    ///
    /// <para>
    /// <b>這一項刻意只回報「有多少條標了版本」，不猜哪些沒標。</b>
    /// 「規格版本」在引用文字裡的寫法太多（r6.5、2020、Vol. 3、v2.0…），
    /// 用啟發式判斷「沒標版本」會產生大量誤報——誤報會讓守門被放寬，
    /// 而放寬過的守門比沒有守門更糟。所以只報確定的那一半。
    /// </para>
    /// </summary>
    public static HardwareFact ToVersionFact(DateTimeOffset at, DateTimeOffset capturedAt)
    {
        var refs = SpecRefRegistry.AllReferences();
        if (refs.Count == 0)
            return new HardwareFact(VersionKey, "出處鏈", "規格版本",
                "沒有引用可以檢查", "條", "SpecRef 登錄", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "登錄中沒有任何引用條目");

        var versioned = refs.Where(r => LooksVersioned(r.Reference)).ToList();
        return new HardwareFact(VersionKey, "出處鏈", "規格版本",
            $"{versioned.Count}/{refs.Count} 條引用標有版本或年份；"
            + $"本清單的引用於 {capturedAt:yyyy-MM-dd} 校對", "條",
            "SpecRef 登錄", FactTrustLevel.Derived, false, at, versioned.Count,
            versioned.Count == refs.Count ? FactAvailability.Present : FactAvailability.Unknown,
            versioned.Count == refs.Count ? null : $"{refs.Count - versioned.Count} 條引用未標版本（需人工確認規格是否本身無版次）");
    }

    private static bool LooksVersioned(string reference) =>
        System.Text.RegularExpressions.Regex.IsMatch(reference,
            @"\b(r\d+(\.\d+)?|v\d+(\.\d+)?|Vol\.?\s*\d|\d{4}|rev\.?\s*\d|release\s*\d)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// 產生 <c>spec.ref.unit</c>：單位換算係數的出處申報。
    ///
    /// <para>
    /// <b>為什麼換算係數也要有出處：</b>「kHz ÷ 1000 = MHz」看起來是常識，
    /// 但 DDR5 的 MT/s 與 MHz、NVMe 的 GB 與 GiB 都不是。係數寫錯的後果是
    /// <b>一個看起來完全正常的錯數字</b>——比讀不到更難發現。
    /// </para>
    /// </summary>
    public static HardwareFact ToUnitFact(DateTimeOffset at,
        IReadOnlyList<(string Name, double Factor, string Source)> conversions)
    {
        if (conversions.Count == 0)
            return new HardwareFact(UnitKey, "出處鏈", "換算係數出處",
                "本輪沒有登記任何換算係數", "條", "單位換算登錄", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有換算係數可以檢查");

        var unsourced = conversions.Where(c => string.IsNullOrWhiteSpace(c.Source)).ToList();
        if (unsourced.Count == 0)
            return new HardwareFact(UnitKey, "出處鏈", "換算係數出處",
                $"{conversions.Count} 個換算係數全部附有出處", "條",
                "單位換算登錄", FactTrustLevel.Derived, false, at, conversions.Count,
                FactAvailability.Present, null);

        return new HardwareFact(UnitKey, "出處鏈", "換算係數出處",
            $"{unsourced.Count}/{conversions.Count} 個換算係數沒有出處：" +
            string.Join("、", unsourced.Select(u => u.Name)), "條",
            "單位換算登錄", FactTrustLevel.Derived, false, at, conversions.Count - unsourced.Count,
            FactAvailability.ReadError,
            $"{unsourced.Count} 個係數沒有出處——係數寫錯會產生一個看起來正常的錯數字");
    }

    /// <summary>
    /// 產生 <c>fact.provenance</c>：值 → 出處的對應稽核。
    /// 這一項檢查的是「每一個有值的事實都指得回一條引用」——沒有引用的值就是未驗證的值。
    /// </summary>
    public static HardwareFact ToProvenanceFact(DateTimeOffset at, IReadOnlyCollection<HardwareFact> facts,
        IReadOnlyCollection<string> unverifiedKeys)
    {
        if (facts.Count == 0)
            return new HardwareFact(ProvenanceKey, "出處鏈", "值與出處對應",
                "這一輪沒有任何事實", "筆", "出處稽核", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有事實可以稽核");

        int withValue = facts.Count(f => f.Availability == FactAvailability.Present);
        if (unverifiedKeys.Count == 0)
            return new HardwareFact(ProvenanceKey, "出處鏈", "值與出處對應",
                $"{withValue} 筆有值的事實全部可回溯到出處", "筆",
                "出處稽核", FactTrustLevel.Derived, false, at, withValue,
                FactAvailability.Present, null);

        return new HardwareFact(ProvenanceKey, "出處鏈", "值與出處對應",
            $"{unverifiedKeys.Count} 筆有值的事實沒有出處：" +
            string.Join("、", unverifiedKeys.Take(10)), "筆",
            "出處稽核", FactTrustLevel.Derived, false, at, withValue - unverifiedKeys.Count,
            FactAvailability.Unknown,
            $"{unverifiedKeys.Count} 筆值沒有可回溯的出處——沒有出處的值等於未驗證");
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..60] + "…";

    // ── P1-030 出處衝突處理 ─────────────────────────────────────────────

    /// <summary>
    /// 產生 <c>spec.ref.conflict</c>：兩個來源給了不同值時，<b>並列</b>而不是挑一個。
    ///
    /// <para>
    /// <b>為什麼不挑一個：</b>挑一個＝替使用者做判斷，而判斷需要他知道我們不知道的事
    /// （哪個來源比較可信、哪個是舊的）。並列兩個值與各自的出處，讓使用者自己決定，
    /// 是這一項唯一誠實的做法。
    /// </para>
    ///
    /// <para>
    /// <b>「沒有衝突」與「只有一個來源」必須分開：</b>前者是「兩個來源都讀了而且一致」，
    /// 後者是「根本沒得比」。回報時若混為一談，就會把「沒檢查」說成「沒問題」。
    /// </para>
    /// </summary>
    public static HardwareFact ToConflictFact(DateTimeOffset at,
        IReadOnlyList<(string Subject, int SourceCount, IReadOnlyList<(string Source, string Value)> Values)> subjects)
    {
        if (subjects.Count == 0)
            return new HardwareFact(ConflictKey, "出處鏈", "出處衝突",
                "本輪沒有可比較的主題", "項", "出處衝突並列", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有任何主題有兩個以上的來源");

        int comparable = subjects.Count(s => s.SourceCount >= 2);
        if (comparable == 0)
            return new HardwareFact(ConflictKey, "出處鏈", "出處衝突",
                $"{subjects.Count} 個主題都只有單一來源——沒有可比較的對象", "項",
                "出處衝突並列", FactTrustLevel.Derived, false, at, 0,
                FactAvailability.NotApplicable,
                "單一來源不等於一致：這不是「沒有衝突」，是「沒得比」");

        var conflicts = subjects.Where(s => s.SourceCount >= 2
            && s.Values.Select(v => v.Value).Distinct(StringComparer.Ordinal).Count() > 1).ToList();

        if (conflicts.Count == 0)
            return new HardwareFact(ConflictKey, "出處鏈", "出處衝突",
                $"{comparable} 個可比較的主題全部一致", "項",
                "出處衝突並列", FactTrustLevel.Derived, false, at, 0, FactAvailability.Present, null);

        string detail = string.Join("；", conflicts.Select(c =>
            $"{c.Subject}：" + string.Join(" vs ", c.Values.Select(v => $"{v.Source} 說 {v.Value}"))));

        return new HardwareFact(ConflictKey, "出處鏈", "出處衝突", detail, "項",
            "出處衝突並列", FactTrustLevel.Derived, false, at, conflicts.Count,
            // 衝突不是錯誤，是「需要人看一眼」——所以標 Unknown 而不是 ReadError。
            FactAvailability.Unknown,
            $"{conflicts.Count} 個主題的來源彼此不一致——並列而不代為選擇");
    }

    // ── P1-032 出處鏈匯出 ───────────────────────────────────────────────

    /// <summary>
    /// 產生 <c>export.provenance</c>：報告匯出時，逐值附上出處。
    ///
    /// <para>
    /// <b>匯出沒有出處的後果比畫面上沒有更嚴重：</b>畫面上的值旁邊還有其他線索
    /// （相鄰欄位、時間戳）；匯出後那個數字會單獨出現在別人的簡報裡，
    /// 沒有出處就無法查證，也無法知道它是不是被遮蔽過。
    /// </para>
    /// </summary>
    public static HardwareFact ToExportProvenanceFact(DateTimeOffset at,
        int exportedValues, int withProvenance, int redactedByPolicy)
    {
        if (exportedValues == 0)
            return new HardwareFact(ExportProvenanceKey, "出處鏈", "匯出出處鏈",
                "本輪沒有匯出任何值", "筆", "匯出出處", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有匯出可以檢查");

        int without = exportedValues - withProvenance;
        if (without <= 0)
            return new HardwareFact(ExportProvenanceKey, "出處鏈", "匯出出處鏈",
                $"{exportedValues} 筆匯出值全部附有出處"
                + (redactedByPolicy > 0 ? $"（另 {redactedByPolicy} 筆依敏感度政策遮蔽）" : ""),
                "筆", "匯出出處", FactTrustLevel.Derived, false, at, withProvenance,
                FactAvailability.Present, null);

        return new HardwareFact(ExportProvenanceKey, "出處鏈", "匯出出處鏈",
            $"{without}/{exportedValues} 筆匯出值沒有出處", "筆",
            "匯出出處", FactTrustLevel.Derived, false, at, withProvenance,
            FactAvailability.Unknown,
            $"{without} 筆匯出值沒有出處——匯出後它們會離開這個畫面，沒有出處就無法查證");
    }
}
