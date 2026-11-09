using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

public sealed class EnterpriseBaselinePackageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private const string SigningPassword = "fixture-only-password";
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string PrivatePem => _signer.ExportEncryptedPkcs8PrivateKeyPem(SigningPassword,
        new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));
    private string PublicPem => _signer.ExportSubjectPublicKeyInfoPem();

    private static EnterpriseBaselinePolicy Policy(params EnterpriseBaselineRule[] rules) => new(
        EnterpriseBaselinePackageService.CurrentSchemaVersion,
        "org-workstation-v1", Now.AddDays(-1), Now.AddDays(30), rules);

    private static HardwareFact Fact(string key, string value, double? numeric = null,
        FactAvailability availability = FactAvailability.Present, string? reason = null) => new(
        key, "測試", key, availability == FactAvailability.Present ? value : "", "",
        "fixture", FactTrustLevel.Reported, false, Now, numeric, availability, reason);

    [Fact]
    public void 有效簽章_精確與數值界線都能評估()
    {
        string package = EnterpriseBaselinePackageService.Sign(Policy(
            new EnterpriseBaselineRule("platform.secure_boot", EnterpriseBaselineMatch.Exact, "是"),
            new EnterpriseBaselineRule("cpu.cores", EnterpriseBaselineMatch.NumericRange, Minimum: 8, Maximum: 32)), PrivatePem, SigningPassword);
        var facts = new Dictionary<string, HardwareFact>
        {
            ["platform.secure_boot"] = Fact("platform.secure_boot", "是"),
            ["cpu.cores"] = Fact("cpu.cores", "16 核", 16),
        };

        var result = EnterpriseBaselinePackageService.Evaluate(package, PublicPem, facts, Now);

        Assert.True(result.SignatureValid);
        Assert.Matches("^[0-9a-f]{64}$", result.SignerKeyId);
        Assert.Equal(2, result.Satisfied);
        Assert.Equal(0, result.NotSatisfied);
        Assert.Equal(0, result.Unverifiable);
        var evidence = Assert.Single(result.Results, x => x.Key == "platform.secure_boot");
        Assert.Equal("Present", evidence.EvidenceAvailability);
        Assert.Equal("fixture", evidence.Source);
        Assert.Equal(FactTrustLevel.Reported.ToString(), evidence.Trust);
        Assert.Equal(Now, evidence.MeasuredAtUtc);
        Assert.DoesNotContain("是", System.Text.Json.JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public void 缺鍵或非Present資料_不得算通過()
    {
        string package = EnterpriseBaselinePackageService.Sign(Policy(
            new EnterpriseBaselineRule("platform.secure_boot", EnterpriseBaselineMatch.Exact, "是"),
            new EnterpriseBaselineRule("tpm.present", EnterpriseBaselineMatch.Exact, "是")), PrivatePem, SigningPassword);
        var facts = new Dictionary<string, HardwareFact>
        {
            ["platform.secure_boot"] = Fact("platform.secure_boot", "", availability: FactAvailability.ReadError,
                reason: "來源逾時"),
        };

        var result = EnterpriseBaselinePackageService.Evaluate(package, PublicPem, facts, Now);

        Assert.Equal(2, result.Unverifiable);
        Assert.Equal(0, result.Satisfied);
        Assert.All(result.Results, r => Assert.Equal(EnterpriseBaselineStatus.Unverifiable, r.Status));
        Assert.Contains(result.Results, r => r.EvidenceAvailability == "ReadError" && r.Source == "fixture");

        var nullFact = EnterpriseBaselinePackageService.Evaluate(package, PublicPem,
            new Dictionary<string, HardwareFact> { ["platform.secure_boot"] = null! }, Now);
        Assert.Equal(2, nullFact.Unverifiable);
        Assert.All(nullFact.Results, r => Assert.Equal(EnterpriseBaselineStatus.Unverifiable, r.Status));
    }

    [Fact]
    public void 值不符_回報不符合而非安全判決()
    {
        string package = EnterpriseBaselinePackageService.Sign(Policy(
            new EnterpriseBaselineRule("platform.secure_boot", EnterpriseBaselineMatch.Exact, "是")), PrivatePem, SigningPassword);
        var result = EnterpriseBaselinePackageService.Evaluate(package, PublicPem,
            new Dictionary<string, HardwareFact>
            {
                ["platform.secure_boot"] = Fact("platform.secure_boot", "否"),
            }, Now);

        Assert.Equal(1, result.NotSatisfied);
        Assert.Contains("不是故障或安全判決", result.Results[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 套件遭竄改_不執行政策()
    {
        string package = EnterpriseBaselinePackageService.Sign(Policy(
            new EnterpriseBaselineRule("platform.secure_boot", EnterpriseBaselineMatch.Exact, "是")), PrivatePem, SigningPassword);
        using var document = JsonDocument.Parse(package);
        string encoded = document.RootElement.GetProperty("payload").GetString()!;
        string changedPayload = Encoding.UTF8.GetString(Convert.FromBase64String(encoded))
            .Replace("org-workstation-v1", "org-admin-v1", StringComparison.Ordinal);
        string tampered = package.Replace(encoded,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(changedPayload)), StringComparison.Ordinal);

        var result = EnterpriseBaselinePackageService.Evaluate(tampered, PublicPem,
            new Dictionary<string, HardwareFact>(), Now);

        Assert.False(result.SignatureValid);
        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.Results);
    }

    [Fact]
    public void 不受信任的公鑰_拒絕套件()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string package = EnterpriseBaselinePackageService.Sign(Policy(
            new EnterpriseBaselineRule("platform.secure_boot", EnterpriseBaselineMatch.Exact, "是")), PrivatePem, SigningPassword);

        var result = EnterpriseBaselinePackageService.Evaluate(package, other.ExportSubjectPublicKeyInfoPem(),
            new Dictionary<string, HardwareFact>(), Now);

        Assert.False(result.SignatureValid);
        Assert.Contains("信任金鑰不符", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void 過期政策_驗簽成功但不產生符合結果()
    {
        var expired = Policy(new EnterpriseBaselineRule("platform.secure_boot", EnterpriseBaselineMatch.Exact, "是"))
            with { ExpiresAtUtc = Now };
        string package = EnterpriseBaselinePackageService.Sign(expired, PrivatePem, SigningPassword);

        var result = EnterpriseBaselinePackageService.Evaluate(package, PublicPem,
            new Dictionary<string, HardwareFact>(), Now);

        Assert.True(result.SignatureValid);
        Assert.True(result.Expired);
        Assert.Equal(0, result.Satisfied);
        Assert.Equal(1, result.Unverifiable);
        Assert.Empty(result.Results);
    }

    [Fact]
    public void 數值邊界含端點_無數值與非有限數值為Unverifiable()
    {
        string package = EnterpriseBaselinePackageService.Sign(Policy(
            new EnterpriseBaselineRule("sensor.cpu.temp", EnterpriseBaselineMatch.NumericRange, Minimum: 0, Maximum: 100)), PrivatePem, SigningPassword);
        var atMinimum = EnterpriseBaselinePackageService.Evaluate(package, PublicPem,
            new Dictionary<string, HardwareFact> { ["sensor.cpu.temp"] = Fact("sensor.cpu.temp", "0", 0) }, Now);
        var noNumber = EnterpriseBaselinePackageService.Evaluate(package, PublicPem,
            new Dictionary<string, HardwareFact> { ["sensor.cpu.temp"] = Fact("sensor.cpu.temp", "100", null) }, Now);
        var infinite = EnterpriseBaselinePackageService.Evaluate(package, PublicPem,
            new Dictionary<string, HardwareFact> { ["sensor.cpu.temp"] = Fact("sensor.cpu.temp", "Inf", double.PositiveInfinity) }, Now);

        Assert.Equal(1, atMinimum.Satisfied);
        Assert.Equal(1, noNumber.Unverifiable);
        Assert.Equal(1, infinite.Unverifiable);
    }

    [Fact]
    public void 重複鍵或無效界線_簽發前拒絕()
    {
        var duplicate = Policy(
            new EnterpriseBaselineRule("cpu.cores", EnterpriseBaselineMatch.Exact, "8"),
            new EnterpriseBaselineRule("cpu.cores", EnterpriseBaselineMatch.Exact, "16"));
        var invertedRange = Policy(
            new EnterpriseBaselineRule("sensor.cpu.temp", EnterpriseBaselineMatch.NumericRange, Minimum: 100, Maximum: 0));
        var nullRule = Policy(new EnterpriseBaselineRule[] { null! });

        Assert.Throws<ArgumentException>(() => EnterpriseBaselinePackageService.Sign(duplicate, PrivatePem, SigningPassword));
        Assert.Throws<ArgumentException>(() => EnterpriseBaselinePackageService.Sign(invertedRange, PrivatePem, SigningPassword));
        Assert.Throws<ArgumentException>(() => EnterpriseBaselinePackageService.Sign(nullRule, PrivatePem, SigningPassword));
    }

    [Fact]
    public void 加密私鑰密碼錯誤_簽署拒絕()
    {
        var policy = Policy(new EnterpriseBaselineRule("cpu.cores", EnterpriseBaselineMatch.Exact, "8"));

        Assert.ThrowsAny<CryptographicException>(() =>
            EnterpriseBaselinePackageService.Sign(policy, PrivatePem, "wrong-password"));
    }

    [Fact]
    public void 空政策與非Utc時間_簽發前拒絕()
    {
        var empty = Policy();
        var localTime = Policy(new EnterpriseBaselineRule("platform.secure_boot", EnterpriseBaselineMatch.Exact, "是"))
            with { CreatedAtUtc = Now.ToOffset(TimeSpan.FromHours(8)) };

        Assert.Throws<ArgumentException>(() => EnterpriseBaselinePackageService.Sign(empty, PrivatePem, SigningPassword));
        Assert.Throws<ArgumentException>(() => EnterpriseBaselinePackageService.Sign(localTime, PrivatePem, SigningPassword));
    }

    [Fact]
    public void 未提供信任公鑰_明確拒絕()
    {
        var result = EnterpriseBaselinePackageService.Evaluate("{}", " ",
            new Dictionary<string, HardwareFact>(), Now);

        Assert.False(result.SignatureValid);
        Assert.Contains("預先信任", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void 錯誤格式與超大套件_回失敗狀態而非拋出()
    {
        var malformed = EnterpriseBaselinePackageService.Evaluate("{", PublicPem,
            new Dictionary<string, HardwareFact>(), Now);
        var oversized = EnterpriseBaselinePackageService.Evaluate(new string(' ',
                EnterpriseBaselinePackageService.MaxPackageBytes + 1), PublicPem,
            new Dictionary<string, HardwareFact>(), Now);

        Assert.False(malformed.SignatureValid);
        Assert.False(oversized.SignatureValid);
        Assert.NotNull(malformed.FailureReason);
        Assert.NotNull(oversized.FailureReason);
    }

    [Fact]
    public void OversizedPolicyAndKeyInputsAreRejectedBeforeParsing()
    {
        var policy = Policy(new EnterpriseBaselineRule("cpu.cores", EnterpriseBaselineMatch.Exact, "8"));
        string oversizedInput = new string('x', EnterpriseBaselinePackageService.MaxPackageBytes + 1);
        string oversizedPem = new string('x', EnterpriseBaselinePackageService.MaxKeyPemBytes + 1);

        Assert.Throws<ArgumentException>(() => EnterpriseBaselinePackageService.Sign(
            oversizedInput, PrivatePem, SigningPassword));
        Assert.Throws<ArgumentException>(() => EnterpriseBaselinePackageService.Sign(
            policy, oversizedPem, SigningPassword));
        var evaluation = EnterpriseBaselinePackageService.Evaluate("{}", oversizedPem,
            new Dictionary<string, HardwareFact>(), Now);
        Assert.False(evaluation.SignatureValid);
        Assert.Contains("上限", evaluation.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void 套件服務不執行網路或檔案I_O()
    {
        string root = new DirectoryInfo(AppContext.BaseDirectory).FullName;
        while (!File.Exists(Path.Combine(root, "XinSpect.csproj")))
            root = Directory.GetParent(root)!.FullName;
        string source = File.ReadAllText(Path.Combine(root, "Services", "EnterpriseBaselinePackageService.cs"));

        Assert.DoesNotContain("HttpClient", source);
        Assert.DoesNotContain("WebRequest", source);
        Assert.DoesNotContain("File.", source);
        Assert.DoesNotContain("Directory.", source);
    }

    public void Dispose() => _signer.Dispose();
}
