using System.Text.Json;

namespace XinSpect;

/// <summary>
/// AI 診斷代理可呼叫的一個本機工具。全部工具皆為「唯讀查詢」：只把已在畫面上的真實讀值
/// 整理成文字回給模型，不會寫入硬體、不會改設定、不碰檔案系統以外的任何東西。
/// </summary>
public sealed class AiTool
{
    /// <summary>函式名稱（送給模型，需為英數與底線）。</summary>
    public required string Name { get; init; }
    /// <summary>給模型看的用途說明（繁中亦可，模型只需理解語意）。</summary>
    public required string Description { get; init; }
    /// <summary>參數的 JSON Schema；無參數者維持預設的空物件。</summary>
    public string ParametersJson { get; init; } = """{"type":"object","properties":{}}""";
    /// <summary>實際執行：輸入模型給的參數 JSON（可能為空字串），輸出要回給模型的文字。</summary>
    public required Func<string, string> Run { get; init; }
}

/// <summary>
/// 工具箱：<see cref="AiService"/> 據此組出 OpenAI 相容的 <c>tools</c> 陣列，並在模型要求時執行。
/// 由 <c>AiToolboxBuilder</c> 依主檢視模型建立，使工具永遠讀到與畫面同一份即時資料。
/// </summary>
public sealed class AiToolbox
{
    private readonly List<AiTool> _tools = [];

    /// <summary>已註冊的工具（順序即送給模型的順序）。</summary>
    public IReadOnlyList<AiTool> Tools => _tools;

    public bool HasTools => _tools.Count > 0;

    /// <summary>註冊一個工具；同名者以後者取代（重建工具箱時不會重複）。</summary>
    public void Add(string name, string description, Func<string, string> run, string? parametersJson = null)
    {
        var tool = new AiTool
        {
            Name = name,
            Description = description,
            Run = run,
            ParametersJson = string.IsNullOrWhiteSpace(parametersJson)
                ? """{"type":"object","properties":{}}"""
                : parametersJson,
        };
        int at = _tools.FindIndex(t => t.Name == name);
        if (at >= 0) _tools[at] = tool;
        else _tools.Add(tool);
    }

    /// <summary>組出 OpenAI 相容的 <c>tools</c> 陣列（供序列化）。</summary>
    public List<object> ToSchema()
    {
        var list = new List<object>(_tools.Count);
        foreach (var t in _tools)
        {
            JsonElement schema;
            try
            {
                using var doc = JsonDocument.Parse(t.ParametersJson);
                schema = doc.RootElement.Clone();
            }
            catch
            {
                // 手寫 schema 打錯字時退回「無參數」，不讓整場對話因此失敗
                using var doc = JsonDocument.Parse("""{"type":"object","properties":{}}""");
                schema = doc.RootElement.Clone();
            }
            list.Add(new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = schema },
            });
        }
        return list;
    }

    /// <summary>
    /// 執行模型要求的工具。找不到工具或執行途中出錯都回傳說明文字而非拋出——
    /// 模型看得懂錯誤訊息，能改用別的工具或如實告知使用者。
    /// </summary>
    public string Invoke(string name, string argsJson)
    {
        var tool = _tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (tool is null)
            return $"（沒有名為 {name} 的工具；可用工具：{string.Join("、", _tools.Select(t => t.Name))}）";

        try
        {
            string text = tool.Run(argsJson ?? "") ?? "";
            return text.Length == 0 ? "（此項目目前沒有可用讀值）" : text;
        }
        catch (Exception ex)
        {
            return $"（工具 {name} 執行失敗：{ex.Message}）";
        }
    }

    /// <summary>取參數 JSON 中的整數（缺少或格式不符時回傳預設值並夾在範圍內）。</summary>
    public static int IntArg(string argsJson, string key, int fallback, int min, int max)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(argsJson))
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(key, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d))
                        return (int)Math.Clamp(Math.Round(d), min, max);
                    if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out double s))
                        return (int)Math.Clamp(Math.Round(s), min, max);
                }
            }
        }
        catch { /* 模型偶爾送出非 JSON 或型別不符，一律退回預設 */ }
        return Math.Clamp(fallback, min, max);
    }

    /// <summary>取參數 JSON 中的字串（缺少、空白或格式不符時回傳 null）。</summary>
    public static string? StringArg(string argsJson, string key)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(argsJson))
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(key, out var v))
                {
                    string? s = v.ValueKind switch
                    {
                        JsonValueKind.String => v.GetString(),
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
            }
        }
        catch { /* 同上：格式不符即視為未提供 */ }
        return null;
    }
}
