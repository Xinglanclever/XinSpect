using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Offline business regressions; no live collection or hardware access.</summary>
public sealed class EnterpriseBaselineV2Tests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    private const string Password = "fixture-only-password";
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string PrivatePem => _signer.ExportEncryptedPkcs8PrivateKeyPem(Password,
        new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1_000));
    private string PublicPem => _signer.ExportSubjectPublicKeyInfoPem();

    private static EnterpriseBaselinePolicy Policy(params EnterpriseBaselineRule[] rules)
        => new(2, "fixture-v2", At.AddDays(-1), At.AddDays(30), rules);

    private static EnterpriseBaselineRule Exact(string unit = "", string expected = "是",
        string key = "fixture.state", string id = "control-state")
        => new(key, EnterpriseBaselineMatch.Exact, expected, Unit: unit, RequirementId: id);

    private static EnterpriseBaselineRule Range(string unit = "", string key = "fixture.count",
        string id = "control-count", double min = 1, double max = 10)
        => new(key, EnterpriseBaselineMatch.NumericRange, Minimum: min, Maximum: max,
            Unit: unit, RequirementId: id);

    private static HardwareFact Fact(string key, string value = "是", string unit = "", double? number = 1)
        => new(key, "fixture", key, value, unit, "fixture-source", FactTrustLevel.Reported, false, At, number);

    private static EnterpriseBaselineConditions Compare(EnterpriseBaselineRule rule, HardwareFact fact)
        => EnterpriseBaselineConditionEvaluator.Evaluate(Policy(rule),
            new Dictionary<string, HardwareFact>(StringComparer.Ordinal) { [rule.Key] = fact });

    [Theory]
    [InlineData("是", "是", EnterpriseBaselineStatus.Satisfied)]
    [InlineData("是", "yes", EnterpriseBaselineStatus.NotSatisfied)]
    [InlineData("true", "True", EnterpriseBaselineStatus.NotSatisfied)]
    [InlineData("是", "是 ", EnterpriseBaselineStatus.NotSatisfied)]
    [InlineData("1", "1.0", EnterpriseBaselineStatus.NotSatisfied)]
    public void Exact_UsesLiteralLanguageCaseWhitespaceAndFormatting(
        string expected, string value, EnterpriseBaselineStatus status)
        => Assert.Equal(status, Assert.Single(Compare(Exact(expected: expected), Fact("fixture.state", value)).Results).Status);

    [Theory]
    [InlineData("C", "F")]
    [InlineData("Hz", "MHz")]
    [InlineData("%", "")]
    [InlineData("MHz", "mhz")]
    [InlineData("MHz", "MHz ")]
    [InlineData("", "count")]
    [InlineData("", null)]
    public void BothPredicates_RequireExactUnitsAndNeverConvert(string required, string? actual)
    {
        foreach (var rule in new[] { Exact(required), Range(required) })
        {
            var fact = Fact(rule.Key, unit: actual!);
            var result = Assert.Single(Compare(rule, fact).Results);
            Assert.Equal(EnterpriseBaselineStatus.Unverifiable, result.Status);
            Assert.Equal("Present", result.EvidenceAvailability);
            Assert.Equal(actual, result.EvidenceUnit);
        }
    }

    [Theory]
    [InlineData(EnterpriseBaselineMatch.Exact)]
    [InlineData(EnterpriseBaselineMatch.NumericRange)]
    public void Version2_RequiresUnitEvenForUnitlessEvidence(EnterpriseBaselineMatch match)
    {
        var rule = match == EnterpriseBaselineMatch.Exact ? Exact() : Range();
        Assert.Throws<ArgumentException>(() => EnterpriseBaselineConditionEvaluator.ValidatePolicy(
            Policy(rule with { Unit = null })));
        Assert.Equal(EnterpriseBaselineStatus.Satisfied, Assert.Single(Compare(rule, Fact(rule.Key)).Results).Status);
        var options = PolicyJsonOptions();
        var json = JsonNode.Parse(JsonSerializer.Serialize(Policy(rule), options))!.AsObject();
        json["rules"]![0]!.AsObject().Remove("unit");
        Assert.Throws<ArgumentException>(() => EnterpriseBaselinePackageService.Sign(json.ToJsonString(), PrivatePem, Password));
    }

    [Theory]
    [InlineData(FactAvailability.Present)]
    [InlineData(FactAvailability.Unknown)]
    [InlineData(FactAvailability.NotApplicable)]
    [InlineData(FactAvailability.NotSupported)]
    [InlineData(FactAvailability.InsufficientPrivilege)]
    [InlineData(FactAvailability.ReadError)]
    public void BothPredicates_PreserveSixStatesWithoutTreatingMissingAsPass(FactAvailability availability)
    {
        const string rawReason = "PRIVATE-RAW-FAILURE-DETAIL";
        foreach (var rule in new[] { Exact(), Range() })
        {
            var fact = Fact(rule.Key) with { Availability = availability, UnavailableReason = availability == FactAvailability.Present ? null : rawReason };
            var result = Assert.Single(Compare(rule, fact).Results);
            Assert.Equal(availability.ToString(), result.EvidenceAvailability);
            Assert.Equal(availability == FactAvailability.Present
                ? EnterpriseBaselineStatus.Satisfied : EnterpriseBaselineStatus.Unverifiable, result.Status);
            Assert.DoesNotContain(rawReason, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(FactTrustLevel.Unknown, false)]
    [InlineData(FactTrustLevel.Speculative, false)]
    [InlineData(FactTrustLevel.Reported, true)]
    [InlineData(FactTrustLevel.Derived, true)]
    [InlineData(FactTrustLevel.Measured, true)]
    [InlineData((FactTrustLevel)999, false)]
    public void BothPredicates_RequireKnownNonSpeculativeTrust(FactTrustLevel trust, bool comparable)
    {
        foreach (var rule in new[] { Exact(), Range() })
        {
            var result = Assert.Single(Compare(rule, Fact(rule.Key) with { Trust = trust }).Results);
            Assert.Equal(comparable ? EnterpriseBaselineStatus.Satisfied : EnterpriseBaselineStatus.Unverifiable, result.Status);
            Assert.Equal(trust.ToString(), result.Trust);
            Assert.Equal("Present", result.EvidenceAvailability);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void BothPredicates_RejectMissingSource(string? source)
    {
        foreach (var rule in new[] { Exact(), Range() })
            Assert.Equal(EnterpriseBaselineStatus.Unverifiable,
                Assert.Single(Compare(rule, Fact(rule.Key) with { Source = source! }).Results).Status);
    }

    [Fact]
    public void PresentNumericZero_IsContraryEvidenceForEnrollmentRequirement()
    {
        var result = Assert.Single(Compare(Range(key: "fixture.mdm.count"),
            Fact("fixture.mdm.count", value: "0 records", number: 0)).Results);
        Assert.Equal(EnterpriseBaselineStatus.NotSatisfied, result.Status);
        Assert.Equal("Present", result.EvidenceAvailability);
    }

    [Theory]
    [InlineData(null, EnterpriseBaselineStatus.Unverifiable)]
    [InlineData(double.NaN, EnterpriseBaselineStatus.Unverifiable)]
    [InlineData(double.PositiveInfinity, EnterpriseBaselineStatus.Unverifiable)]
    [InlineData(double.NegativeInfinity, EnterpriseBaselineStatus.Unverifiable)]
    [InlineData(1d, EnterpriseBaselineStatus.Satisfied)]
    [InlineData(10d, EnterpriseBaselineStatus.Satisfied)]
    [InlineData(11d, EnterpriseBaselineStatus.NotSatisfied)]
    public void Range_UsesOnlyFiniteNumericValueWithInclusiveBounds(double? number, EnterpriseBaselineStatus status)
        => Assert.Equal(status, Assert.Single(Compare(Range(), Fact("fixture.count", value: "5", number: number)).Results).Status);

    [Fact]
    public void KeyIdentityRedactionInferenceAndPriorOutput_CannotBecomeEvidence()
    {
        var state = Exact();
        foreach (var fact in new[]
        {
            Fact("different.key"), Fact(state.Key, HardwareSnapshotService.RedactedValue), Fact(state.Key) with { Value = null! },
            Fact(state.Key) with { Availability = (FactAvailability)999 },
        })
            Assert.Equal(EnterpriseBaselineStatus.Unverifiable, Assert.Single(Compare(state, fact).Results).Status);
        foreach (string key in new[] { "infer.fixture", DeviceComplianceFactsService.BaselineKey, DeviceComplianceFactsService.TableKey })
            Assert.Equal(EnterpriseBaselineStatus.Unverifiable,
                Assert.Single(Compare(Exact(key: key), Fact(key)).Results).Status);
        var insensitive = new Dictionary<string, HardwareFact>(StringComparer.OrdinalIgnoreCase)
            { ["FIXTURE.STATE"] = Fact(state.Key) };
        Assert.Equal(EnterpriseBaselineStatus.Unverifiable,
            Assert.Single(EnterpriseBaselineConditionEvaluator.Evaluate(Policy(state), insensitive).Results).Status);
    }

    [Fact]
    public void DistinctControlsMayShareOneFact_WithoutCollapsingCounts()
    {
        var first = Exact(id: "acceptance-one");
        var second = Exact(expected: "否", id: "audit-two");
        var policy = Policy(first, second);
        string package = EnterpriseBaselinePackageService.Sign(policy, PrivatePem, Password);
        var signed = EnterpriseBaselinePackageService.Evaluate(package, PublicPem,
            new Dictionary<string, HardwareFact> { [first.Key] = Fact(first.Key) }, At);
        Assert.True(signed.SignatureValid);
        Assert.Equal(1, signed.Satisfied);
        Assert.Equal(1, signed.NotSatisfied);
        Assert.Equal(new[] { "acceptance-one", "audit-two" }, signed.Results.Select(r => r.RequirementId).ToArray());
        Assert.Throws<ArgumentException>(() => EnterpriseBaselineConditionEvaluator.ValidatePolicy(
            Policy(first, second with { RequirementId = first.RequirementId })));
    }

    [Fact]
    public void Version2_RejectsMissingIdentityWildcardsMixedOperandsAndInvalidRanges()
    {
        var invalid = new[]
        {
            Exact() with { RequirementId = null }, Exact() with { RequirementId = " " },
            Exact() with { Key = "fixture.*" }, Exact() with { Minimum = 0 }, Exact() with { Maximum = 1 },
            Range() with { ExpectedValue = "1" }, Range() with { Minimum = double.NaN },
            Range() with { Maximum = double.PositiveInfinity }, Range() with { Minimum = 11 },
            Range() with { Maximum = null }, Exact() with { Match = (EnterpriseBaselineMatch)99 },
        };
        foreach (var rule in invalid)
            Assert.Throws<ArgumentException>(() => EnterpriseBaselineConditionEvaluator.ValidatePolicy(Policy(rule)));
        Assert.Throws<ArgumentException>(() => EnterpriseBaselineConditionEvaluator.ValidatePolicy(Policy()));
        Assert.Throws<ArgumentException>(() => EnterpriseBaselineConditionEvaluator.ValidatePolicy(
            Policy(Exact()) with { SchemaVersion = 3 }));
    }

    [Fact]
    public void StrictJson_RejectsUnknownAndDuplicateFieldsAndIntegerPredicates()
    {
        string json = JsonSerializer.Serialize(Policy(Exact()), PolicyJsonOptions());
        var duplicate = json.Replace("\"unit\":\"\"", "\"unit\":\"\",\"unit\":\"\"", StringComparison.Ordinal);
        var unknown = json.Replace("\"unit\":\"\"", "\"unit\":\"\",\"convertUnit\":true", StringComparison.Ordinal);
        var integer = json.Replace("\"match\":\"Exact\"", "\"match\":0", StringComparison.Ordinal);
        foreach (string invalid in new[] { duplicate, unknown, integer })
            Assert.Throws<JsonException>(() => EnterpriseBaselinePackageService.Sign(invalid, PrivatePem, Password));
    }

    // Independent byte contract: this literal was written from the original v1 wire format,
    // not produced by either the new canonical writer or a duplicate policy-model serializer.
    private const string LegacyPayload = """
        {"schemaVersion":1,"policyId":"fixture-v1","createdAtUtc":"2026-11-16T00:00:00+00:00","expiresAtUtc":"2026-12-17T00:00:00+00:00","rules":[{"key":"a.fixture","match":"Exact","expectedValue":"YES","minimum":null,"maximum":null},{"key":"z.fixture","match":"NumericRange","expectedValue":null,"minimum":0,"maximum":10}]}
        """;

    [Fact]
    public void Version1_CanonicalBytesAndExternalSignatureRemainCompatible()
    {
        var policy = new EnterpriseBaselinePolicy(1, "fixture-v1", At.AddDays(-1), At.AddDays(30),
            [new("z.fixture", EnterpriseBaselineMatch.NumericRange, Minimum: 0, Maximum: 10),
             new("a.fixture", EnterpriseBaselineMatch.Exact, "YES")]);
        string package = EnterpriseBaselinePackageService.Sign(policy, PrivatePem, Password);
        byte[] actual = Convert.FromBase64String(JsonNode.Parse(package)!["payload"]!.GetValue<string>());
        Assert.Equal(Encoding.UTF8.GetBytes(LegacyPayload), actual);
        using var independentVerifier = ECDsa.Create();
        independentVerifier.ImportFromPem(PublicPem);
        Assert.True(independentVerifier.VerifyData(actual,
            Convert.FromBase64String(JsonNode.Parse(package)!["signature"]!.GetValue<string>()),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var result = EnterpriseBaselinePackageService.Evaluate(SignRaw(LegacyPayload, 1), PublicPem,
            new Dictionary<string, HardwareFact>
            {
                ["a.fixture"] = Fact("a.fixture", "YES", "different-unit"),
                ["z.fixture"] = Fact("z.fixture", number: 0),
            }, At);
        Assert.True(result.SignatureValid);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal(2, result.Satisfied);
        Assert.Contains("未綁定單位", result.Semantics, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyTrustTightening_IsExplicitAndDoesNotReinterpretUnits()
    {
        var result = EnterpriseBaselinePackageService.Evaluate(SignRaw(LegacyPayload, 1), PublicPem,
            new Dictionary<string, HardwareFact>
            {
                ["a.fixture"] = Fact("a.fixture", "YES") with { Trust = FactTrustLevel.Unknown },
                ["z.fixture"] = Fact("z.fixture") with { Source = " " },
            }, At);
        Assert.True(result.SignatureValid);
        Assert.Equal(0, result.Satisfied);
        Assert.Equal(2, result.Unverifiable);
        Assert.Contains("Unknown", result.Semantics, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => EnterpriseBaselinePackageService.Sign(
            LegacyPayload.Replace("\"match\":\"Exact\"", "\"unit\":\"\",\"match\":\"Exact\"", StringComparison.Ordinal), PrivatePem, Password));
    }

    [Fact]
    public void Version2_SignsUnitAndControlIdentityAndRejectsEnvelopePayloadMismatch()
    {
        string package = EnterpriseBaselinePackageService.Sign(Policy(Exact("MHz")), PrivatePem, Password);
        foreach (string field in new[] { "unit", "requirementId" })
        {
            var envelope = JsonNode.Parse(package)!.AsObject();
            var payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(envelope["payload"]!.GetValue<string>())))!;
            payload["rules"]![0]![field] = "tampered";
            envelope["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            Assert.False(EnterpriseBaselinePackageService.Evaluate(envelope.ToJsonString(), PublicPem,
                new Dictionary<string, HardwareFact>(), At).SignatureValid);
        }
        Assert.False(EnterpriseBaselinePackageService.Evaluate(SignRaw(LegacyPayload, 2), PublicPem,
            new Dictionary<string, HardwareFact>(), At).SignatureValid);
    }

    [Fact]
    public void Version2_CanonicalControlOrderingAndValidityMetadataAreStable()
    {
        var policy = Policy(Exact(id: "z-control"), Range(id: "a-control"));
        string package = EnterpriseBaselinePackageService.Sign(policy, PrivatePem, Password);
        var payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(JsonNode.Parse(package)!["payload"]!.GetValue<string>())))!;
        Assert.Equal("a-control", payload["rules"]![0]!["requirementId"]!.GetValue<string>());
        foreach (var clock in new[] { At.AddDays(-2), At.AddDays(30) })
        {
            var result = EnterpriseBaselinePackageService.Evaluate(package, PublicPem,
                new Dictionary<string, HardwareFact>(), clock);
            Assert.True(result.SignatureValid);
            Assert.Equal(2, result.SchemaVersion);
            Assert.Equal(2, result.Unverifiable);
            Assert.Empty(result.Results);
            Assert.NotNull(result.Semantics);
            Assert.NotNull(result.FailureReason);
        }
    }

    [Fact]
    public void DeviceAdapter_UsesSameCoreButExcludesAmbiguousAndPreviousOutputs()
    {
        var policy = Policy(Exact(), Range());
        var input = new[] { Fact("fixture.state"), Fact("fixture.count", number: 0) };
        string package = EnterpriseBaselinePackageService.Sign(policy, PrivatePem, Password);
        var signed = EnterpriseBaselinePackageService.Evaluate(package, PublicPem,
            input.ToDictionary(f => f.Key, StringComparer.Ordinal), At);
        var adapter = DeviceComplianceFactsService.EvaluateConditions(policy, input);
        // Signed payloads use canonical control ordering; compare every field by control identity.
        Assert.Equal(signed.Results.OrderBy(result => result.RequirementId, StringComparer.Ordinal).ToArray(),
            adapter.Results.OrderBy(result => result.RequirementId, StringComparer.Ordinal).ToArray());
        Assert.Equal(signed.Satisfied, adapter.Satisfied);
        Assert.Equal(signed.NotSatisfied, adapter.NotSatisfied);
        Assert.Equal(signed.Unverifiable, adapter.Unverifiable);
        var ambiguous = DeviceComplianceFactsService.EvaluateConditions(Policy(Exact()), [input[0], input[0]]);
        Assert.Equal(1, ambiguous.Unverifiable);
        Assert.Equal(0, ambiguous.Satisfied);
        var circular = DeviceComplianceFactsService.EvaluateConditions(
            Policy(Exact(key: DeviceComplianceFactsService.BaselineKey)), [Fact(DeviceComplianceFactsService.BaselineKey)]);
        Assert.Equal(1, circular.Unverifiable);
        Assert.Equal(0, circular.Satisfied);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(2, "")]
    [InlineData(2, "MHz")]
    [InlineData(2, null)]
    public void Cli_ReportsActualSchemaAndKeepsRawValuesPrivate(int schemaVersion, string? evidenceUnit)
    {
        string directory = Path.Combine(Path.GetTempPath(), "XinSpect-v2-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string secret = "PRIVATE-EVIDENCE-TEXT";
            bool unitMismatch = schemaVersion == 2 && evidenceUnit != "";
            var now = DateTimeOffset.UtcNow;
            var rule = Exact(expected: secret);
            if (schemaVersion == 1) rule = rule with { Unit = null, RequirementId = null };
            var policy = new EnterpriseBaselinePolicy(schemaVersion, "cli-fixture", now.AddDays(-1), now.AddDays(1), [rule]);
            string policyPath = Path.Combine(directory, "policy.json");
            string keyPath = Path.Combine(directory, "public.pem");
            string snapshotPath = Path.Combine(directory, "snapshot.json");
            File.WriteAllText(policyPath, EnterpriseBaselinePackageService.Sign(policy, PrivatePem, Password));
            File.WriteAllText(keyPath, PublicPem);
            HardwareSnapshotService.Save(snapshotPath, HardwareSnapshotService.Create("test",
                [Fact(rule.Key, secret, unit: evidenceUnit!)], includeSensitive: false));
            var output = new StringWriter();
            var error = new StringWriter();
            int exit = CliService.Run(["--enterprise-baseline", policyPath, "--trusted-key", keyPath, "--snapshot", snapshotPath],
                () => throw new InvalidOperationException("Must not collect hardware"), output, error);
            Assert.Equal(unitMismatch ? CliService.ExitPartial : CliService.ExitOk, exit);
            Assert.Empty(error.ToString());
            Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(schemaVersion, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.NotNull(json.RootElement.GetProperty("semantics").GetString());
            Assert.True(json.RootElement.GetProperty("policySignatureValid").GetBoolean());
            Assert.Equal(unitMismatch ? "Unverifiable" : "Satisfied", json.RootElement.GetProperty("status").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private string SignRaw(string payload, int envelopeVersion)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = envelopeVersion, algorithm = "ECDSA-P256-SHA256",
            signerKeyId = Convert.ToHexStringLower(SHA256.HashData(_signer.ExportSubjectPublicKeyInfo())),
            payload = Convert.ToBase64String(bytes),
            signature = Convert.ToBase64String(_signer.SignData(bytes, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
        });
    }

    private static JsonSerializerOptions PolicyJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public void Dispose() => _signer.Dispose();
}
