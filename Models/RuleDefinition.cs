using System.Text.Json.Nodes;

namespace XinSpect;

/// <summary>
/// 外部化對帳規則的資料模型（V7 WP28／A45 第一層）：規則從程式碼搬進資料，
/// 判決邏輯以<b>宣告式條件樹＋分支表</b>表達。設計界線（TASK-GAP-6 §A45）：
/// 等價定義＝<b>Relation 層</b>逐條逐情境與內建 C# 規則一致（Reason 允許語義等價、不逐字相同）；
/// 不碰 FactRelationEngine 公開介面；本機載入，不下載遠端規則。
/// </summary>
/// <param name="TryLookupKeys">未列入 Inputs 但條件會探測的鍵（引擎不對它們做 Present 守衛——缺席正是判決條件之一）。</param>
public sealed record RuleDefinition(
    string Id,
    string Name,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> TryLookupKeys,
    IReadOnlyList<RuleBranch> Branches,
    string Explanation,
    IReadOnlyList<string> SpecRefs,
    IReadOnlyList<string> FalseReports)
{
    public IEnumerable<string> AllKeys => Inputs.Concat(TryLookupKeys);
}

/// <summary>一個分支：when 全部成立（空 when＝預設分支，只准最後一個）→ then 判決＋reason 樣板。</summary>
public sealed record RuleBranch(IReadOnlyList<RulePredicate> When, FactRelation Then, string Reason);

/// <summary>
/// 條件述詞（kind 判別的聯集，JSON 友善）。鍵一律指 InputKeys∪TryLookupKeys；
/// 值類述詞（valueEquals／valueStartsWith／textContains／numericPresent／numericBetween）
/// 只對 Present 的事實成立——讀不到就不成立，不冒充。
/// </summary>
public sealed record RulePredicate
{
    public required string Kind { get; init; }
    public string? Key { get; init; }
    public string? OtherKey { get; init; }
    public string? Text { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public RulePredicate? Child { get; init; }
    public IReadOnlyList<RulePredicate>? Children { get; init; }

    public static RulePredicate KeyMissing(string key) => new() { Kind = "keyMissing", Key = key };
    public static RulePredicate KeyAvailable(string key) => new() { Kind = "keyAvailable", Key = key };
    public static RulePredicate KeyUnavailable(string key) => new() { Kind = "keyUnavailable", Key = key };
    public static RulePredicate ValueEquals(string key, string text) => new() { Kind = "valueEquals", Key = key, Text = text };
    public static RulePredicate ValueEqualsKey(string key, string otherKey) => new() { Kind = "valueEqualsKey", Key = key, OtherKey = otherKey };
    public static RulePredicate ValueStartsWith(string key, string text) => new() { Kind = "valueStartsWith", Key = key, Text = text };
    public static RulePredicate TextContains(string key, string text) => new() { Kind = "textContains", Key = key, Text = text };
    public static RulePredicate ReasonContains(string key, string text) => new() { Kind = "reasonContains", Key = key, Text = text };
    public static RulePredicate NumericPresent(string key) => new() { Kind = "numericPresent", Key = key };
    public static RulePredicate NumericEqualsKey(string key, string otherKey) => new() { Kind = "numericEqualsKey", Key = key, OtherKey = otherKey };
    public static RulePredicate NumericBetween(string key, double min, double max) => new() { Kind = "numericBetween", Key = key, Min = min, Max = max };
    public static RulePredicate Not(RulePredicate child) => new() { Kind = "not", Child = child };
    public static RulePredicate AnyOf(params RulePredicate[] children) => new() { Kind = "anyOf", Children = children };
}

/// <summary>
/// 條件直譯器：對分支表逐支求值（首中即判），回引擎同款 Outcome。
/// 值類述詞只吃 Present 事實；模板 {key}＝值、{key:hex}＝16 位元 hex、{key:reason}＝三態原因。
/// </summary>
public static class RuleInterpreter
{
    public static FactRelationOutcome Evaluate(RuleDefinition rule, IReadOnlyDictionary<string, HardwareFact> byKey)
    {
        foreach (var branch in rule.Branches)
        {
            if (branch.When.All(p => Matches(p, byKey)))
                return new(branch.Then, Interpolate(branch.Reason, byKey));
        }
        return FactRelationOutcome.Unverifiable($"規則 {rule.Id} 無分支命中——分支表不完整，先查規則定義");
    }

    public static bool Matches(RulePredicate p, IReadOnlyDictionary<string, HardwareFact> byKey) => p.Kind switch
    {
        "keyMissing" => p.Key is { } k && !byKey.ContainsKey(k),
        "keyAvailable" => p.Key is { } k && byKey.TryGetValue(k, out var f1) && f1.Availability == FactAvailability.Present,
        "keyUnavailable" => p.Key is { } k && byKey.TryGetValue(k, out var f2) && f2.Availability != FactAvailability.Present,
        "valueEquals" => p.Key is { } k && byKey.TryGetValue(k, out var f3) && f3.Availability == FactAvailability.Present && f3.Value == p.Text,
        "valueEqualsKey" => p.Key is { } ka && p.OtherKey is { } kb
            && byKey.TryGetValue(ka, out var fa) && fa.Availability == FactAvailability.Present
            && byKey.TryGetValue(kb, out var fb) && fb.Availability == FactAvailability.Present
            && fa.Value == fb.Value,
        "valueStartsWith" => p.Key is { } k4 && p.Text is { } t4
            && byKey.TryGetValue(k4, out var f4) && f4.Availability == FactAvailability.Present
            && f4.Value.StartsWith(t4, StringComparison.Ordinal),
        "textContains" => p.Key is { } k5 && p.Text is { } t5
            && byKey.TryGetValue(k5, out var f5) && f5.Availability == FactAvailability.Present
            && f5.Value.Contains(t5, StringComparison.Ordinal),
        "reasonContains" => p.Key is { } k6 && p.Text is { } t6
            && byKey.TryGetValue(k6, out var f6) && (f6.UnavailableReason ?? "").Contains(t6, StringComparison.Ordinal),
        "numericPresent" => p.Key is { } k7 && byKey.TryGetValue(k7, out var f7)
            && f7.Availability == FactAvailability.Present && f7.NumericValue is not null,
        "numericEqualsKey" => p.Key is { } k8 && p.OtherKey is { } k9
            && byKey.TryGetValue(k8, out var fa2) && fa2.NumericValue is not null
            && byKey.TryGetValue(k9, out var fb2) && fb2.NumericValue is not null
            && fa2.NumericValue!.Value == fb2.NumericValue!.Value,
        "numericBetween" => p.Key is { } k10 && byKey.TryGetValue(k10, out var f10)
            && f10.Availability == FactAvailability.Present && f10.NumericValue is { } v
            && v >= (p.Min ?? double.MinValue) && v <= (p.Max ?? double.MaxValue),
        "not" => p.Child is { } child && !Matches(child, byKey),
        "anyOf" => p.Children is { } cs && cs.Any(c => Matches(c, byKey)),
        _ => false, // 未知述詞不成立——驗證器會擋，這裡保守不猜
    };

    public static string Interpolate(string template, IReadOnlyDictionary<string, HardwareFact> byKey)
    {
        var output = template;
        foreach (var (key, fact) in byKey)
        {
            output = output
                .Replace("{" + key + ":hex}", fact.NumericValue is { } n ? $"0x{(uint)n:X8}" : "—")
                .Replace("{" + key + ":reason}", fact.UnavailableReason ?? "")
                .Replace("{" + key + "}", fact.Value);
        }
        return output;
    }
}
