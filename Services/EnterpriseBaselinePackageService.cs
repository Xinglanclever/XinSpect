using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    double? Maximum = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequirementId = null);

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
    string Reason,
    string? RequirementId = null,
    string? EvidenceUnit = null);

public sealed record EnterpriseBaselineEvaluation(
    bool SignatureValid,
    bool Expired,
    string PolicyId,
    string SignerKeyId,
    int Satisfied,
    int NotSatisfied,
    int Unverifiable,
    IReadOnlyList<EnterpriseBaselineResult> Results,
    string? FailureReason,
    int? SchemaVersion = null,
    string? Semantics = null);

/// <summary>
/// Offline enterprise policy packages. The caller must supply an out-of-band trusted public key;
/// the package never establishes trust in its own signer and this service performs no I/O.
/// </summary>
public static class EnterpriseBaselinePackageService
{
    public const int LegacySchemaVersion = 1;
    public const int CurrentSchemaVersion = 2;
    public const int MaxPackageBytes = 1_048_576;
    public const int MaxKeyPemBytes = 16_384;
    public const int MaxRules = 500;
    private const string Algorithm = "ECDSA-P256-SHA256";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
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
        EnterpriseBaselineConditionEvaluator.ValidatePolicy(policy);

        byte[] payload = SerializeCanonical(policy);
        using ECDsa signer = ECDsa.Create();
        signer.ImportFromEncryptedPem(encryptedPrivateKeyPem, password);
        if (!IsP256(signer))
            throw new ArgumentException("企業基準簽章只接受 ECDSA P-256 金鑰。", nameof(encryptedPrivateKeyPem));

        byte[] signature = signer.SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var envelope = new Envelope(policy.SchemaVersion, Algorithm, KeyId(signer),
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
        var policy = DeserializePolicy(Encoding.UTF8.GetBytes(policyJson));
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
            if (envelope.SchemaVersion is not (LegacySchemaVersion or CurrentSchemaVersion) || envelope.Algorithm != Algorithm)
                return Failed("套件版本或簽章演算法不受支援。");

            byte[] payload = Convert.FromBase64String(envelope.Payload);
            byte[] signature = Convert.FromBase64String(envelope.Signature);
            using ECDsa verifier = ECDsa.Create();
            verifier.ImportFromPem(trustedPublicKeyPem);
            if (!IsP256(verifier) || !string.Equals(KeyId(verifier), envelope.SignerKeyId, StringComparison.Ordinal))
                return Failed("簽發者公鑰與組織信任金鑰不符。");
            if (!verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return Failed("簽章驗證失敗；套件可能遭竄改。");

            var policy = DeserializePolicy(payload);
            if (policy.SchemaVersion != envelope.SchemaVersion)
                return Failed("套件與簽署政策的 schema 版本不一致。");
            EnterpriseBaselineConditionEvaluator.ValidatePolicy(policy);
            if (policy.CreatedAtUtc > nowUtc)
                return new(true, false, policy.PolicyId, envelope.SignerKeyId, 0, 0, policy.Rules.Count, [],
                    "政策尚未生效；不評估、不宣告符合。", policy.SchemaVersion,
                    EnterpriseBaselineConditionEvaluator.Semantics(policy.SchemaVersion));
            if (policy.ExpiresAtUtc <= nowUtc)
                return new(true, true, policy.PolicyId, envelope.SignerKeyId, 0, 0, policy.Rules.Count, [],
                    "政策已過期；不評估、不宣告符合。", policy.SchemaVersion,
                    EnterpriseBaselineConditionEvaluator.Semantics(policy.SchemaVersion));

            var conditions = EnterpriseBaselineConditionEvaluator.Evaluate(policy, facts);
            return new(true, false, policy.PolicyId, envelope.SignerKeyId,
                conditions.Satisfied, conditions.NotSatisfied, conditions.Unverifiable,
                conditions.Results, null, policy.SchemaVersion, conditions.Semantics);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException
                                   or ArgumentException or InvalidOperationException)
        {
            return Failed("套件無法驗證或格式無效（" + ex.GetType().Name + "）。");
        }
    }

    // Keep v1's exact field set/order, null operands, timestamp formatting and ordinal key sort.
    // Adding optional public model members must never change bytes emitted by the v1 signer.
    private sealed record LegacyRule(string Key, EnterpriseBaselineMatch Match,
        string? ExpectedValue = null, double? Minimum = null, double? Maximum = null);

    private sealed record LegacyPolicy(int SchemaVersion, string PolicyId,
        DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, IReadOnlyList<LegacyRule> Rules);

    private sealed record Version2Rule(string RequirementId, string Key, EnterpriseBaselineMatch Match,
        string Unit, string? ExpectedValue = null, double? Minimum = null, double? Maximum = null);

    private sealed record Version2Policy(int SchemaVersion, string PolicyId,
        DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, IReadOnlyList<Version2Rule> Rules);

    private static EnterpriseBaselinePolicy DeserializePolicy(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schemaVersion))
            throw new JsonException("政策須有整數 schemaVersion。");
        // Strict typed deserialization below still rejects duplicate and unknown properties.
        if (schemaVersion == LegacySchemaVersion)
        {
            var legacy = JsonSerializer.Deserialize<LegacyPolicy>(payload, JsonOptions)
                ?? throw new JsonException("簽署的政策內容為空。");
            return new(legacy.SchemaVersion, legacy.PolicyId, legacy.CreatedAtUtc, legacy.ExpiresAtUtc,
                legacy.Rules?.Select(rule => rule is null ? null! : new EnterpriseBaselineRule(
                    rule.Key, rule.Match, rule.ExpectedValue, rule.Minimum, rule.Maximum)).ToArray()!);
        }
        if (schemaVersion == CurrentSchemaVersion)
        {
            var current = JsonSerializer.Deserialize<Version2Policy>(payload, JsonOptions)
                ?? throw new JsonException("簽署的政策內容為空。");
            return new(current.SchemaVersion, current.PolicyId, current.CreatedAtUtc, current.ExpiresAtUtc,
                current.Rules?.Select(rule => rule is null ? null! : new EnterpriseBaselineRule(
                    rule.Key, rule.Match, rule.ExpectedValue, rule.Minimum, rule.Maximum,
                    rule.Unit, rule.RequirementId)).ToArray()!);
        }
        throw new ArgumentException("政策 schema 版本不受支援。");
    }

    private static byte[] SerializeCanonical(EnterpriseBaselinePolicy policy)
    {
        if (policy.SchemaVersion == LegacySchemaVersion)
        {
            var legacy = new LegacyPolicy(policy.SchemaVersion, policy.PolicyId,
                policy.CreatedAtUtc, policy.ExpiresAtUtc,
                policy.Rules.OrderBy(rule => rule.Key, StringComparer.Ordinal)
                    .Select(rule => new LegacyRule(rule.Key, rule.Match,
                        rule.ExpectedValue, rule.Minimum, rule.Maximum)).ToArray());
            return JsonSerializer.SerializeToUtf8Bytes(legacy, JsonOptions);
        }
        var current = new Version2Policy(policy.SchemaVersion, policy.PolicyId,
            policy.CreatedAtUtc, policy.ExpiresAtUtc,
            policy.Rules.OrderBy(rule => rule.RequirementId, StringComparer.Ordinal)
                .Select(rule => new Version2Rule(rule.RequirementId!, rule.Key, rule.Match,
                    rule.Unit!, rule.ExpectedValue, rule.Minimum, rule.Maximum)).ToArray());
        return JsonSerializer.SerializeToUtf8Bytes(current, JsonOptions);
    }

    private static string KeyId(ECDsa key) =>
        Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    private static bool IsP256(ECDsa key) =>
        key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;

    private static EnterpriseBaselineEvaluation Failed(string reason) =>
        new(false, false, "", "", 0, 0, 0, [], reason);
}
