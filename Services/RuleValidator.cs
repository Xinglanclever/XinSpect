namespace XinSpect;

/// <summary>
/// 規則驗證器（V7 WP28／A45）：載入時把關——每條規則必須有 SpecRef（§12.11）、
/// 必須宣告誤報條件（A45 硬性要求 2）、分支表完整（預設分支只准最後一個）、
/// 述詞引用的鍵必須登記在 Inputs∪TryLookupKeys（抓打錯鍵）。回傳錯誤清單；空＝通過。
/// </summary>
public static class RuleValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<RuleDefinition> rules)
    {
        var errors = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            string at = $"規則 {rule.Id}";
            if (string.IsNullOrWhiteSpace(rule.Id)) { errors.Add("有規則的 Id 為空"); continue; }
            if (!seenIds.Add(rule.Id)) errors.Add($"{at}：Id 重複");
            if (string.IsNullOrWhiteSpace(rule.Name)) errors.Add($"{at}：Name 為空");
            if (rule.Inputs.Count == 0) errors.Add($"{at}：Inputs 不可為空（至少一個鍵）");
            if (string.IsNullOrWhiteSpace(rule.Explanation)) errors.Add($"{at}：Explanation 為空——規則要能解釋自己");
            if (rule.SpecRefs.Count == 0) errors.Add($"{at}：缺少 SpecRef（沒有規格出處＝未驗證）");
            if (rule.FalseReports.Count == 0) errors.Add($"{at}：未宣告誤報條件——答不出什麼情況會誤報的規則不合格");

            if (rule.Branches.Count == 0) { errors.Add($"{at}：分支表為空"); continue; }
            for (int i = 0; i < rule.Branches.Count; i++)
            {
                var b = rule.Branches[i];
                if (string.IsNullOrWhiteSpace(b.Reason)) errors.Add($"{at}：分支 {i} 的 Reason 為空");
                if (b.When.Count == 0 && i != rule.Branches.Count - 1)
                    errors.Add($"{at}：分支 {i} 是空 when 的預設分支，但不是最後一個分支");
            }

            var knownKeys = new HashSet<string>(rule.AllKeys, StringComparer.Ordinal);
            foreach (var b in rule.Branches)
            {
                foreach (var p in Enumerate(b.When))
                {
                    if (p.Key is { } k && !knownKeys.Contains(k))
                        errors.Add($"{at}：述詞引用未登記的鍵 {k}（打錯鍵的規則直接擋，不靜默通過）");
                    if (p.OtherKey is { } ok && !knownKeys.Contains(ok))
                        errors.Add($"{at}：述詞引用未登記的鍵 {ok}");
                    if (p.Child is { } nested) CheckChildren(at, nested, knownKeys, errors);
                    if (p.Children is { } cs) foreach (var nested2 in cs) CheckChildren(at, nested2, knownKeys, errors);
                }
            }
        }
        return errors;
    }

    private static void CheckChildren(string at, RulePredicate p, HashSet<string> knownKeys, List<string> errors)
    {
        if (p.Key is { } k && !knownKeys.Contains(k))
            errors.Add($"{at}：巢狀述詞引用未登記的鍵 {k}");
        if (p.Child is { } nested) CheckChildren(at, nested, knownKeys, errors);
        if (p.Children is { } cs) foreach (var nestedChild in cs) CheckChildren(at, nestedChild, knownKeys, errors);
    }

    private static IEnumerable<RulePredicate> Enumerate(IEnumerable<RulePredicate> preds)
    {
        foreach (var p in preds) yield return p;
    }
}

/// <summary>規則解釋器（V7 §12.9「規則要能解釋自己」）：為什麼觸發＋依據的規格＋什麼情況會誤報。</summary>
public static class RuleExplainer
{
    public static string Explain(RuleDefinition rule, FactRelationRow row) =>
        $"規則「{rule.Name}」（{rule.Id}）判定：{row.Relation}\n" +
        $"判定內容：{row.Reason}\n" +
        $"規則說明：{rule.Explanation}\n" +
        $"依據規格：{string.Join("；", rule.SpecRefs)}\n" +
        $"誤報條件（什麼情況下這條規則會誤報）：{string.Join("；", rule.FalseReports)}";
}
