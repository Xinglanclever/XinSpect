using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// CLI 的兩條新命令：<c>--integrity-baseline</c>（本工具唯一寫入自身基線的動作）與
/// <c>--sbom</c>（CycloneDX 匯出）。退出碼語意與既有命令一致：0／2／1。
/// </summary>
public class CliIntegrityAndSbomTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = CliService.Run(args, () => [], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    // ── --integrity-baseline ─────────────────────────────────────────────

    [Fact]
    public void 基線命令_寫入審計日誌並回報已記錄且鏈可驗()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xincli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string audit = Path.Combine(dir, "audit.json");
        try
        {
            var (exit, stdout, _) = Run(CliService.IntegrityBaselineArg, audit);

            Assert.Equal(CliService.ExitOk, exit);
            using var json = JsonDocument.Parse(stdout);
            Assert.True(json.RootElement.GetProperty("written").GetBoolean());
            Assert.Equal(audit, json.RootElement.GetProperty("path").GetString());
            Assert.Contains("鏈雜湊可驗", json.RootElement.GetProperty("summary").GetString());
            Assert.Contains("不是防篡改保證", json.RootElement.GetProperty("note").GetString());
            Assert.True(AuditVerifier.VerifyFile(audit).Valid);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void 基線命令_寫不進去時退出碼是2且原因說得出來()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xincli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string blocker = Path.Combine(dir, "blocker");
        File.WriteAllText(blocker, "x");
        try
        {
            var (exit, stdout, stderr) = Run(CliService.IntegrityBaselineArg, Path.Combine(blocker, "audit.json"));

            Assert.Equal(CliService.ExitPartial, exit);
            using var json = JsonDocument.Parse(stdout);
            Assert.False(json.RootElement.GetProperty("written").GetBoolean());
            Assert.Contains("基線沒建立", stderr);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ── --sbom ───────────────────────────────────────────────────────────

    [Fact]
    public void SBOM命令_輸出合法的CycloneDX文件()
    {
        string path = Path.Combine(Path.GetTempPath(), "sbom-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var (exit, _, _) = Run(CliService.SbomArg, CliService.OutArg, path);

            // 驅動清單讀不到時退出碼 2（部分元件）——兩種都不算失敗，但內容必須是合法文件
            Assert.Contains(exit, new[] { CliService.ExitOk, CliService.ExitPartial });
            Assert.True(File.Exists(path));

            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("CycloneDX", json.RootElement.GetProperty("bomFormat").GetString());
            Assert.Equal("1.5", json.RootElement.GetProperty("specVersion").GetString());
            Assert.False(string.IsNullOrWhiteSpace(
                json.RootElement.GetProperty("metadata").GetProperty("component").GetProperty("name").GetString()));
            Assert.True(json.RootElement.GetProperty("components").GetArrayLength() >= 1);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void 說明文字要列出兩條新命令()
    {
        var (exit, stdout, _) = Run(CliService.HelpArg);

        Assert.Equal(CliService.ExitOk, exit);
        Assert.Contains(CliService.IntegrityBaselineArg, stdout);
        Assert.Contains(CliService.SbomArg, stdout);
        Assert.Contains("CycloneDX", stdout);
    }
}
