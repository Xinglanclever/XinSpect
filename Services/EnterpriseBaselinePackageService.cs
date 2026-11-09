using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace XinSpect;

public enum EnterpriseBaselineMatch
{
    Exact,
    NumericRange,
}

public enum EnterpriseBaselineStatus
{
    Satisfied,
    NotSatisfied,
    Unverifiable,
}

public sealed record EnterpriseBaselineRule(
    string Key,
    EnterpriseBaselineMatch Match,
    string? ExpectedValue = null,
    double? Minimum = null,
    double? Maximum = null);

public sealed record EnterpriseBaselinePolicy(
    int SchemaVersion,
    string PolicyId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<EnterpriseBaselineRule> Rules);

public sealed record EnterpriseBaselineResult(
    string Key,
    EnterpriseBaselineStatus Status,
    string? EvidenceAvailability,
    string? Source,
    string? Trust,
    DateTimeOffset? MeasuredAtUtc,
    string Reason);

public sealed record EnterpriseBaselineEvaluation(
    bool SignatureValid,
    bool Expired,
    string PolicyId,
    string SignerKeyId,
    int Satisfied,
    int NotSatisfied,
    int Unverifiable,
    IReadOnlyList<EnterpriseBaselineResult> Results,
    string? FailureReason);

/// <summary>
/// Offline enterprise policy packages. The caller must supply an out-of-band trusted public key;
/// the package never establishes trust in its own signer and this service performs no I/O.
/// </summary>
public static class EnterpriseBaselinePackageService
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxPackageBytes = 1_048_576;
    public const int MaxKeyPemBytes = 16_384;
    public const int MaxRules = 500;
    private const string Algorithm = "ECDSA-P256-SHA256";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private sealed record Envelope(int SchemaVersion, string Algorithm, string SignerKeyId,
        string Payload, string Signature);

    public static string Sign(EnterpriseBaselinePolicy policy, string encryptedPrivateKeyPem, string password)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedPrivateKeyPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (Encoding.UTF8.GetByteCount(encryptedPrivateKeyPem) > MaxKeyPemBytes)
            throw new ArgumentException("簽署私鑰 PEM 超過安全大小上限。", nameof(encryptedPrivateKeyPem));
        ValidatePolicy(policy);

        byte[] payload = SerializeCanonical(policy);
        using ECDsa signer = ECDsa.Create();
        signer.ImportFromEncryptedPem(encryptedPrivateKeyPem, password);
        if (signer.KeySize != 256)
            throw new ArgumentException("企業基準簽章只接受 ECDSA P-256 金鑰。", nameof(encryptedPrivateKeyPem));

        byte[] signature = signer.SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var envelope = new Envelope(CurrentSchemaVersion, Algorithm, KeyId(signer),
            Convert.ToBase64String(payload), Convert.ToBase64String(signature));
        string json = JsonSerializer.Serialize(envelope, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaxPackageBytes)
            throw new ArgumentException("簽章基準套件超過大小上限。", nameof(policy));
        return json;
    }

    public static string Sign(string policyJson, string encryptedPrivateKeyPem, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyJson);
        if (Encoding.UTF8.GetByteCount(policyJson) > MaxPackageBytes)
            throw new ArgumentException("政策 JSON 超過安全大小上限。", nameof(policyJson));
        var policy = JsonSerializer.Deserialize<EnterpriseBaselinePolicy>(policyJson, JsonOptions)
            ?? throw new JsonException("政策內容為空。");
        return Sign(policy, encryptedPrivateKeyPem, password);
    }

    public static EnterpriseBaselineEvaluation Evaluate(
        string packageJson,
        string trustedPublicKeyPem,
        IReadOnlyDictionary<string, HardwareFact> facts,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (string.IsNullOrWhiteSpace(packageJson) || Encoding.UTF8.GetByteCount(packageJson) > MaxPackageBytes)
            return Failed("套件為空或超過大小上限。");
        if (string.IsNullOrWhiteSpace(trustedPublicKeyPem))
            return Failed("未提供組織預先信任的簽發公鑰。");
        if (Encoding.UTF8.GetByteCount(trustedPublicKeyPem) > MaxKeyPemBytes)
            return Failed("組織信任公鑰 PEM 超過安全大小上限。");

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(packageJson, JsonOptions)
                ?? throw new JsonException("套件內容為空。");
            if (envelope.SchemaVersion != CurrentSchemaVersion || envelope.Algorithm != Algorithm)
                return Failed("套件版本或簽章演算法不受支援。");

            byte[] payload = Convert.FromBase64String(envelope.Payload);
            byte[] signature = Convert.FromBase64String(envelope.Signature);
            using ECDsa verifier = ECDsa.Create();
            verifier.ImportFromPem(trustedPublicKeyPem);
            if (verifier.KeySize != 256 || !string.Equals(KeyId(verifier), envelope.SignerKeyId, StringComparison.Ordinal))
                return Failed("簽發者公鑰與組織信任金鑰不符。");
            if (!verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return Failed("簽章驗證失敗；套件可能遭竄改。");

            var policy = JsonSerializer.Deserialize<EnterpriseBaselinePolicy>(payload, JsonOptions)
                ?? throw new JsonException("簽署的政策內容為空。");
            ValidatePolicy(policy);
            if (policy.ExpiresAtUtc <= nowUtc)
                return new(true, true, policy.PolicyId, envelope.SignerKeyId, 0, 0, policy.Rules.Count, [],
                    "政策已過期；不評估、不宣告符合。");

            var results = policy.Rules.Select(rule => EvaluateRule(rule, facts)).ToArray();
            return new(true, false, policy.PolicyId, envelope.SignerKeyId,
                results.Count(r => r.Status == EnterpriseBaselineStatus.Satisfied),
                results.Count(r => r.Status == EnterpriseBaselineStatus.NotSatisfied),
                results.Count(r => r.Status == EnterpriseBaselineStatus.Unverifiable),
                results, null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException
                                   or ArgumentException or InvalidOperationException)
        {
            return Failed("套件無法驗證或格式無效（" + ex.GetType().Name + "）。");
        }
    }

    private static EnterpriseBaselineResult EvaluateRule(
        EnterpriseBaselineRule rule, IReadOnlyDictionary<string, HardwareFact> facts)
    {
        if (!facts.TryGetValue(rule.Key, out var fact) || fact is null)
            return Result(rule, EnterpriseBaselineStatus.Unverifiable,
                "本次沒有這把事實鍵；缺資料不算符合。", null);
        if (fact.Availability != FactAvailability.Present)
            return Result(rule, EnterpriseBaselineStatus.Unverifiable,
                $"事實狀態為 {fact.Availability}；原始原因留在本機快照，不複製到報告。缺資料不算符合。", fact);

        bool matches = rule.Match switch
        {
            EnterpriseBaselineMatch.Exact => string.Equals(fact.Value, rule.ExpectedValue, StringComparison.Ordinal),
            EnterpriseBaselineMatch.NumericRange => fact.NumericValue is { } value
                && double.IsFinite(value) && value >= rule.Minimum && value <= rule.Maximum,
            _ => false,
        };
        if (rule.Match == EnterpriseBaselineMatch.NumericRange)
        {
            if (fact.NumericValue is not { } numeric || !double.IsFinite(numeric))
                return Result(rule, EnterpriseBaselineStatus.Unverifiable,
                    "事實沒有有效數值；不從顯示文字猜數字。", fact);
        }

        return Result(rule,
            matches ? EnterpriseBaselineStatus.Satisfied : EnterpriseBaselineStatus.NotSatisfied,
            matches ? "符合組織自訂基準。這不是整體安全背書。" : "不符合組織自訂基準；這不是故障或安全判決。",
            fact);
    }

    private static EnterpriseBaselineResult Result(EnterpriseBaselineRule rule,
        EnterpriseBaselineStatus status, string reason, HardwareFact? fact)
        => new(rule.Key, status, fact?.Availability.ToString(), fact?.Source,
            fact?.Trust.ToString(), fact?.MeasuredAtUtc, reason);

    private static byte[] SerializeCanonical(EnterpriseBaselinePolicy policy)
    {
        var canonical = policy with
        {
            Rules = policy.Rules.OrderBy(r => r.Key, StringComparer.Ordinal).ToArray(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(canonical, JsonOptions);
    }

    private static void ValidatePolicy(EnterpriseBaselinePolicy policy)
    {
        if (policy.SchemaVersion != CurrentSchemaVersion)
            throw new ArgumentException("政策 schema 版本不受支援。");
        if (string.IsNullOrWhiteSpace(policy.PolicyId) || policy.PolicyId.Length > 128)
            throw new ArgumentException("政策識別碼必須為 1 至 128 字元。");
        if (policy.CreatedAtUtc.Offset != TimeSpan.Zero || policy.ExpiresAtUtc.Offset != TimeSpan.Zero
            || policy.ExpiresAtUtc <= policy.CreatedAtUtc)
            throw new ArgumentException("政策時間必須為 UTC，且到期時間晚於建立時間。");
        if (policy.Rules is null || policy.Rules.Count is < 1 or > MaxRules)
            throw new ArgumentException($"政策規則數必須介於 1 至 {MaxRules}。");
        if (policy.Rules.Any(rule => rule is null))
            throw new ArgumentException("政策規則不得包含 null 項目。");
        if (policy.Rules.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != policy.Rules.Count)
            throw new ArgumentException("政策不得重複指定事實鍵。");

        foreach (var rule in policy.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Key) || rule.Key.Length > 160
                || !Regex.IsMatch(rule.Key, @"\A[a-z0-9][a-z0-9._-]*\z", RegexOptions.CultureInvariant))
                throw new ArgumentException("政策含無效事實鍵。");
            switch (rule.Match)
            {
                case EnterpriseBaselineMatch.Exact when rule.ExpectedValue is { Length: <= 4096 }:
                    break;
                case EnterpriseBaselineMatch.NumericRange
                    when rule.Minimum is { } min && rule.Maximum is { } max
                         && double.IsFinite(min) && double.IsFinite(max) && min <= max
                         && rule.ExpectedValue is null:
                    break;
                default:
                    throw new ArgumentException($"政策規則格式無效：{rule.Key}。");
            }
        }
    }

    private static string KeyId(ECDsa key) =>
        Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    private static EnterpriseBaselineEvaluation Failed(string reason) =>
        new(false, false, "", "", 0, 0, 0, [], reason);
}
