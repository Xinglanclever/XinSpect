using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Offline signed-policy import regressions; no dialogs, network or hardware collection.</summary>
public sealed class EnterpriseBaselineImportTests : IDisposable
{
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly DateTimeOffset At = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    private const string Password = "fixture-only-password";
    private const string PrivateValue = "PRIVATE-EXPECTED-EVIDENCE";
    private string PublicPem => _signer.ExportSubjectPublicKeyInfoPem();

    private string Package(int version = 2, DateTimeOffset? created = null, DateTimeOffset? expires = null)
    {
        var rule = new EnterpriseBaselineRule("fixture.import", EnterpriseBaselineMatch.Exact,
            PrivateValue, Unit: version == 2 ? "" : null, RequirementId: version == 2 ? "PRIVATE-CONTROL-ID" : null);
        var policy = new EnterpriseBaselinePolicy(version, "PRIVATE-POLICY-ID", created ?? At.AddDays(-1),
            expires ?? At.AddDays(1), [rule]);
        return EnterpriseBaselinePackageService.Sign(policy,
            _signer.ExportEncryptedPkcs8PrivateKeyPem(Password,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1_000)), Password);
    }

    private static HardwareSnapshot Snapshot(string? unit = "", FactTrustLevel trust = FactTrustLevel.Reported,
        FactAvailability availability = FactAvailability.Present)
        => HardwareSnapshotService.Create("fixture", [new HardwareFact("fixture.import", "fixture", "fixture",
            PrivateValue, unit!, "fixture-source", trust, false, At, Availability: availability,
            UnavailableReason: availability == FactAvailability.Present ? null : "fixture evidence unavailable")], includeSensitive: false);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SavedSnapshotImport_UsesSharedSignedPolicyVersionAndVerdict(int version)
    {
        string package = Package(version);
        int reads = 0;
        var actual = EnterpriseBaselineImportService.EvaluateInputs(() => package, () => PublicPem,
            () => { reads++; return Snapshot(); }, () => At);
        Assert.True(actual.SignatureValid);
        Assert.Equal(version, actual.SchemaVersion);
        Assert.Equal(1, actual.Satisfied);
        Assert.Equal(1, reads);
        Assert.Equal("Present", Assert.Single(actual.Results).EvidenceAvailability);
    }

    [Theory]
    [InlineData("", EnterpriseBaselineStatus.Satisfied)]
    [InlineData(null, EnterpriseBaselineStatus.Unverifiable)]
    [InlineData("MHz", EnterpriseBaselineStatus.Unverifiable)]
    public void SavedSnapshotImport_DoesNotCoerceUnknownUnitToUnitless(string? unit, EnterpriseBaselineStatus status)
    {
        var result = EnterpriseBaselineImportService.EvaluateInputs(() => Package(), () => PublicPem,
            () => Snapshot(unit), () => At);
        Assert.Equal(status, Assert.Single(result.Results).Status);
        Assert.Equal(unit, result.Results[0].EvidenceUnit);
    }

    [Theory]
    [InlineData(FactAvailability.Unknown)]
    [InlineData(FactAvailability.NotSupported)]
    [InlineData(FactAvailability.InsufficientPrivilege)]
    [InlineData(FactAvailability.ReadError)]
    [InlineData(FactAvailability.NotApplicable)]
    public void SavedSnapshotImport_PreservesAllUnavailableEvidenceStates(FactAvailability availability)
    {
        var result = EnterpriseBaselineImportService.EvaluateInputs(() => Package(), () => PublicPem,
            () => Snapshot(availability: availability), () => At);
        Assert.Equal(1, result.Unverifiable);
        Assert.Equal(0, result.Satisfied);
        Assert.Equal(availability.ToString(), Assert.Single(result.Results).EvidenceAvailability);
    }

    [Theory]
    [InlineData(FactTrustLevel.Unknown)]
    [InlineData(FactTrustLevel.Speculative)]
    public void SavedSnapshotImport_RejectsUnknownAndSpeculativeTrust(FactTrustLevel trust)
    {
        var result = EnterpriseBaselineImportService.EvaluateInputs(() => Package(), () => PublicPem,
            () => Snapshot(trust: trust), () => At);
        Assert.Equal(1, result.Unverifiable);
        Assert.Equal(0, result.Satisfied);
        Assert.Equal(trust.ToString(), Assert.Single(result.Results).Trust);
    }

    [Fact]
    public void RejectedUntrustedFutureAndExpiredPolicies_NeverTouchSnapshot()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var cases = new[]
        {
            (Package: "{}", Key: PublicPem),
            (Package: Package(), Key: other.ExportSubjectPublicKeyInfoPem()),
            (Package: Package(created: At.AddHours(1)), Key: PublicPem),
            (Package: Package(created: At.AddDays(-2), expires: At), Key: PublicPem),
        };
        foreach (var item in cases)
        {
            var result = EnterpriseBaselineImportService.EvaluateInputs(() => item.Package, () => item.Key,
                () => throw new InvalidOperationException("Snapshot must not be touched"), () => At);
            Assert.True(!result.SignatureValid || result.FailureReason is not null);
            Assert.Equal(0, result.Satisfied);
        }
    }

    [Fact]
    public void PolicyExpiringDuringSnapshotRead_IsCheckedAgainBeforeComparison()
    {
        string package = Package(expires: At.AddSeconds(1));
        int clockReads = 0;
        var result = EnterpriseBaselineImportService.EvaluateInputs(() => package, () => PublicPem,
            () => Snapshot(), () => ++clockReads == 1 ? At : At.AddSeconds(1));
        Assert.True(result.SignatureValid);
        Assert.True(result.Expired);
        Assert.Empty(result.Results);
        Assert.Equal(0, result.Satisfied);
        Assert.Equal(2, clockReads);
    }

    [Fact]
    public async Task ImportProjection_OnlyShowsAggregateAndNeverPolicyOrEvidenceValues()
    {
        var result = EnterpriseBaselineImportService.EvaluateInputs(() => Package(), () => PublicPem,
            () => Snapshot(), () => At);
        var service = new EnterpriseBaselineImportService((_, _, _) => result);
        await service.EvaluateAsync("PRIVATE-PACKAGE-PATH", "PRIVATE-KEY-PATH", "PRIVATE-SNAPSHOT-PATH");
        string projection = Projection(service);
        Assert.Contains("符合 1 項", projection, StringComparison.Ordinal);
        Assert.Contains("schema 2", projection, StringComparison.Ordinal);
        Assert.Contains(result.SignerKeyId, projection, StringComparison.Ordinal);
        foreach (string value in new[] { PrivateValue, "PRIVATE-POLICY-ID", "PRIVATE-CONTROL-ID", "fixture.import",
                     "PRIVATE-PACKAGE-PATH", "PRIVATE-KEY-PATH", "PRIVATE-SNAPSHOT-PATH" })
            Assert.DoesNotContain(value, projection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedNewImport_ClearsOldSuccessAndHidesExceptionDetails()
    {
        var result = EnterpriseBaselineImportService.EvaluateInputs(() => Package(), () => PublicPem,
            () => Snapshot(), () => At);
        int calls = 0;
        var service = new EnterpriseBaselineImportService((_, _, _) => ++calls == 1 ? result
            : throw new IOException("PRIVATE-PATH-AND-EXCEPTION-TEXT"));
        await service.EvaluateAsync("", "", "");
        Assert.Contains("符合 1 項", service.Summary, StringComparison.Ordinal);
        await service.EvaluateAsync("", "", "");
        Assert.DoesNotContain("符合 1 項", service.Summary, StringComparison.Ordinal);
        Assert.Contains("IOException", service.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-PATH-AND-EXCEPTION-TEXT", Projection(service), StringComparison.Ordinal);
        Assert.Equal("—", service.SignerFingerprint);
        Assert.Equal("—", service.Schema);
        Assert.True(service.CanRun);
    }

    [Fact]
    public async Task StartingNewSelection_ClearsOldCountsBeforeDialogsCanBeCanceled()
    {
        var result = EnterpriseBaselineImportService.EvaluateInputs(() => Package(), () => PublicPem,
            () => Snapshot(), () => At);
        var service = new EnterpriseBaselineImportService((_, _, _) => result);
        await service.EvaluateAsync("", "", "");
        service.ResetSelection();
        Assert.DoesNotContain("符合 1 項", Projection(service), StringComparison.Ordinal);
        Assert.Equal("—", service.SignerFingerprint);
        Assert.Contains("取消選檔", service.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleFlight_BlocksSecondImportAndStateResetUntilWorkerReturns()
    {
        var result = EnterpriseBaselineImportService.EvaluateInputs(() => Package(), () => PublicPem,
            () => Snapshot(), () => At);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        int calls = 0;
        var service = new EnterpriseBaselineImportService((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture release missing");
            return result;
        });
        var pending = service.EvaluateAsync("", "", "");
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(service.CanRun);
            string status = service.Status;
            await service.EvaluateAsync("", "", "");
            service.ResetSelection();
            Assert.Equal(1, calls);
            Assert.Equal(status, service.Status);
        }
        finally { release.Set(); await pending; }
        Assert.True(service.CanRun);
    }

    [Fact]
    public void FileBoundary_ValidatesPersistedHashAndRejectsOversizedOrMalformedUtf8BeforeSnapshot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "XinSpect-policy-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string policyPath = Path.Combine(directory, "policy.json");
            string keyPath = Path.Combine(directory, "public.pem");
            string snapshotPath = Path.Combine(directory, "snapshot.xinsnapshot");
            File.WriteAllText(policyPath, Package());
            File.WriteAllText(keyPath, PublicPem);
            HardwareSnapshotService.Save(snapshotPath, Snapshot());
            // Production entry uses current time; use a currently valid independently signed
            // policy here rather than a fixed historical validity window.
            string nowPackage = Package(created: DateTimeOffset.UtcNow.AddDays(-1), expires: DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText(policyPath, nowPackage);
            Assert.Equal(1, EnterpriseBaselineImportService.EvaluateFiles(policyPath, keyPath, snapshotPath).Satisfied);
            string saved = File.ReadAllText(snapshotPath);
            File.WriteAllText(snapshotPath, saved.Replace(PrivateValue, "TAMPERED-EVIDENCE", StringComparison.Ordinal));
            Assert.ThrowsAny<Exception>(() => EnterpriseBaselineImportService.EvaluateFiles(policyPath, keyPath, snapshotPath));
            File.WriteAllText(policyPath, new string('x', EnterpriseBaselinePackageService.MaxPackageBytes + 1));
            Assert.Throws<InvalidDataException>(() => EnterpriseBaselineImportService.EvaluateFiles(policyPath, keyPath, "must-not-open"));
            File.WriteAllBytes(policyPath, [0xff, 0xff]);
            Assert.Throws<DecoderFallbackException>(() => EnterpriseBaselineImportService.EvaluateFiles(policyPath, keyPath, "must-not-open"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Projection(EnterpriseBaselineImportService service)
        => string.Join("\n", service.Status, service.Summary, service.PolicySignature, service.Schema, service.SignerFingerprint);

    public void Dispose() => _signer.Dispose();
}
