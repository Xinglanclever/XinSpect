using System.IO;
using System.Text;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 匯出與查詢快取（Vol2 推進第 102–103 項）：RS-006 JSONL 串流、QS-007 查詢結果快取。
/// </summary>
/// <remarks>
/// <para><b>RS-006</b>：每行一個事實物件（JSONL），敏感值依 <see cref="SensitiveValuePolicy"/> 處理；
/// 寫檔由呼叫端指定路徑、零網路。</para>
/// <para><b>QS-007</b>：以「事實指紋（鍵＋值＋可用性）」當失效條件——指紋不同即失效，
/// 不做時間型快取；快取命中時仍標示來源是快取，殘留一律當 Unknown 而不是 Present。</para>
/// </remarks>
public static class JsonlExportService
{
    public const string FactKey = "rs.jsonl";

    private sealed record Row(
        string key, string category, string name, string value, string unit,
        string trust, string availability, string? unavailableReason, bool sensitive, DateTimeOffset measuredAtUtc);

    /// <summary>把事實串成 JSONL（每行一個物件；UTF-8 不帶 BOM；敏感值依政策遮蔽）。</summary>
    public static string Serialize(IEnumerable<HardwareFact> facts, SensitiveValuePolicy policy = SensitiveValuePolicy.Redact)
    {
        var sb = new StringBuilder();
        foreach (var f in facts ?? [])
        {
            bool redact = policy == SensitiveValuePolicy.Redact && f.Sensitive && !string.IsNullOrEmpty(f.Value);
            var row = new Row(f.Key, f.Category, f.Name,
                redact ? "（敏感值已遮蔽）" : f.Value,
                f.Unit, f.Trust.ToString(), f.Availability.ToString(),
                f.Availability == FactAvailability.Present ? null : f.UnavailableReason,
                f.Sensitive, f.MeasuredAtUtc);
            sb.Append(JsonSerializer.Serialize(row)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>寫檔（UTF-8 無 BOM）；由呼叫端給路徑，不預設任何位置。</summary>
    public static async Task WriteAsync(string path, IReadOnlyList<HardwareFact> facts,
        SensitiveValuePolicy policy = SensitiveValuePolicy.Redact, CancellationToken cancellationToken = default)
    {
        string text = Serialize(facts, policy);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    public static HardwareFact Describe(DateTimeOffset at)
        => new(FactKey, "報告輸出", "JSONL 串流",
            "JSONL 匯出可用：每行一個事實物件（key／category／name／value／unit／trust／availability／reason）；" +
            "敏感值依政策遮蔽（預設 Redact）、UTF-8 無 BOM、零網路、路徑由使用者指定。" +
            "界線：JSONL 是「資料流」不是報告——沒有排版與註解，適合程式後處理。",
            "", "JsonlExportService.Serialize／WriteAsync", FactTrustLevel.Derived, false, at);
}

/// <summary>
/// QS-007：查詢結果快取。失效條件是「事實指紋」，不是時間——指紋不同就重算，
/// 避免「時間還沒到、資料已經變了」的殘留。
/// </summary>
public sealed class QueryResultCache
{
    private readonly Dictionary<string, (string Fingerprint, IReadOnlyList<HardwareFact> Result)> _entries = new(StringComparer.Ordinal);

    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public int Invalidations { get; private set; }
    public int Entries => _entries.Count;

    /// <summary>取快取或建新結果；回傳的第二項＝是否來自快取。</summary>
    public (IReadOnlyList<HardwareFact> Result, bool FromCache) GetOrBuild(
        string query, string fingerprint, Func<IReadOnlyList<HardwareFact>> build)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("查詢不可為空", nameof(query));
        if (_entries.TryGetValue(query, out var hit) && string.Equals(hit.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            Hits++;
            return (hit.Result, true);
        }
        Misses++;
        var result = build();
        _entries[query] = (fingerprint, result);
        return (result, false);
    }

    /// <summary>全部失效（例如使用者按下重新整理）。</summary>
    public void Invalidate()
    {
        if (_entries.Count > 0) Invalidations++;
        _entries.Clear();
    }

    /// <summary>把快取狀態攤成事實；命中率不是效能保證，殘留一律 Unknown。</summary>
    public HardwareFact ToFact(DateTimeOffset at)
    {
        int total = Hits + Misses;
        string rate = total == 0 ? "（尚無查詢）" : $"{100.0 * Hits / total:F0}%";
        var f = new HardwareFact("qs.cache", "查詢與智慧層", "查詢結果快取",
            $"快取 {Entries} 筆查詢・命中 {Hits}／未命中 {Misses}（命中率 {rate}）・失效 {Invalidations} 次。" +
            "界線：失效條件是事實指紋（鍵＋值＋可用性）——指紋不同即重算，不做時間型快取；" +
            "命中只代表「輸入沒變」，不代表值仍新鮮（來源本身可能是快取殘留，那種情況各事實會自己標 Unknown）。",
            "筆", "QueryResultCache（記憶體；不落盤）", total == 0 ? FactTrustLevel.Unknown : FactTrustLevel.Derived,
            false, at, total, total == 0 ? FactAvailability.Unknown : FactAvailability.Present,
            total == 0 ? "尚未有任何查詢——沒有命中率可陳述（不是 0% 效能）" : null);
        return f;
    }
}
