using System.IO;
using System.Text.Json.Nodes;

namespace XinSpect;

/// <summary>
/// 外部規則引擎（V7 WP28／A45）：對載入的 <see cref="RuleDefinition"/> 套用<b>與內建引擎相同</b>的
/// 守衛語意（宣告的 Inputs 缺失／非 Present → Unverifiable 帶原因），再走分支表直譯。
/// 宣告界線：等價＝Relation 層逐條逐情境與 FactRelationRules 一致；本類不改內建規則的任何判決。
/// </summary>
public static class ExternalRuleEngine
{
    public static IReadOnlyList<FactRelationRow> Evaluate(
        IReadOnlyList<RuleDefinition> rules, IReadOnlyList<HardwareFact> facts)
    {
        var byKey = facts.GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var rows = new List<FactRelationRow>(rules.Count);
        foreach (var rule in rules)
        {
            var outcome = EvaluateRule(rule, byKey);
            rows.Add(new FactRelationRow(rule.Id, rule.Name, outcome.Relation, outcome.Reason, rule.Explanation));
        }
        return rows;
    }

    public static FactRelationOutcome EvaluateRule(RuleDefinition rule, IReadOnlyDictionary<string, HardwareFact> byKey)
    {
        var missing = rule.Inputs.Where(k => !byKey.ContainsKey(k)).ToList();
        if (missing.Count > 0)
            return FactRelationOutcome.Unverifiable($"缺少輸入事實：{string.Join("、", missing)}——無法對帳，不下判決");

        var notPresent = rule.Inputs
            .Select(k => byKey[k])
            .Where(f => f.Availability != FactAvailability.Present)
            .ToList();
        if (notPresent.Count > 0)
            return FactRelationOutcome.Unverifiable(
                $"輸入事實讀不到：{string.Join("、", notPresent.Select(f => f.Key))}（{notPresent[0].UnavailableReason ?? "原因不明"}）——無法對帳，不下判決");

        return RuleInterpreter.Evaluate(rule, byKey);
    }
}

/// <summary>從 JSON 載入外部規則（本機檔案；TASK-GAP-6 §A45 明列不下載遠端規則）。</summary>
public static class RuleLoader
{
    public static IReadOnlyList<RuleDefinition> LoadFromFile(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"規則檔 {path} 解析為空");
        var rulesNode = node["rules"] ?? throw new InvalidOperationException($"規則檔 {path} 缺少 rules 陣列");
        var rules = new List<RuleDefinition>();
        foreach (var r in rulesNode.AsArray())
            rules.Add(ParseRule(r ?? throw new InvalidOperationException("rules 內含 null 元素")));
        return rules;
    }

    public static IReadOnlyList<RuleDefinition> ParseRulesJson(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("規則 JSON 解析為空");
        var rulesNode = node["rules"] ?? throw new InvalidOperationException("規則 JSON 缺少 rules 陣列");
        var rules = new List<RuleDefinition>();
        foreach (var r in rulesNode.AsArray())
            rules.Add(ParseRule(r ?? throw new InvalidOperationException("rules 內含 null 元素")));
        return rules;
    }

    private static RuleDefinition ParseRule(JsonNode r)
    {
        string id = Require(r, "id");
        string name = Require(r, "name");
        string explanation = Require(r, "explanation");
        var inputs = StringArray(r, "inputs");
        var tryLookup = r["tryLookup"]?.AsArray().Select(n => (string?)n?.GetValue<string>()).Where(s => s is not null).Select(s => s!).ToList() ?? [];
        var specRefs = StringArray(r, "specRefs");
        var falseReports = StringArray(r, "falseReports");
        var branches = new List<RuleBranch>();
        foreach (var b in r["branches"]?.AsArray() ?? throw new InvalidOperationException($"規則 {id} 缺少 branches"))
        {
            var branchNode = b ?? throw new InvalidOperationException($"規則 {id} 的分支為 null");
            var when = new List<RulePredicate>();
            foreach (var p in branchNode["when"]?.AsArray() ?? new JsonArray())
                when.Add(ParsePredicate(p ?? throw new InvalidOperationException($"規則 {id} 的述詞為 null"), id));
            string then = (string?)branchNode["then"]?.GetValue<string>() ?? "";
            string reason = (string?)branchNode["reason"]?.GetValue<string>() ?? "";
            var relation = then switch
            {
                "Consistent" => FactRelation.Consistent,
                "Contradicts" => FactRelation.Contradicts,
                "Unverifiable" => FactRelation.Unverifiable,
                _ => throw new InvalidOperationException($"規則 {id} 的 then 值不合法：{then}"),
            };
            branches.Add(new RuleBranch(when, relation, reason));
        }
        return new RuleDefinition(id, name, inputs, tryLookup, branches, explanation, specRefs, falseReports);
    }

    private static RulePredicate ParsePredicate(JsonNode p, string ruleId)
    {
        string kind = (string?)p["kind"]?.GetValue<string>() ?? "";
        string? key = (string?)p["key"]?.GetValue<string>();
        string? otherKey = (string?)p["otherKey"]?.GetValue<string>();
        string? text = (string?)p["text"]?.GetValue<string>();
        double? min = p["min"]?.GetValue<double>();
        double? max = p["max"]?.GetValue<double>();
        return kind switch
        {
            "keyMissing" => RulePredicate.KeyMissing(key!),
            "keyAvailable" => RulePredicate.KeyAvailable(key!),
            "keyUnavailable" => RulePredicate.KeyUnavailable(key!),
            "valueEquals" => RulePredicate.ValueEquals(key!, text!),
            "valueEqualsKey" => RulePredicate.ValueEqualsKey(key!, otherKey!),
            "valueStartsWith" => RulePredicate.ValueStartsWith(key!, text!),
            "textContains" => RulePredicate.TextContains(key!, text!),
            "reasonContains" => RulePredicate.ReasonContains(key!, text!),
            "numericPresent" => RulePredicate.NumericPresent(key!),
            "numericEqualsKey" => RulePredicate.NumericEqualsKey(key!, otherKey!),
            "numericBetween" => RulePredicate.NumericBetween(key!, min ?? double.MinValue, max ?? double.MaxValue),
            "not" => RulePredicate.Not(ParsePredicate(p["child"] ?? throw new InvalidOperationException($"規則 {ruleId} 的 not 缺 child"), ruleId)),
            "anyOf" => RulePredicate.AnyOf((p["children"] ?? throw new InvalidOperationException($"規則 {ruleId} 的 anyOf 缺 children")).AsArray()
                .Select(c => ParsePredicate(c ?? throw new InvalidOperationException($"規則 {ruleId} 的 anyOf 含 null"), ruleId)).ToArray()),
            _ => throw new InvalidOperationException($"規則 {ruleId} 有未知述詞 kind：{kind}"),
        };
    }

    private static IReadOnlyList<string> StringArray(JsonNode r, string field) =>
        r[field]?.AsArray().Select(n => (string?)n?.GetValue<string>()).Where(s => s is not null).Select(s => s!).ToList() ?? [];

    private static string Require(JsonNode r, string field) =>
        (string?)r[field]?.GetValue<string>() ?? throw new InvalidOperationException($"規則缺少欄位 {field}");
}
