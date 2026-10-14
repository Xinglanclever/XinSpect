namespace XinSpect;

/// <summary>
/// 裝置合規基線（Vol2 第 ZR-004 項）：<c>zr.compliance.baseline</c>、
/// <c>zr.compliance.table</c>——把「這台機器符不符合某份清單」從<b>零信任的角度</b>回答。
/// </summary>
/// <remarks>
/// <para>
/// <b>與既有能力的界線：</b>本專案已經有很多「對照表」——<c>el.*</c>（EOL）、
/// <c>fw.ucode.cve</c>（微碼 CVE）、<c>ws.kb.known</c>（KB 已知問題）、
/// <c>se.baseline</c>（安全基線報告）。它們的共同點是<b>表由本專案維護</b>。
/// 本項不同：用的是使用者提供的<b>對照清單</b>（機構自己的合規要求），
/// 放在知識包框架裡（<see cref="KnowledgePackService"/>），所以有快照日期與完整性序列號。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① <b>快照日期必印</b>——離線清單會過期，日期是它唯一的新鮮度線索；
/// 沒有日期的清單<b>整包拒收</b>（同 <see cref="KnowledgePackService"/> 的政策）；
/// ② <b>「符合」不是安全背書</b>——符不符合一份清單，與這台機器安全不安全是兩件事；
/// 清單沒涵蓋到的面向不會因為「全部符合」而變成沒問題；
/// ③ <b>三態分明</b>：沒提供清單＝<see cref="FactAvailability.NotApplicable"/>、
/// 有清單但讀不到某項＝不能算「符合」（否則缺資料會偽裝成通過）；
/// ④ <b>不列清單的具體條目文字</b>——機構的合規要求可能含內部控制編號，
/// 只報「對了幾項、缺了幾項」。
/// </para>
/// </remarks>
public static class DeviceComplianceFactsService
{
    private const string Cat = "零信任";
    private const string Source = "知識包離線對照清單（使用者提供；缺則不適用）";

    /// <summary>ZR-004 合規基線摘要。</summary>
    public const string BaselineKey = "zr.compliance.baseline";
    /// <summary>ZR-004 合規清單本身的可讀性。</summary>
    public const string TableKey = "zr.compliance.table";

    /// <summary>本項使用的知識表名稱。</summary>
    public const string TableName = "device-compliance";

    /// <summary>一條合規要求的評估結果。</summary>
    /// <param name="RequirementId">要求的識別碼（如控制編號）；<b>不是</b>要求全文。</param>
    /// <param name="Met">是否符合。</param>
    /// <param name="Reason">判定的依據（哪把事實鍵、什麼值）。</param>
    public sealed record RequirementResult(string RequirementId, bool Met, string Reason);

    /// <summary>整份清單的評估。</summary>
    /// <param name="SnapshotDate">清單快照日期（必填，缺整包拒收）。</param>
    /// <param name="Total">要求總數。</param>
    /// <param name="Met">符合數。</param>
    /// <param name="Unresolved">無法判定數（來源讀不到或要求指向不存在的鍵）——<b>這不算符合</b>。</param>
    /// <param name="Detail">逐條結果（僅供彙總，值裡只出計數）。</param>
    public sealed record ComplianceEvaluation(
        string SnapshotDate, int Total, int Met, int Unresolved,
        IReadOnlyList<RequirementResult> Detail);

    /// <summary>
    /// 評估一份合規清單。<paramref name="requirements"/> 是「要求識別碼 → 事實鍵」；
    /// <paramref name="facts"/> 是本輪事實（用來查值）。
    /// <b>指向不存在鍵的要求算 Unresolved 而不是 Met</b>——鍵表改版時，
    /// 「查不到」絕不能變成「通過」。
    /// </summary>
    public static ComplianceEvaluation Evaluate(
        string snapshotDate,
        IReadOnlyDictionary<string, string> requirements,
        IReadOnlyDictionary<string, HardwareFact> facts)
    {
        var detail = new List<RequirementResult>(requirements.Count);
        int met = 0, unresolved = 0;

        foreach (var (id, key) in requirements.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!facts.TryGetValue(key, out var fact))
            {
                unresolved++;
                detail.Add(new RequirementResult(id, false, $"找不到事實鍵 {key}（鍵表可能改版）——不算符合"));
                continue;
            }
            if (fact.Availability != FactAvailability.Present)
            {
                unresolved++;
                detail.Add(new RequirementResult(id, false,
                    $"{key} 目前是 {fact.Availability}（{fact.UnavailableReason ?? "無原因"}）——缺資料不算符合"));
                continue;
            }
            met++;
            detail.Add(new RequirementResult(id, true, $"{key} 有值"));
        }

        return new ComplianceEvaluation(snapshotDate, requirements.Count, met, unresolved, detail);
    }

    // ── 事實 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 收集合規事實。<paramref name="pack"/> 為 null ＝沒有知識包（用內建表，
    /// 而內建表沒有合規清單 → 不適用）。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        KnowledgePack? pack,
        IReadOnlyDictionary<string, HardwareFact>? facts)
    {
        var table = pack?.Tables.FirstOrDefault(t =>
            string.Equals(t.Table, TableName, StringComparison.OrdinalIgnoreCase));

        if (table is null)
            return
            [
                Unavailable(at, TableKey, "合規對照清單", FactAvailability.NotApplicable,
                    $"知識包裡沒有「{TableName}」表——不適用。" +
                    "合規清單必須由使用者提供（機構自己的要求，本專案不代為維護）；" +
                    "沒有它不等於不合規，也不等於合規"),
                Unavailable(at, BaselineKey, "裝置合規基線", FactAvailability.NotApplicable,
                    $"沒有「{TableName}」清單可比對——不適用，不是「全部符合」也不是「全部不符合」"),
            ];

        // 快照日期是清單唯一的新鮮度線索；缺了整包拒收（同 KnowledgePackService 政策）。
        if (string.IsNullOrWhiteSpace(table.SnapshotDate))
            return
            [
                Unavailable(at, TableKey, "合規對照清單", FactAvailability.ReadError,
                    "合規清單沒有快照日期——整包拒收。讀者無從判斷它多舊，" +
                    "而一份不知道多舊的合規清單比沒有清單更危險：它會給出看起來確定的答案"),
                Unavailable(at, BaselineKey, "裝置合規基線", FactAvailability.ReadError,
                    "合規清單缺快照日期——拒收"),
            ];

        if (table.Entries.Count == 0)
            return
            [
                new HardwareFact(TableKey, Cat, "合規對照清單",
                    $"清單存在（快照 {table.SnapshotDate}）但沒有任何要求項目。", "項",
                    Source, FactTrustLevel.Reported, false, at, 0),
                Unavailable(at, BaselineKey, "裝置合規基線", FactAvailability.NotApplicable,
                    "合規清單是空的——沒有要求可評估"),
            ];

        var eval = facts is null
            ? null
            : Evaluate(table.SnapshotDate, table.Entries, facts);

        string coverage = eval is null
            ? "本輪沒有事實可比對"
            : $"{eval.Met}／{eval.Total} 項符合"
              + (eval.Unresolved > 0
                  ? $"・<b>{eval.Unresolved} 項無法判定</b>（來源讀不到或要求指向不存在的鍵）" +
                    "——<b>這不算符合</b>：缺資料偽裝成通過是這一項最該避免的結果"
                  : "");

        return
        [
            new HardwareFact(TableKey, Cat, "合規對照清單",
                $"清單 {table.Entries.Count} 項・<b>快照 {table.SnapshotDate}</b>。" +
                "<b>快照日期必印</b>：離線清單會過期，日期是它唯一的新鮮度線索——" +
                "一份 2024 年的合規清單不會知道 2026 年新增的要求。" +
                "<b>不列清單的具體條目文字</b>：機構的合規要求可能含內部控制編號，" +
                "只報「對了幾項、缺了幾項」。" +
                "清單由使用者提供（放在知識包裡，因此有完整性序列號可驗）；" +
                "本專案不代為維護合規要求。",
                "項", Source, FactTrustLevel.Reported, false, at, table.Entries.Count),

            new HardwareFact(BaselineKey, Cat, "裝置合規基線",
                $"{coverage}。清單快照 {table.SnapshotDate}。" +
                "<b>「符合」不是安全背書</b>：符不符合一份清單，與這台機器安全不安全是兩件事——" +
                "清單沒涵蓋到的面向不會因為「全部符合」而變成沒問題。" +
                "本項不做分數、不做等級，只回答「這份清單上的每一項，本機的資料支不支持它成立」。",
                "項", Source, FactTrustLevel.Derived, false, at, eval?.Met ?? 0),
        ];
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
