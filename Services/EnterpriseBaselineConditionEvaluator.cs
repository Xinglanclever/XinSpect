namespace XinSpect;

/// <summary>Pure condition results; this type carries no signature or endpoint-attestation claim.</summary>
internal sealed record EnterpriseBaselineConditions(
    int SchemaVersion, string Semantics, int Satisfied, int NotSatisfied, int Unverifiable,
    IReadOnlyList<EnterpriseBaselineResult> Results);

/// <summary>
/// One comparator for signed enterprise packages and the device-compliance adapter.
/// Exact is ordinal display-text equality, including language and formatting. Version 2 binds
/// both predicates to an explicit, ordinal unit string (empty means explicitly unitless).
/// </summary>
internal static class EnterpriseBaselineConditionEvaluator
{
    internal static string Semantics(int schemaVersion) => schemaVersion switch
    {
        EnterpriseBaselinePackageService.LegacySchemaVersion =>
            "v1 legacy: Exact 為顯示文字逐字比對，NumericRange 為包含上下界；未綁定單位。所有版本均拒絕 Unknown／Speculative 信任、缺來源及合規輸出作為證據。",
        EnterpriseBaselinePackageService.CurrentSchemaVersion =>
            "v2: Exact 為顯示文字逐字比對（含語言、大小寫及格式），NumericRange 僅用有限 NumericValue 且包含上下界；兩者均要求單位逐字相同，不換算單位。",
        _ => throw new ArgumentException("政策 schema 版本不受支援。"),
    };

    internal static void ValidatePolicy(EnterpriseBaselinePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _ = Semantics(policy.SchemaVersion);
        bool version2 = policy.SchemaVersion == EnterpriseBaselinePackageService.CurrentSchemaVersion;
        if (string.IsNullOrWhiteSpace(policy.PolicyId) || policy.PolicyId.Length > 128)
            throw new ArgumentException("政策識別碼必須為 1 至 128 字元。");
        if (policy.CreatedAtUtc.Offset != TimeSpan.Zero || policy.ExpiresAtUtc.Offset != TimeSpan.Zero
            || policy.ExpiresAtUtc <= policy.CreatedAtUtc)
            throw new ArgumentException("政策時間必須為 UTC，且到期時間晚於建立時間。");
        if (policy.Rules is null || policy.Rules.Count is < 1 or > EnterpriseBaselinePackageService.MaxRules
            || policy.Rules.Any(rule => rule is null))
            throw new ArgumentException("政策須包含 1 至 500 條非 null 規則。");

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in policy.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Key) || rule.Key.Length > 160
                || !System.Text.RegularExpressions.Regex.IsMatch(rule.Key,
                    @"\A[a-z0-9][a-z0-9._-]*\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new ArgumentException("政策含無效事實鍵；不接受萬用字元或鍵別名。");
            if (version2)
            {
                if (string.IsNullOrWhiteSpace(rule.RequirementId) || rule.RequirementId.Length > 128
                    || rule.RequirementId.Any(char.IsControl) || !identities.Add(rule.RequirementId))
                    throw new ArgumentException("v2 每條規則須有唯一的 1 至 128 字元 requirementId。");
                if (rule.Unit is null || rule.Unit.Length > 64 || rule.Unit.Any(char.IsControl))
                    throw new ArgumentException("v2 每條規則均須明確指定 unit；空字串表示無單位，不做裁切或換算。");
            }
            else if (rule.RequirementId is not null || rule.Unit is not null || !identities.Add(rule.Key))
            {
                throw new ArgumentException("v1 規則不得重複指定事實鍵，亦不得夾帶 v2 的 unit 或 requirementId。");
            }

            bool valid = rule.Match switch
            {
                EnterpriseBaselineMatch.Exact => rule.ExpectedValue is { Length: <= 4096 }
                    && (!version2 || (rule.Minimum is null && rule.Maximum is null)),
                EnterpriseBaselineMatch.NumericRange => rule.Minimum is { } min && rule.Maximum is { } max
                    && double.IsFinite(min) && double.IsFinite(max) && min <= max && rule.ExpectedValue is null,
                _ => false,
            };
            if (!valid)
                throw new ArgumentException("政策規則格式無效；v2 不接受混用的比較運算元。");
        }
    }

    internal static EnterpriseBaselineConditions Evaluate(
        EnterpriseBaselinePolicy policy, IReadOnlyDictionary<string, HardwareFact> facts)
    {
        ValidatePolicy(policy);
        ArgumentNullException.ThrowIfNull(facts);
        // Do not inherit a caller's case-insensitive/aliasing dictionary comparer.
        var exactFacts = facts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var results = policy.Rules.Select(rule => EvaluateRule(policy.SchemaVersion, rule, exactFacts)).ToArray();
        return new(policy.SchemaVersion, Semantics(policy.SchemaVersion),
            results.Count(r => r.Status == EnterpriseBaselineStatus.Satisfied),
            results.Count(r => r.Status == EnterpriseBaselineStatus.NotSatisfied),
            results.Count(r => r.Status == EnterpriseBaselineStatus.Unverifiable), results);
    }

    private static EnterpriseBaselineResult EvaluateRule(int schemaVersion,
        EnterpriseBaselineRule rule, IReadOnlyDictionary<string, HardwareFact> facts)
    {
        if (!facts.TryGetValue(rule.Key, out var fact) || fact is null)
            return Result(rule, EnterpriseBaselineStatus.Unverifiable,
                "本次沒有唯一且逐字相同的事實鍵；缺資料不算符合。", null);
        if (!string.Equals(fact.Key, rule.Key, StringComparison.Ordinal)
            || fact.Value is null || HardwareSnapshotService.IsRedacted(fact.Value))
            return Result(rule, EnterpriseBaselineStatus.Unverifiable,
                "事實鍵不符或值已遮蔽；無法對照原始證據。", fact);
        if (fact.Key.StartsWith("infer.", StringComparison.Ordinal) || fact.Trust == FactTrustLevel.Speculative)
            return Result(rule, EnterpriseBaselineStatus.Unverifiable,
                "推測事實（infer.* 或 Speculative）不得作為合規證據；Present 也不代表量測或驗證。", fact);
        if (fact.Key.StartsWith("zr.compliance.", StringComparison.Ordinal))
            return Result(rule, EnterpriseBaselineStatus.Unverifiable,
                "合規輸出不得回灌為本輪的合規輸入證據。", fact);
        if (fact.Availability != FactAvailability.Present)
            return Result(rule, EnterpriseBaselineStatus.Unverifiable,
                $"事實狀態為 {fact.Availability}；原始原因留在本機快照，不複製到報告。缺資料不算符合。", fact);
        if (fact.Trust is not (FactTrustLevel.Reported or FactTrustLevel.Derived or FactTrustLevel.Measured)
            || string.IsNullOrWhiteSpace(fact.Source))
            return Result(rule, EnterpriseBaselineStatus.Unverifiable,
                "事實信任等級未知或來源缺失；Present 本身不構成可用合規證據。", fact);
        if (schemaVersion == EnterpriseBaselinePackageService.CurrentSchemaVersion
            && !string.Equals(fact.Unit, rule.Unit, StringComparison.Ordinal))
            return Result(rule, EnterpriseBaselineStatus.Unverifiable,
                "事實單位與政策單位不完全相同；不從文字推測或換算單位。", fact);
        if (rule.Match == EnterpriseBaselineMatch.NumericRange
            && (fact.NumericValue is not { } numeric || !double.IsFinite(numeric)))
            return Result(rule, EnterpriseBaselineStatus.Unverifiable,
                "事實沒有有效數值；不從顯示文字猜數字。", fact);

        bool matches = rule.Match == EnterpriseBaselineMatch.Exact
            ? string.Equals(fact.Value, rule.ExpectedValue, StringComparison.Ordinal)
            : fact.NumericValue >= rule.Minimum && fact.NumericValue <= rule.Maximum;
        return Result(rule, matches ? EnterpriseBaselineStatus.Satisfied : EnterpriseBaselineStatus.NotSatisfied,
            matches ? "符合組織自訂基準。這不是整體安全背書。" : "不符合組織自訂基準；這不是故障或安全判決。", fact);
    }

    private static EnterpriseBaselineResult Result(EnterpriseBaselineRule rule,
        EnterpriseBaselineStatus status, string reason, HardwareFact? fact)
        => new(rule.Key, status, fact?.Availability.ToString(), fact?.Source,
            fact?.Trust.ToString(), fact?.MeasuredAtUtc, reason, rule.RequirementId, fact?.Unit);
}
