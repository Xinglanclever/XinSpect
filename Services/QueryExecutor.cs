namespace XinSpect;

/// <summary>
/// 查詢執行器（V7 WP28／A44）：對事實集合或快照執行查詢管線。
/// 跨機器＝把多份快照各轉成 QueryFact（帶匿名機器識別與擷取時間）一起餵入；
/// 只讀不寫、不外推——匹配就是匹配，查無就是查無。
/// </summary>
public static class QueryExecutor
{
    public static FactQueryResult Run(FactQuery query, IEnumerable<QueryFact> facts)
    {
        var all = facts.ToList();
        IReadOnlyList<QueryFact> current = all;
        foreach (var clause in query.Clauses)
            current = Apply(current, clause);
        return new FactQueryResult(query.QueryText, all.Count, current);
    }

    public static FactQueryResult Run(FactQuery query, IEnumerable<HardwareFact> facts, string? machineId = null, DateTimeOffset? capturedAt = null) =>
        Run(query, facts.Select(f => QueryFact.From(f, machineId, capturedAt)));

    /// <summary>對單一快照查詢（時間軸查詢的基礎：快照帶擷取時間與匿名機器識別）。</summary>
    public static FactQueryResult RunOnSnapshot(FactQuery query, HardwareSnapshot snapshot) =>
        Run(query, snapshot.Facts.Select(f => QueryFact.From(f, snapshot.AnonymousMachineId, snapshot.CapturedAtUtc)));

    /// <summary>跨機器查詢：多份快照同時查（結果各帶匿名機器識別，可分辨是哪台的）。</summary>
    public static FactQueryResult RunOnSnapshots(FactQuery query, IEnumerable<HardwareSnapshot> snapshots) =>
        Run(query, snapshots.SelectMany(s => s.Facts.Select(f => QueryFact.From(f, s.AnonymousMachineId, s.CapturedAtUtc))));

    private static IReadOnlyList<QueryFact> Apply(IReadOnlyList<QueryFact> input, QueryClause clause) => clause.Field switch
    {
        "key" => Text(input, clause, f => f.Key),
        "category" => Text(input, clause, f => f.Category),
        "source" => Text(input, clause, f => f.Source),
        "value" => Text(input, clause, f => f.Value),
        "availability" => input.Where(f => f.Availability == ParseAvailability(clause.Value)).ToList(),
        "since" => input.Where(f => f.MeasuredAtUtc >= QueryParser.ParseDate(clause.Value)).ToList(),
        "until" => input.Where(f => f.MeasuredAtUtc <= QueryParser.ParseDate(clause.Value)).ToList(),
        _ => throw new QueryParseException($"未知欄位「{clause.Field}」——解析器應已擋下，此處為防禦"),
    };

    private static IReadOnlyList<QueryFact> Text(IReadOnlyList<QueryFact> input, QueryClause clause, Func<QueryFact, string> selector) =>
        clause.Op switch
        {
            QueryOperator.Equals => input.Where(f => string.Equals(selector(f), clause.Value, StringComparison.Ordinal)).ToList(),
            QueryOperator.Contains => input.Where(f => selector(f).Contains(clause.Value, StringComparison.Ordinal)).ToList(),
            QueryOperator.StartsWith => input.Where(f => selector(f).StartsWith(clause.Value, StringComparison.Ordinal)).ToList(),
            _ => throw new QueryParseException($"未知運算子——解析器應已擋下，此處為防禦"),
        };

    private static FactAvailability ParseAvailability(string value) =>
        QueryParser.ParseAvailability(value)
        ?? throw new QueryParseException($"availability 值「{value}」不合法——解析器應已擋下，此處為防禦");
}
