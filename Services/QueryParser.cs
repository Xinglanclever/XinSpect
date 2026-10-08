using System.Globalization;

namespace XinSpect;

/// <summary>查詢語法錯誤——訊息要讓不懂程式的使用者知道怎麼改（列出合法欄位與範例）。</summary>
public sealed class QueryParseException(string message) : Exception(message);

/// <summary>
/// 查詢解析器（V7 WP28／A44）：語法五頁能講完——
/// <code>
///   key^=platform.            （鍵以前綴開頭）
///   category~=安全            （類別包含）
///   availability=read-error   （只要讀不到的）
///   since=2026-10-01          （量測時間起）
/// </code>
/// 一行一子句、按順序成管線；`#` 開頭是註解；值可含空格（吃到行尾）。
/// 絕不猜：未知欄位／運算子／壞日期直接丟 <see cref="QueryParseException"/>。
/// </summary>
public static class QueryParser
{
    public static readonly IReadOnlyList<string> ValidFields = ["key", "category", "source", "value", "availability", "since", "until"];

    public static FactQuery Parse(string queryText)
    {
        var clauses = new List<QueryClause>();
        var lines = queryText.Replace("\r\n", "\n").Split('\n');
        for (int lineNo = 0; lineNo < lines.Length; lineNo++)
        {
            var line = lines[lineNo].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            int opIndex = IndexOfOperator(line);
            if (opIndex < 0)
                throw new QueryParseException($"第 {lineNo + 1} 行缺少運算子（=、~= 或 ^=）：「{line}」。合法欄位：{string.Join("、", ValidFields)}。");
            var field = line[..opIndex].Trim().ToLowerInvariant();
            var (op, opText) = ParseOperator(line, opIndex);
            var value = line[(opIndex + opText.Length)..].Trim();
            if (value.Length == 0)
                throw new QueryParseException($"第 {lineNo + 1} 行的「{field}{opText}」沒有值。");

            ValidateField(field, op, value, lineNo + 1);
            clauses.Add(new QueryClause(field, op, value));
        }
        return new FactQuery(clauses, queryText);
    }

    private static int IndexOfOperator(string line)
    {
        int tilde = line.IndexOf("~=", StringComparison.Ordinal);
        int caret = line.IndexOf("^=", StringComparison.Ordinal);
        int eq = line.IndexOf('=');
        // ~^ 的第二字元也是 '='——取最先出現的運算子起點
        int start = new[] { tilde < 0 ? int.MaxValue : tilde, caret < 0 ? int.MaxValue : caret, eq < 0 ? int.MaxValue : eq }.Min();
        return start == int.MaxValue ? -1 : start;
    }

    private static (QueryOperator Op, string Text) ParseOperator(string line, int index)
    {
        if (line[index..].StartsWith("~=")) return (QueryOperator.Contains, "~=");
        if (line[index..].StartsWith("^=")) return (QueryOperator.StartsWith, "^=");
        return (QueryOperator.Equals, "=");
    }

    private static void ValidateField(string field, QueryOperator op, string value, int lineNo)
    {
        if (!ValidFields.Contains(field))
            throw new QueryParseException($"第 {lineNo + 1} 行的欄位「{field}」不合法。合法欄位：{string.Join("、", ValidFields)}。");
        if (field is "since" or "until")
        {
            if (op != QueryOperator.Equals)
                throw new QueryParseException($"第 {lineNo + 1} 行：{field} 只支援 =（時間範圍用 since/until 兩行夾出來）。");
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _))
                throw new QueryParseException($"第 {lineNo + 1} 行：{field} 的時間「{value}」無法解析（例：2026-10-01 或 2026-10-01T08:00:00Z）。");
            return;
        }
        if (field is "key" or "category" or "source" or "value")
        {
            if (value.Length > 300) throw new QueryParseException($"第 {lineNo + 1} 行：值超過 300 字——查詢不是全文檢索，先縮小範圍。");
            return;
        }
        // availability
        if (op != QueryOperator.Equals)
            throw new QueryParseException($"第 {lineNo + 1} 行：availability 只支援 =。");
        if (ParseAvailability(value) is null)
            throw new QueryParseException($"第 {lineNo + 1} 行：availability 值「{value}」不合法。" +
                "可用：present（可讀）、unconfirmed（有值但未確認）、read-error、"
                + "not-supported、insufficient-privilege、not-applicable。");
    }

    /// <summary>可用性值：列舉名不分大小寫＋常用別名。null＝不合法。</summary>
    public static FactAvailability? ParseAvailability(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "present" or "ok" or "可讀" => FactAvailability.Present,
            "read-error" or "error" => FactAvailability.ReadError,
            "not-supported" => FactAvailability.NotSupported,
            "insufficient-privilege" or "no-permission" => FactAvailability.InsufficientPrivilege,
            "not-applicable" => FactAvailability.NotApplicable,
            // 2.36 起：有值但未確認。別名刻意取 unconfirmed 而不是 "unknown"——
            // unknown 太容易與「不知道」混用，而這一態的語意是「拿到了東西但不敢背書」。
            "unconfirmed" => FactAvailability.Unknown,
            _ => Enum.TryParse<FactAvailability>(value, ignoreCase: true, out var parsed) ? parsed : null,
        };
    }

    public static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
