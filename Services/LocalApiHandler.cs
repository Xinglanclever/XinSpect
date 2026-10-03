using System.Text.Json;
using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>
/// WP34 本機 API 的請求處理（**純函式**；HTTP 殼層極薄不在單測範圍）：
/// <list type="bullet">
/// <item>GET /api/facts——全部事實（canonical JSON，與時間膠囊同一形狀）。</item>
/// <item>POST /api/query——查詢語言全文（WP35）；值不含 <c>=</c> 時相容舊的 key 前綴語意。</item>
/// </list>
/// 只綁 loopback、唯讀、只回匿名機器識別；沒有寫入端點。查詢語法錯誤＝400 帶修正指引——
/// 「查錯」不能偽裝成「沒有結果」（誠實契約）。
/// </summary>
public static class LocalApiHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static (int Status, string Json) Handle(string method, string path, string? body,
        Func<HardwareSnapshot?> currentSnapshot)
    {
        switch (path)
        {
            case "/api/facts" when method == "GET":
            {
                var snapshot = currentSnapshot();
                return snapshot is null
                    ? (503, Error("no-snapshot", "尚未擷取任何事實——先執行擷取動作"))
                    : (200, JsonSerializer.Serialize(snapshot, JsonOptions));
            }
            case "/api/query" when method == "POST":
            {
                var snapshot = currentSnapshot();
                if (snapshot is null) return (503, Error("no-snapshot", "尚未擷取任何事實——先執行擷取動作"));
                string text = (body ?? "").Trim();
                // 舊前綴語意相容：單一 token 且含點（如「spi.」）才當前綴；其餘一律走查詢語言——
                // 「nonsense」這種無運算子的輸入要回 400，不能偽裝成前綴查詢回 200 空結果。
                bool legacyPrefix = text.Contains('.') && !text.Contains(' ') && !text.Contains('\n') && !text.Contains('=');
                try
                {
                    if (!legacyPrefix)
                    {
                        var query = QueryParser.Parse(text);
                        var result = QueryExecutor.RunOnSnapshot(query, snapshot);
                        return (200, JsonSerializer.Serialize(result, JsonOptions));
                    }
                    var facts = snapshot.Facts.Where(f => f.Key.StartsWith(text, StringComparison.Ordinal)).ToArray();
                    return (200, JsonSerializer.Serialize(new { Matched = facts.Length, Facts = facts }, JsonOptions));
                }
                catch (QueryParseException ex)
                {
                    return (400, Error("ParseException", ex.Message));
                }
            }
            default:
                return (404, Error("not-found", "支援的端點：GET /api/facts、POST /api/query"));
        }
    }

    private static string Error(string code, string message) =>
        JsonSerializer.Serialize(new { Error = code, Message = message }, JsonOptions);
}
