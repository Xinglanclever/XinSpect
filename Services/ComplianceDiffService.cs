using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 比較兩份 <c>--compliance</c> JSON 的控制項／ATT&amp;CK 判定變化。
/// 純函式：只讀 JSON，不重新收集事實、不重新判定、不連網。
/// </summary>
/// <remarks>
/// <para>
/// <b>「變成 Unverifiable」是一種退步。</b>它可能代表證據消失、權限改變或來源讀不到；
/// 把 Unverifiable 當成「沒事」會讓基準比較失去意義，所以 <see cref="ComplianceChangeKind.Regressed"/>
/// 涵蓋 Satisfied→Unverifiable，而不是只涵蓋明確的 NotSatisfied。
/// </para>
/// <para>
/// <b>方向的定義很窄：</b>只有「變成 Satisfied／Mitigated」叫改善，
/// 只有「從 Satisfied／Mitigated 變成其他」叫退步；其餘判定變動一律叫 Changed，
/// 不在這裡做風險評分。排序與計數都不是安全保證。
/// </para>
/// </remarks>
public static class ComplianceDiffService
{
    public const int SchemaVersion = 1;

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>比較兩份 JSON 字串；格式不認得時丟 <see cref="FormatException"/>，不產生半份結果。</summary>
    public static ComplianceDiff Compare(string beforeJson, string afterJson)
    {
        ArgumentNullException.ThrowIfNull(beforeJson);
        ArgumentNullException.ThrowIfNull(afterJson);

        var before = Parse(beforeJson, "舊");
        var after = Parse(afterJson, "新");

        var controls = CompareMap(before.Controls, after.Controls,
            (o, n) => o == n ? ComplianceChangeKind.Same : ControlKind(o, n));
        var techniques = CompareMap(before.Techniques, after.Techniques,
            (o, n) => o == n ? ComplianceChangeKind.Same : TechniqueKind(o, n));

        return new ComplianceDiff(controls, techniques,
            before.Controls.Count, after.Controls.Count,
            before.Techniques.Count, after.Techniques.Count);
    }

    private static IReadOnlyList<ComplianceItemChange> CompareMap(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after,
        Func<string, string, ComplianceChangeKind> classify)
    {
        var changes = new List<ComplianceItemChange>();
        foreach (var (id, oldValue) in before)
        {
            if (!after.TryGetValue(id, out string? newValue))
                changes.Add(new ComplianceItemChange(id, ComplianceChangeKind.Removed, oldValue, null));
            else if (oldValue != newValue)
                changes.Add(new ComplianceItemChange(id, classify(oldValue, newValue), oldValue, newValue));
        }
        foreach (var (id, newValue) in after)
        {
            if (!before.ContainsKey(id))
                changes.Add(new ComplianceItemChange(id, ComplianceChangeKind.Added, null, newValue));
        }
        return changes;
    }

    private static ComplianceChangeKind ControlKind(string oldValue, string newValue)
    {
        if (newValue == nameof(ComplianceStatus.Satisfied)) return ComplianceChangeKind.Improved;
        if (oldValue == nameof(ComplianceStatus.Satisfied)) return ComplianceChangeKind.Regressed;
        return ComplianceChangeKind.Changed;
    }

    private static ComplianceChangeKind TechniqueKind(string oldValue, string newValue)
    {
        if (newValue == nameof(AttackExposure.Mitigated)) return ComplianceChangeKind.Improved;
        if (oldValue == nameof(AttackExposure.Mitigated)) return ComplianceChangeKind.Regressed;
        return ComplianceChangeKind.Changed;
    }

    private static ParsedReport Parse(string json, string role)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, ParseOptions); }
        catch (JsonException ex)
        {
            throw new FormatException($"{role}檔不是合法 JSON：{ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException($"{role}檔根元素不是物件。");
            if (!root.TryGetProperty("scope", out var scope)
                || scope.ValueKind != JsonValueKind.String
                || scope.GetString() != "compliance")
                throw new FormatException($"{role}檔不是 --compliance 的輸出（scope 不是 compliance）。");
            if (!root.TryGetProperty("controls", out var controlsEl)
                || controlsEl.ValueKind != JsonValueKind.Array)
                throw new FormatException($"{role}檔缺少 controls 陣列。");

            var controls = ReadMap(controlsEl, "status", role, "控制項");
            var techniques = root.TryGetProperty("attack", out var attackEl)
                && attackEl.ValueKind == JsonValueKind.Array
                ? ReadMap(attackEl, "exposure", role, "ATT&CK")
                : new Dictionary<string, string>(StringComparer.Ordinal);
            return new ParsedReport(controls, techniques);
        }
    }

    private static Dictionary<string, string> ReadMap(JsonElement array, string valueName, string role, string label)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var idEl)
                || idEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idEl.GetString()))
                throw new FormatException($"{role}檔的{label}項目缺少 id。");
            if (!item.TryGetProperty(valueName, out var valueEl)
                || valueEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(valueEl.GetString()))
                throw new FormatException($"{role}檔的{label}項目「{idEl.GetString()}」缺少 {valueName}。");

            string id = idEl.GetString()!;
            if (!map.TryAdd(id, valueEl.GetString()!))
                throw new FormatException($"{role}檔的{label} id「{id}」重複。");
        }
        return map;
    }

    private sealed record ParsedReport(
        IReadOnlyDictionary<string, string> Controls,
        IReadOnlyDictionary<string, string> Techniques);
}

public enum ComplianceChangeKind
{
    Added,
    Removed,
    Improved,
    Regressed,
    Changed,
    Same,
}

public sealed record ComplianceItemChange(
    string Id, ComplianceChangeKind Kind, string? Before, string? After);

public sealed record ComplianceDiff(
    IReadOnlyList<ComplianceItemChange> Controls,
    IReadOnlyList<ComplianceItemChange> Techniques,
    int ControlsBefore,
    int ControlsAfter,
    int TechniquesBefore,
    int TechniquesAfter);