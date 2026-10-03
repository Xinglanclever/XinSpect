namespace XinSpect;

/// <summary>查詢運算子：等於／包含／前綴。</summary>
public enum QueryOperator { Equals, Contains, StartsWith }

/// <summary>一個查詢子句：欄位＋運算子＋值（值可含空格——一行一子句）。</summary>
public sealed record QueryClause(string Field, QueryOperator Op, string Value);

/// <summary>一個查詢＝子句管線（按順序套用，前一個的輸出是後一個的輸入）。</summary>
public sealed record FactQuery(IReadOnlyList<QueryClause> Clauses, string QueryText);

/// <summary>統一查詢事實形狀：HardwareFact 與快照事實都能轉成這個；跨機器時帶匿名機器識別。</summary>
public sealed record QueryFact(
    string Key,
    string Category,
    string Source,
    string Value,
    FactAvailability Availability,
    string? UnavailableReason,
    DateTimeOffset MeasuredAtUtc,
    string? MachineId = null,
    DateTimeOffset? CapturedAtUtc = null)
{
    public static QueryFact From(HardwareFact f, string? machineId = null, DateTimeOffset? capturedAt = null) =>
        new(f.Key, f.Category, f.Source, f.Value, f.Availability, f.UnavailableReason, f.MeasuredAtUtc, machineId, capturedAt);

    public static QueryFact From(HardwareSnapshotFact f, string machineId, DateTimeOffset capturedAt) =>
        new(f.Key, f.Category, f.Source, f.Value, f.Availability, f.UnavailableReason, f.MeasuredAtUtc, machineId, capturedAt);
}

/// <summary>
/// 查詢結果。<b>查不到 ≠ 沒有</b>：0 筆匹配可能是「沒有這個事實」或「鍵名不同」——
/// 摘要文字明說這個區別；存在但讀不到的事實（三態）是正常匹配項，原因隨附。
/// </summary>
public sealed record FactQueryResult(string QueryText, int Scanned, IReadOnlyList<QueryFact> Matches)
{
    private int UnavailableCount => Matches.Count(m => m.Availability != FactAvailability.Present);

    public string SummaryText => Matches.Count == 0
        ? $"查無符合的事實（0/{Scanned}）——可能沒有這個事實、欄位值不同，或它存在但讀不到（可用 availability= 查三態項）"
        : $"符合 {Matches.Count}/{Scanned} 筆" +
          (UnavailableCount > 0 ? $"（其中 {UnavailableCount} 筆讀不到——三態原因隨附，不是沒有這個事實）" : "");
}
