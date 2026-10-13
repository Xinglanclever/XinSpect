using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 治理與知識收口（Vol2 第 343–345 項）：DK-002 規則候選輔助（<c>dk.candidate</c>）、
/// DK-007 資料保留政策（<c>dk.retention</c>）、DK-010 文件-程式對帳（<c>dk.docmap</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一批是「關於本工具自己」的事實</b>：候選規則從矛盾記錄來（人核可才入，不自動）、
/// 保留政策陳述歷史裁剪的不可逆性、文件對帳檢查 docs 宣稱的功能有沒有實作。
/// </para>
/// <para>
/// <b>紅線：</b>① 候選規則<b>絕不自動入庫</b>——只產生候選並附「需人核可」；② 裁剪<b>不可逆</b>要聲明，
/// 且審計鏈（<see cref="AuditLogService"/>）明文列為例外不裁剪；③ 文件對帳只<b>比對計數與存在性</b>，
/// 不自動改文件；④ 讀不到一律三態。
/// </para>
/// </remarks>
public static class GovernanceKnowledgeFactsService
{
    private const string Cat = "治理";

    public const string CandidateKey = "dk.candidate";
    public const string RetentionKey = "dk.retention";
    public const string DocMapKey = "dk.docmap";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    /// <summary>矛盾記錄（規則命中歷史）→ 可提為候選的訊號數。</summary>
    public delegate (int ConflictRecords, int DistinctKeys)? CandidateProbe();
    /// <summary>（歷史檔筆數, 最舊樣本天數, 是否已裁剪過）。</summary>
    public delegate (int Entries, double OldestDays, bool Trimmed)? RetentionProbe();
    /// <summary>（docs 宣稱的功能數, 程式碼可見的實作數）。</summary>
    public delegate (int Claimed, int Implemented)? DocMapProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        CandidateProbe? candidate = null,
        RetentionProbe? retention = null,
        DocMapProbe? docMap = null)
        =>
        [
            CandidateFact(at, candidate),
            RetentionFact(at, retention),
            DocMapFact(at, docMap),
        ];

    // ── DK-002 規則候選輔助 ────────────────────────────────────────────

    public static HardwareFact CandidateFact(DateTimeOffset at, CandidateProbe? probe = null)
    {
        (int ConflictRecords, int DistinctKeys)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, CandidateKey, Cat, "規則候選輔助", FactAvailability.ReadError,
                "矛盾記錄不可讀——讀不到就是不猜");

        var (records, keys) = s.Value;
        if (records == 0)
            return new HardwareFact(CandidateKey, Cat, "規則候選輔助",
                "目前沒有矛盾記錄可供提為規則候選。0 是值：沒有記錄不代表規則庫完整。",
                "筆", "規則命中歷史（唯讀）", FactTrustLevel.Reported, false, at, 0);

        return new HardwareFact(CandidateKey, Cat, "規則候選輔助",
            $"{records} 筆矛盾記錄、涉及 {keys} 個相異鍵。" +
            "這些是<b>候選</b>不是規則——把矛盾提為規則必須<b>人工核可</b>，本工具不自動入庫" +
            "（自動入庫會讓一次性雜訊變成永久判決）。",
            "筆", "規則命中歷史（唯讀）", FactTrustLevel.Derived, false, at, records);
    }

    // ── DK-007 資料保留政策 ────────────────────────────────────────────

    public static HardwareFact RetentionFact(DateTimeOffset at, RetentionProbe? probe = null)
    {
        (int Entries, double OldestDays, bool Trimmed)? s;
        try { s = (probe ?? ReadRetention)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, RetentionKey, Cat, "資料保留政策", FactAvailability.ReadError,
                "歷史倉不可讀——讀不到就是不猜");

        var (entries, oldest, trimmed) = s.Value;
        return new HardwareFact(RetentionKey, Cat, "資料保留政策",
            $"歷史 {entries} 筆、最舊 {oldest:0.#} 天{(trimmed ? "、曾執行裁剪" : "")}。" +
            "<b>裁剪不可逆</b>——被裁掉的樣本無法還原；審計鏈（稽核日誌）" +
            "明文列為例外，<b>永不裁剪</b>。本項只陳述政策狀態，不執行裁剪。",
            "筆", "歷史倉（唯讀）", FactTrustLevel.Reported, false, at, entries);
    }

    private static (int, double, bool)? ReadRetention()
    {
        try
        {
            // 只讀歷史倉檔案是否存在與大小，不解析內容（保留政策是宣告，非量測）
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XinSpect", "History");
            if (!System.IO.Directory.Exists(dir)) return (0, 0, false);
            var files = System.IO.Directory.GetFiles(dir, "*.bin");
            if (files.Length == 0) return (0, 0, false);
            var oldest = files.Select(f => System.IO.File.GetLastWriteTimeUtc(f)).Min();
            double days = (DateTime.UtcNow - oldest).TotalDays;
            return (files.Length, days, false);
        }
        catch { return null; }
    }

    // ── DK-010 文件-程式對帳 ───────────────────────────────────────────

    public static HardwareFact DocMapFact(DateTimeOffset at, DocMapProbe? probe = null)
    {
        (int Claimed, int Implemented)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null || s.Value.Claimed == 0)
            return Unavailable(at, DocMapKey, Cat, "文件-程式對帳", FactAvailability.NotApplicable,
                "沒有可比對的文件宣稱——缺文件不判差異");

        var (claimed, implemented) = s.Value;
        int gap = claimed - implemented;
        return new HardwareFact(DocMapKey, Cat, "文件-程式對帳",
            $"文件宣稱 {claimed} 項、程式碼可見 {implemented} 項{(gap > 0 ? $"、落差 {gap} 項" : "、無落差")}。" +
            "<b>防文件說謊</b>：這只比對計數與存在性，無法判斷實作是否真的等價；" +
            "落差需人工核對——本項不自動改文件。",
            "項", "docs ↔ Services 掃描（唯讀）", FactTrustLevel.Derived, false, at, implemented);
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
