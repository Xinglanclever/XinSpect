using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>離線知識包的一條公告：產品、受影響組建上限、修補組建、CVE 編號、一句話、來源。</summary>
public sealed record OfflineAdvisory(
    [property: JsonPropertyName("cve")] string CveId,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("affectedUpToBuild")] string AffectedUpToBuild,
    [property: JsonPropertyName("fixedInBuild")] string? FixedInBuild,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("source")] string Source);

/// <summary>離線表的表頭（含<b>必要的快照日期</b>）。</summary>
public sealed record OfflineAdvisoryTable(
    [property: JsonPropertyName("snapshotDate")] string SnapshotDate,
    [property: JsonPropertyName("entries")] IReadOnlyList<OfflineAdvisory> Entries);

/// <summary>
/// 作業系統 CVE 離線對照（Vol 2 批次 C／SE-002）。
/// <para>
/// <b>這一支刻意「只做框架、不塞資料」。</b>理由是可驗證性：CVE 對照的每一列都是一句關於真實世界的
/// 斷言（哪個組建受影響、修補在哪個組建）。條目若要隨程式出貨，就必須有可查的來源、可追的快照日期，
/// 而且要能更新——那需要一條知識包流程（主綱 F4）。<b>沒有來源的 CVE 表是一份捏造的清單，
/// 比沒有這張表更糟</b>：它會讓使用者以為自己剛剛被比對過。
/// </para>
/// <para>
/// 本版交付：離線表<b>格式</b>（<see cref="OfflineAdvisoryTable"/>）、<b>載入器</b>（外部 JSON，
/// 快照日期缺漏即拒收）、<b>比對器</b>（組建號數字比較，不是字串比較），以及<b>誠實的申報</b>：
/// 收錄幾條、命中幾條、快照哪一天、覆蓋有多有限。隨程式出貨的表是空的——
/// 於是 <c>se.cve</c> 會如實說「無條目可比對，這不是『沒有問題』」。
/// </para>
/// </summary>
public static class CveOfflineFactsService
{
    public const string Category = "進階安全";
    public const string SummaryKey = "se.cve";

    /// <summary>命中清單最多列幾條（其餘以「等 N 條」收束）。</summary>
    private const int ListedMax = 10;

    /// <summary>本版隨程式出貨的離線表內容（空＝沒有條目）。</summary>
    public static IReadOnlyList<OfflineAdvisory> ShippedTable => [];

    /// <summary>外部離線表的預設檔名（與執行檔同目錄；不存在就沒有表）。</summary>
    public const string TableFileName = "cve-offline.json";

    /// <summary>外部離線表的預設路徑：<c>&lt;執行檔目錄&gt;/cve-offline.json</c>。</summary>
    public static string DefaultTablePath => Path.Combine(AppContext.BaseDirectory, TableFileName);

    private const string Source = "內建／外部離線表";

    // ── 載入與比對 ────────────────────────────────────────────────────────

    /// <summary>讀外部離線表。檔不存在回 null（＝沒有表）；檔在但格式或快照日期不合格則回 null 並帶原因。</summary>
    public static OfflineAdvisoryTable? Load(string path, out string? reason)
    {
        reason = null;
        if (!File.Exists(path)) return null;
        try
        {
            var table = JsonSerializer.Deserialize<OfflineAdvisoryTable>(File.ReadAllText(path));
            if (table is null)
            {
                reason = "檔案內容不是有效的離線表（解析回 null）";
                return null;
            }
            if (string.IsNullOrWhiteSpace(table.SnapshotDate))
            {
                reason = "離線表缺少 snapshotDate——沒有快照日期的對照表不能出貨（讀者無從判斷它多舊）";
                return null;
            }
            return table;
        }
        catch (Exception ex)
        {
            reason = $"離線表解析失敗（{ex.GetType().Name}）：{ex.Message}";
            return null;
        }
    }

    /// <summary>組建號數字比較：取字串中第一段大於 1000 的數字（<c>10.0.26100.1</c> → 26100）。解析不到回 null。</summary>
    public static int? BuildNumber(string? build)
    {
        if (string.IsNullOrWhiteSpace(build)) return null;
        var parts = build.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string p in parts)
            if (int.TryParse(p, out int n) && n > 1000) return n;
        return null;
    }

    /// <summary>命中＝本機組建 ≤ 該條公告的受影響上限（純數字比對，與廠牌字串無關）。</summary>
    public static IReadOnlyList<OfflineAdvisory> Match(IReadOnlyList<OfflineAdvisory> table, string? build)
    {
        if (BuildNumber(build) is not { } mine) return [];
        return table.Where(a =>
        {
            int? upTo = BuildNumber(a.AffectedUpToBuild);
            return upTo is not null && mine <= upTo;
        }).ToList();
    }

    // ── 事實 ──────────────────────────────────────────────────────────────

    /// <summary>本機版本資訊（<see cref="Environment.OSVersion"/>），供自動收集使用。</summary>
    public static IReadOnlyList<HardwareFact> CollectForThisMachine(DateTimeOffset at,
        Func<(IReadOnlyList<OfflineAdvisory> Table, string SnapshotDate, string? LoadReason)>? probe = null)
    {
        var v = Environment.OSVersion.Version;
        return Collect(at, "Windows", v.ToString(), v.Build.ToString(), probe);
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, string? osName, string? osVersion,
        string? build, Func<(IReadOnlyList<OfflineAdvisory> Table, string SnapshotDate, string? LoadReason)>? probe = null)
    {
        (IReadOnlyList<OfflineAdvisory> Table, string SnapshotDate, string? LoadReason) source;
        if (probe is not null)
        {
            source = probe();
        }
        else
        {
            var external = Load(DefaultTablePath, out string? reason);
            source = external is not null
                ? (external.Entries, external.SnapshotDate, null)
                : (ShippedTable, "—", reason ?? "本版隨程式出貨的離線表是空的（可放一份 "
                    + TableFileName + " 在執行檔目錄）");
        }

        if (BuildNumber(build) is null)
            return
            [
                new HardwareFact(SummaryKey, Category, "作業系統 CVE 離線對照", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    "讀不到作業系統組建號（版本字串「" + (osVersion ?? "—") + "」解析失敗）——無法對照，不假裝沒問題"),
            ];

        var hits = Match(source.Table, build);
        string snapshot = source.SnapshotDate == "—" ? "快照日期缺失" : "快照 " + source.SnapshotDate;
        string coverage = source.Table.Count == 0
            ? "收錄 0 條——沒有任何條目可比對（這不是『沒有問題』，是『還沒有資料』）"
            : "收錄 " + source.Table.Count + " 條（離線表覆蓋有限，不是完整 CVE 資料庫）";
        string reasonPart = source.LoadReason is null ? "" : "（" + source.LoadReason + "）";
        // 命中明細串在同一個值裡（不另立鍵）：這一版刻意不新增動態家族；每條均宣布「待查證」不是「已中招」。
        string hitDetail = hits.Count == 0
            ? ""
            : "命中：" + string.Join("；", hits.Take(ListedMax).Select(h =>
                  h.CveId + " " + h.Product + "（受影響 ≤ " + h.AffectedUpToBuild
                  + "／修補 " + (h.FixedInBuild ?? "未標") + "）待查證"))
              + (hits.Count > ListedMax ? "；等 " + (hits.Count - ListedMax) + " 條" : "") + "。";
        string value = (osName ?? "作業系統") + " " + (osVersion ?? "") + "（組建 " + build + "）・" + snapshot
            + "・" + coverage + "・命中 " + hits.Count + " 條。" + hitDetail
            + "命中不等於已中招——還要看修補狀態與實際元件；本表只回答『有沒有值得查的條目』。" + reasonPart;

        return
        [
            new(SummaryKey, Category, "作業系統 CVE 離線對照", value, "條", Source,
                FactTrustLevel.Derived, false, at, hits.Count,
                source.Table.Count == 0 ? FactAvailability.NotSupported : FactAvailability.Present,
                source.Table.Count == 0 ? "離線表沒有條目可比對——不適用，不是『沒有已知問題』"
                    : hits.Count > 0 ? "命中 " + hits.Count + " 條待查證修補狀態（不視為已中招）" : null),
        ];
    }
}
