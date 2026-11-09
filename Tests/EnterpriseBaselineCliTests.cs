using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace XinSpect.Tests;

public sealed class EnterpriseBaselineCliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "XinSpect-enterprise-baseline-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly DateTimeOffset At = new(2026, 11, 9, 1, 0, 0, TimeSpan.Zero);
    private const string SigningPassword = "fixture-only-password";
    private string PrivatePem => _signer.ExportEncryptedPkcs8PrivateKeyPem(SigningPassword,
        new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));

    public EnterpriseBaselineCliTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void 已信任的簽章套件_只輸出判定不輸出快照值()
    {
        const string privateValue = "PRIVATE-HARDWARE-VALUE";
        string policyPath = Write("policy.json", EnterpriseBaselinePackageService.Sign(
            new EnterpriseBaselinePolicy(1, "org-workstation", At.AddDays(-1), At.AddDays(1),
                [new("cpu.model", EnterpriseBaselineMatch.Exact, privateValue)]),
            PrivatePem, SigningPassword));
        string keyPath = Write("trusted-public.pem", _signer.ExportSubjectPublicKeyInfoPem());
        var snapshot = HardwareSnapshotService.Create("test",
            [Fact("cpu.model", privateValue)], includeSensitive: false);
        string snapshotPath = Path.Combine(_directory, "snapshot.json");
        HardwareSnapshotService.Save(snapshotPath, snapshot);
        var output = new StringWriter();

        int exit = CliService.Run(["--enterprise-baseline", policyPath,
                "--trusted-key", keyPath, "--snapshot", snapshotPath],
            () => [], output, new StringWriter());

        Assert.Equal(CliService.ExitOk, exit);
        Assert.DoesNotContain(privateValue, output.ToString(), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal("enterprise-baseline", root.GetProperty("scope").GetString());
        Assert.Equal("Satisfied", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("policySignatureValid").GetBoolean());
        Assert.False(root.TryGetProperty("signatureValid", out _));
        Assert.Contains("未簽章、未做遠端證明", root.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("signerKeyId").GetString());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("satisfied").GetInt32());
    }

    [Fact]
    public void 不符合或缺資料_退出碼2且不給安全總分()
    {
        string policyPath = Write("policy.json", EnterpriseBaselinePackageService.Sign(
            new EnterpriseBaselinePolicy(1, "org-workstation", At.AddDays(-1), At.AddDays(1),
                [new("cpu.model", EnterpriseBaselineMatch.Exact, "Expected CPU"),
                 new("tpm.present", EnterpriseBaselineMatch.Exact, "是")]),
            PrivatePem, SigningPassword));
        string keyPath = Write("trusted-public.pem", _signer.ExportSubjectPublicKeyInfoPem());
        var snapshot = HardwareSnapshotService.Create("test",
            [Fact("cpu.model", "Different CPU")], includeSensitive: false);
        string snapshotPath = Path.Combine(_directory, "snapshot.json");
        HardwareSnapshotService.Save(snapshotPath, snapshot);
        var output = new StringWriter();

        int exit = CliService.Run(["--enterprise-baseline", policyPath,
                "--trusted-key", keyPath, "--snapshot", snapshotPath],
            () => [], output, new StringWriter());

        Assert.Equal(CliService.ExitPartial, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("NotSatisfied", document.RootElement.GetProperty("status").GetString());
        Assert.False(document.RootElement.TryGetProperty("score", out _));
        Assert.Equal(1, document.RootElement.GetProperty("counts").GetProperty("notSatisfied").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("counts").GetProperty("unverifiable").GetInt32());
    }

    [Fact]
    public void 缺少信任金鑰或快照路徑_不啟動評估()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        int exit = CliService.Run(["--enterprise-baseline", "policy.json"],
            () => [], output, error);

        Assert.Equal(CliService.ExitError, exit);
        Assert.Empty(output.ToString());
        Assert.Contains("--trusted-key", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void 管理端簽署命令_私鑰不進標準輸出且套件可供端點驗證()
    {
        string policyPath = Write("source-policy.json", JsonSerializer.Serialize(
            new EnterpriseBaselinePolicy(1, "org-workstation", At.AddDays(-1), At.AddDays(1),
                [new("cpu.model", EnterpriseBaselineMatch.Exact, "Approved CPU")]),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }));
        string privateKeyPath = Write("signing-private.pem", PrivatePem);
        string signedPackagePath = Path.Combine(_directory, "signed-policy.json");
        var output = new StringWriter();
        var error = new StringWriter();

        int exit = CliService.Run(["--enterprise-baseline-sign", policyPath,
                "--signing-key", privateKeyPath, "--password-stdin", "--out", signedPackagePath],
            () => [], output, error, stdin: new StringReader(SigningPassword));

        Assert.Equal(CliService.ExitOk, exit);
        Assert.True(File.Exists(signedPackagePath));
        Assert.Empty(output.ToString());
        Assert.Empty(error.ToString());
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(signedPackagePath), StringComparison.Ordinal);
    }

    [Fact]
    public void 簽署輸出不得覆寫政策或私鑰()
    {
        string policyPath = Write("source-policy.json", "{}");
        string privateKey = PrivatePem;
        string privateKeyPath = Write("signing-private.pem", privateKey);
        var error = new StringWriter();

        int exit = CliService.Run(["--enterprise-baseline-sign", policyPath,
                "--signing-key", privateKeyPath, "--password-stdin", "--out", privateKeyPath],
            () => [], new StringWriter(), error, stdin: new StringReader(SigningPassword));

        Assert.Equal(CliService.ExitError, exit);
        Assert.Equal(privateKey, File.ReadAllText(privateKeyPath));
        Assert.Contains("不得覆寫", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void 簽署命令不接受命令列密碼且需要stdin秘密()
    {
        string policyPath = Write("source-policy.json", "{}");
        string privateKeyPath = Write("signing-private.pem", PrivatePem);
        string outputPath = Path.Combine(_directory, "signed.json");
        var error = new StringWriter();

        int exit = CliService.Run(["--enterprise-baseline-sign", policyPath,
                "--signing-key", privateKeyPath, "--password-stdin", "--out", outputPath],
            () => [], new StringWriter(), error, stdin: null);

        Assert.Equal(CliService.ExitError, exit);
        Assert.False(File.Exists(outputPath));
        Assert.Contains("stdin", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidPolicyIsRejectedBeforeSnapshotValidation()
    {
        string policyPath = Write("policy.json", EnterpriseBaselinePackageService.Sign(
            new EnterpriseBaselinePolicy(1, "org-workstation", At.AddDays(-1), At.AddDays(1),
                [new("cpu.model", EnterpriseBaselineMatch.Exact, "Expected CPU")]),
            PrivatePem, SigningPassword));
        string keyPath = Write("trusted-public.pem", _signer.ExportSubjectPublicKeyInfoPem());
        string invalidSnapshotPath = Write("invalid-snapshot.json", "{}");
        string package = File.ReadAllText(policyPath);
        var packageNode = JsonNode.Parse(package)!.AsObject();
        string signature = packageNode["signature"]!.GetValue<string>();
        string alteredSignature = (signature[0] == 'A' ? "B" : "A") + signature[1..];
        packageNode["signature"] = alteredSignature;
        string invalidPackage = packageNode.ToJsonString();
        Assert.NotEqual(signature, JsonNode.Parse(invalidPackage)! .AsObject()["signature"]!.GetValue<string>());
        var verification = EnterpriseBaselinePackageService.Evaluate(invalidPackage,
            _signer.ExportSubjectPublicKeyInfoPem(), new Dictionary<string, HardwareFact>(), DateTimeOffset.UtcNow);
        Assert.False(verification.SignatureValid);
        File.WriteAllText(policyPath, invalidPackage);
        var output = new StringWriter();
        var error = new StringWriter();

        int exit = CliService.Run(["--enterprise-baseline", policyPath,
                "--trusted-key", keyPath, "--snapshot", invalidSnapshotPath],
            () => [], output, error);

        Assert.Equal(CliService.ExitError, exit);
        Assert.Empty(error.ToString());
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal("Rejected", report.RootElement.GetProperty("status").GetString());
        Assert.False(report.RootElement.GetProperty("policySignatureValid").GetBoolean());
    }

    [Fact]
    public void OversizedPolicyAndKeyFilesAreRejectedBeforeParsing()
    {
        string keyPath = Write("trusted-public.pem", _signer.ExportSubjectPublicKeyInfoPem());
        string snapshotPath = Write("invalid-snapshot.json", "{}");
        string oversizedPolicyPath = Write("oversized-policy.json",
            new string('x', EnterpriseBaselinePackageService.MaxPackageBytes + 1));
        var evalOutput = new StringWriter();
        var evalError = new StringWriter();

        int evalExit = CliService.Run(["--enterprise-baseline", oversizedPolicyPath,
                "--trusted-key", keyPath, "--snapshot", snapshotPath],
            () => [], evalOutput, evalError);

        Assert.Equal(CliService.ExitError, evalExit);
        Assert.Empty(evalOutput.ToString());
        Assert.Contains("InvalidDataException", evalError.ToString(), StringComparison.Ordinal);

        string policyPath = Write("small-policy.json", "{}");
        string oversizedKeyPath = Write("oversized-private.pem",
            new string('x', EnterpriseBaselinePackageService.MaxKeyPemBytes + 1));
        string outputPath = Path.Combine(_directory, "signed.json");
        var signOutput = new StringWriter();
        var signError = new StringWriter();
        int signExit = CliService.Run(["--enterprise-baseline-sign", policyPath,
                "--signing-key", oversizedKeyPath, "--password-stdin", "--out", outputPath],
            () => [], signOutput, signError, stdin: new StringReader(SigningPassword));

        Assert.Equal(CliService.ExitError, signExit);
        Assert.Empty(signOutput.ToString());
        Assert.False(File.Exists(outputPath));
        Assert.Contains("InvalidDataException", signError.ToString(), StringComparison.Ordinal);
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static HardwareFact Fact(string key, string value) => new(
        key, "測試", key, value, "", "fixture", FactTrustLevel.Reported,
        false, At, null);

    public void Dispose()
    {
        _signer.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
