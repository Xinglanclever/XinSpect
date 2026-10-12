using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 第 143–152 項：電源／恢復、開發者互通與資料生命週期的守門。
/// 紅線：零網路、唯讀、別名只指向真實鍵、未設定不用 Windows 預設代填。
/// </summary>
public class RecoveryPowerAndInteropFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    // ── RC-013／RC-014 休眠與快速啟動 ───────────────────────────────────────

    [Fact]
    public void 休眠檔_存在時報大小與登錄狀態()
    {
        var f = RecoveryPowerAndInteropFactsService.HibernationFact(At,
            () => new(true, 8L * 1024 * 1024 * 1024, 1));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("8192.0 MiB", f.Value);
        Assert.Contains("登錄啟用", f.Value);
    }

    [Fact]
    public void 休眠檔_未見且登錄停用_標不適用不是0()
    {
        var f = RecoveryPowerAndInteropFactsService.HibernationFact(At, () => new(false, null, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("未見", f.Value);
        Assert.Equal("", f.UnavailableReason?.Contains("0 位元組") == true ? "壞" : "");
    }

    [Fact]
    public void 休眠探測失敗_ReadError()
    {
        var f = RecoveryPowerAndInteropFactsService.HibernationFact(At, () => throw new IOException("x"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void 快速啟動_一二三態不代填預設()
    {
        Assert.Contains("已啟用", RecoveryPowerAndInteropFactsService.FastStartupFact(At, () => new(false, null, 1)).Value);
        Assert.Contains("已停用", RecoveryPowerAndInteropFactsService.FastStartupFact(At, () => new(false, null, 0)).Value);
        var unknown = RecoveryPowerAndInteropFactsService.FastStartupFact(At, () => new(false, null, null));
        Assert.Equal(FactAvailability.Unknown, unknown.Availability);
        Assert.Contains("不能代填", unknown.UnavailableReason);
    }

    // ── PW-002 USB 選擇性暫停 ───────────────────────────────────────────────

    [Fact]
    public void USB暫停_AC_DC分開陳述()
    {
        var f = RecoveryPowerAndInteropFactsService.UsbSuspendFact(At, () => new(1, 0));
        Assert.Contains("AC=啟用", f.Value);
        Assert.Contains("DC=停用", f.Value);
    }

    [Fact]
    public void USB暫停_沒有設定索引_Unknown()
    {
        var f = RecoveryPowerAndInteropFactsService.UsbSuspendFact(At, () => new(null, null));
        Assert.Equal(FactAvailability.Unknown, f.Availability);
    }

    [Fact]
    public void USB暫停_探測失敗_ReadError()
    {
        var f = RecoveryPowerAndInteropFactsService.UsbSuspendFact(At, () => throw new IOException("x"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── PW-007 停機時長 ─────────────────────────────────────────────────────

    [Fact]
    public void 停機時長_配對6006與6005()
    {
        var f = RecoveryPowerAndInteropFactsService.ShutdownSummaryFact(At, () =>
        [
            new(At.AddHours(-10), 6006),
            new(At.AddHours(-9), 6005),
            new(At.AddHours(-2), 6006),
            new(At.AddHours(-1), 6005),
        ]);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("60", f.Value);
        Assert.Contains("2 段", f.Value);
    }

    [Fact]
    public void 停機時長_只有單邊事件_NotApplicable()
    {
        var f = RecoveryPowerAndInteropFactsService.ShutdownSummaryFact(At, () => [new(At, 6006)]);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不是 0 分鐘", f.UnavailableReason);
    }

    [Fact]
    public void 停機時長_查詢失敗_ReadError()
    {
        var f = RecoveryPowerAndInteropFactsService.ShutdownSummaryFact(At, () => throw new IOException("x"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── IE-002 Grafana ──────────────────────────────────────────────────────

    [Fact]
    public void Grafana匯出_JSON有面板且不內嵌值()
    {
        string json = RecoveryPowerAndInteropFactsService.BuildGrafanaDashboard(["cpu.generation", "cpu.tjmax"]);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("曦覽 XinSpect", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("panels").GetArrayLength());
        Assert.Contains("不內嵌本機值", doc.RootElement.GetProperty("boundary").GetString());
        Assert.DoesNotContain("C:\\", json);
    }

    [Fact]
    public void Grafana匯出_零網路()
    {
        string json = RecoveryPowerAndInteropFactsService.BuildGrafanaDashboard(["cpu.generation"]);
        Assert.DoesNotContain("http://", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── IE-005 PowerShell 範本 ─────────────────────────────────────────────

    [Fact]
    public void PowerShell範本_只呼叫本機CLI且引號安全()
    {
        string script = RecoveryPowerAndInteropFactsService.GeneratePowerShellTemplate("cpu.", @"C:\temp\o'hara.json");
        Assert.Contains("--json evidence --query 'cpu.'", script);
        Assert.Contains("--out", script);
        Assert.Contains("'C:\\temp\\o''hara.json'", script);
        Assert.DoesNotContain("Invoke-WebRequest", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("curl", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShell範本_說明退出碼()
    {
        string script = RecoveryPowerAndInteropFactsService.GeneratePowerShellTemplate("tpm.", "x.json");
        Assert.Contains("$LASTEXITCODE -eq 2", script);
        Assert.Contains("部分事實讀不到", script);
    }

    // ── IE-006 deep link ────────────────────────────────────────────────────

    [Fact]
    public void DeepLink_中文與鍵都轉義_只產生不啟動()
    {
        string link = RecoveryPowerAndInteropFactsService.BuildDeepLink("總覽/正在發生什麼事", "cpu.generation");
        Assert.StartsWith("xinspect://", link);
        Assert.Contains("%E7%B8%BD%E8%A6%BD", link);
        Assert.Contains("key=cpu.generation", link);
    }

    [Fact]
    public void DeepLink_可只帶頁面()
    {
        Assert.Equal("xinspect://about", RecoveryPowerAndInteropFactsService.BuildDeepLink("about"));
    }

    // ── IE-007 參數補完 ─────────────────────────────────────────────────────

    [Fact]
    public void 參數補完_包含stdin批次且不寫profile()
    {
        string script = RecoveryPowerAndInteropFactsService.GeneratePowerShellCompleter();
        Assert.Contains("Register-ArgumentCompleter", script);
        Assert.Contains(CliService.StdinBatchArg, script);
        Assert.DoesNotContain("Out-File", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$PROFILE", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 參數補完_沒有網路請求字樣()
    {
        string script = RecoveryPowerAndInteropFactsService.GeneratePowerShellCompleter();
        Assert.DoesNotContain("http://", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", script, StringComparison.OrdinalIgnoreCase);
    }

    // ── DK-003 別名 ────────────────────────────────────────────────────────

    [Fact]
    public void 別名_已知別名指向真實固定鍵()
    {
        foreach (string alias in new[] { "cpu", "gpu", "boot", "tpm", "secureboot", "net", "audio", "storage", "printer" })
        {
            string? key = RecoveryPowerAndInteropFactsService.ResolveAlias(alias);
            Assert.NotNull(key);
            Assert.Contains(key, FactKeyCatalog.Keys);
        }
    }

    [Fact]
    public void 別名_未知名稱回null_不猜()
    {
        Assert.Null(RecoveryPowerAndInteropFactsService.ResolveAlias("definitely-not-a-real-alias"));
    }

    // ── DK-008 快照期限 ─────────────────────────────────────────────────────

    [Fact]
    public void 快照期限_新鮮_提醒_過期三態()
    {
        Assert.Contains("新鮮", RecoveryPowerAndInteropFactsService.SnapshotAgeLabel(At.AddDays(-3), At));
        Assert.Contains("提醒", RecoveryPowerAndInteropFactsService.SnapshotAgeLabel(At.AddDays(-60), At));
        Assert.Contains("過期", RecoveryPowerAndInteropFactsService.SnapshotAgeLabel(At.AddDays(-200), At));
    }

    [Fact]
    public void 快照期限_時間戳晚於現在不判新鮮()
    {
        Assert.Contains("時鐘異常", RecoveryPowerAndInteropFactsService.SnapshotAgeLabel(At.AddHours(1), At));
    }

    [Fact]
    public void 快照期限_門檻順序錯直接拒絕()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RecoveryPowerAndInteropFactsService.SnapshotAgeLabel(At, At, warnDays: 90, expireDays: 30));
    }

    // ── 整組契約 ────────────────────────────────────────────────────────────

    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = RecoveryPowerAndInteropFactsService.Collect(At);
        string[] expected =
        [
            RecoveryPowerAndInteropFactsService.HibernateKey, RecoveryPowerAndInteropFactsService.FastStartupKey,
            RecoveryPowerAndInteropFactsService.UsbSuspendKey, RecoveryPowerAndInteropFactsService.ShutdownKey,
            RecoveryPowerAndInteropFactsService.GrafanaKey, RecoveryPowerAndInteropFactsService.PowerShellTemplateKey,
            RecoveryPowerAndInteropFactsService.DeepLinkKey, RecoveryPowerAndInteropFactsService.CompletionKey,
            RecoveryPowerAndInteropFactsService.AliasKey, RecoveryPowerAndInteropFactsService.SnapshotAgeKey,
        ];
        foreach (string key in expected) Assert.Contains(facts, f => f.Key == key);
        Assert.Equal(10, facts.Count);
    }
}
