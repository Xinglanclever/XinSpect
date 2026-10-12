using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 推進第 113–122 項（生命週期／報告／治理補充）的守門：全部注入假件。
/// 紅線：生物特徵與憑證內容不讀；DHA 未施測不假裝；查詢歷史不落盤。
/// </summary>
public class LifecycleAndGovernanceFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 16, 0, 0, TimeSpan.Zero);

    private static HardwareFact F(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static HardwareFact Fact(string key, string value, double? numeric = null,
        FactAvailability availability = FactAvailability.Present) =>
        new(key, "測試域", key, value, "", "測試", FactTrustLevel.Reported, false, At, numeric, availability,
            availability == FactAvailability.Present ? null : "原因");

    // ── EL-002 Office ───────────────────────────────────────────────────

    [Fact]
    public void Office_有版本_報版本與通道_不做EOL判定()
    {
        var f = LifecycleAndReportsFactsService.OfficeFact(At,
            (p, v) => v == "VersionToReport" ? "16.0.17928.20114" : (v == "CDNBaseUrl" ? "http://officecdn.microsoft.com" : null));

        Assert.Contains("16.0.17928.20114", f.Value);
        Assert.Contains("officecdn.microsoft.com", f.Value);
        Assert.Contains("不做 EOL 判定", f.Value);
    }

    [Fact]
    public void Office_未安裝_標不適用不是0()
    {
        var f = F(LifecycleAndReportsFactsService.Collect(At, (p, v) => null), LifecycleAndReportsFactsService.OfficeKey);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("未安裝 Office 不是錯誤", f.UnavailableReason);
    }

    // ── EL-003 瀏覽器 ───────────────────────────────────────────────────

    [Fact]
    public void 瀏覽器_多個版本_逐個列出()
    {
        var f = LifecycleAndReportsFactsService.BrowserFact(At, (p, v) => p switch
        {
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}" => "120.0.2210.61",
            @"SOFTWARE\Google\Update\Clients\{8A69D345-D564-463C-AFF1-A69D9E530F96}" => "121.0.6167.85",
            _ => null,
        });

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("Edge 120.0.2210.61", f.Value);
        Assert.Contains("Chrome 121.0.6167.85", f.Value);
        Assert.Contains("不列使用者設定或瀏覽記錄", f.Value);
    }

    [Fact]
    public void 瀏覽器_沒有_標不適用()
    {
        var f = F(LifecycleAndReportsFactsService.Collect(At, (p, v) => null), LifecycleAndReportsFactsService.BrowserKey);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    // ── RS-008 CSV ──────────────────────────────────────────────────────

    [Fact]
    public async Task CSV_標頭固定_含逗號的值會轉義_敏感值遮蔽()
    {
        var facts = new List<HardwareFact>
        {
            new("cpu.name", "測試域", "cpu.name", "Intel, 1234", "", "s", FactTrustLevel.Reported, false, At),
            new("board.serial", "測試域", "board.serial", "SECRET", "", "s", FactTrustLevel.Reported, true, At),
        };
        string path = Path.Combine(Path.GetTempPath(), "xincsv-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            await LifecycleAndReportsFactsService.WriteCsvAsync(path, facts);
            string[] lines = File.ReadAllLines(path);

            Assert.Equal("key,category,name,value,unit,trust,availability,sensitive,measured_at_utc", lines[0]);
            Assert.Contains("\"Intel, 1234\"", lines[1]);
            Assert.Contains("（敏感值已遮蔽）", lines[2]);
            Assert.Equal(3, lines.Length);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task CSV_引號本身也轉義()
    {
        var facts = new List<HardwareFact>
        {
            new("q", "測試域", "q", "has \"quote\"", "", "s", FactTrustLevel.Reported, false, At),
        };
        string path = Path.Combine(Path.GetTempPath(), "xincsv-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            await LifecycleAndReportsFactsService.WriteCsvAsync(path, facts);
            Assert.Contains("\"has \"\"quote\"\"\"", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── RS-009 diff JSON ────────────────────────────────────────────────

    [Fact]
    public void DiffJSON_可解析且計數正確()
    {
        var diff = new HardwareSnapshotDiff
        {
            BeforeMachineId = "M", AfterMachineId = "M",
            BeforeCapturedAtUtc = At.AddMonths(-1), AfterCapturedAtUtc = At,
            Changes =
            [
                new HardwareFactChange { Key = "new.1", Kind = SnapshotChangeKind.Added },
                new HardwareFactChange { Key = "same.1", Kind = SnapshotChangeKind.Unchanged },
            ],
        };
        var json = LifecycleAndReportsFactsService.SerializeDiffJson(diff);

        var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("sameMachine").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("added").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("unchanged").GetInt32());
    }

    [Fact]
    public void DiffJSON_描述_講明需要先比較()
    {
        var f = F(LifecycleAndReportsFactsService.Collect(At, (p, v) => null), LifecycleAndReportsFactsService.DiffJsonKey);
        Assert.Contains("只在做過快照比較時有內容", f.Value);
    }

    // ── RS-011 報告版本比較 ─────────────────────────────────────────────

    [Fact]
    public void 報告版本比較_收口DiffKeySets_沒有兩份標不適用()
    {
        var withData = LifecycleAndReportsFactsService.ReportDiffFact(At, new[] { "a" }, new[] { "a", "b" });
        Assert.Equal(1, withData.NumericValue);
        Assert.Contains("新增：b", withData.Value);

        var none = F(LifecycleAndReportsFactsService.Collect(At, (p, v) => null), LifecycleAndReportsFactsService.ReportDiffKey);
        Assert.Equal(FactAvailability.NotApplicable, none.Availability);
    }

    // ── PL-010 版本公告草稿 ─────────────────────────────────────────────

    [Fact]
    public void 公告草稿_由Changelog產生_聲明草稿非發佈()
    {
        string draft = LifecycleAndReportsFactsService.BuildReleaseNoteDraft("2.71");

        Assert.Contains("曦覽 XinSpect 2.71", draft);
        Assert.Contains("草稿非發佈", draft);
    }

    [Fact]
    public void 公告草稿_描述鍵存在()
    {
        var f = F(LifecycleAndReportsFactsService.Collect(At, (p, v) => null), LifecycleAndReportsFactsService.ReleaseDraftKey);
        Assert.Contains("不自動發佈", f.Value);
    }

    // ── DS-002 DHA ──────────────────────────────────────────────────────

    [Fact]
    public void DHA_未施測聲明_不假裝施測()
    {
        var facts = GovernanceExtraFactsService.Collect(At, events: _ => [], pnp: () => [], previousProblems: null);
        var f = F(facts, GovernanceExtraFactsService.DhaKey);

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("未施測", f.UnavailableReason);
        Assert.Contains("不假裝施測", f.UnavailableReason);
        Assert.Contains("不產生假的健康憑證", f.Value);
    }

    // ── DM-001 維護狀態史 ───────────────────────────────────────────────

    [Fact]
    public void 維護史_事件分組_只計維護相關()
    {
        var f = GovernanceExtraFactsService.MaintFact(At, _ => new List<(DateTimeOffset, int, string)>
        {
            (At.AddHours(-1), 201, "\\Microsoft\\Windows\\TaskScheduler\\Maintenance Configurator"),
            (At.AddHours(-2), 200, "\\Microsoft\\Windows\\Update\\AutomaticApp Update"),
            (At.AddHours(-3), 201, "\\Microsoft\\Windows\\TaskScheduler\\Maintenance Configurator"),
        });

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("完成 2 筆", f.Value);
    }

    [Fact]
    public void 維護史_通道不存在_NotSupported()
    {
        var f = GovernanceExtraFactsService.MaintFact(At, _ => null);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("不等於「維護沒跑過」", f.UnavailableReason);
    }

    // ── PL-005 綠色零寫入 ───────────────────────────────────────────────

    [Fact]
    public void 零寫入邊界_講明哪些寫入是允許的()
    {
        var f = GovernanceExtraFactsService.PortableFact(At);

        Assert.Contains("不寫任何檔案", f.Value);
        Assert.Contains("%APPDATA%", f.Value);
        Assert.Contains("守門", f.Value);
    }

    [Fact]
    public void 零寫入邊界_對應測試確實存在()
    {
        string dir = "";
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "XinSpect.csproj"))) d = d.Parent;
        Assert.NotNull(d);
        dir = d!.FullName;
        string tests = Path.Combine(dir, "Tests");
        bool hasGate = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Any(file => File.ReadAllText(file).Contains("禁止對系統目錄與其他使用者設定檔的寫入"));
        Assert.True(hasGate, "零寫入邊界的守門測試不見了——這條紅線不能只靠文字");
    }

    // ── HM-002 錯誤碼變更 ───────────────────────────────────────────────

    [Fact]
    public void 錯誤碼變更_沒上一份_標不適用()
    {
        var f = GovernanceExtraFactsService.ProblemDeltaFact(At, () => [], null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不做連續事件史", f.UnavailableReason);
    }

    [Fact]
    public void 錯誤碼變更_新出現消失與改變分開()
    {
        var f = GovernanceExtraFactsService.ProblemDeltaFact(At,
            () => new List<(string, uint)> { ("A", 10), ("B", 0) },
            new List<(string, uint)> { ("A", 0), ("C", 12) });

        Assert.Equal(3, f.NumericValue);
        Assert.Contains("A（0→10）", f.Value);
    }

    [Fact]
    public void 錯誤碼變更_列舉失敗_ReadError()
    {
        var f = GovernanceExtraFactsService.ProblemDeltaFact(At, () => null, []);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 整組契約 ────────────────────────────────────────────────────────

    [Fact]
    public void 治理補充_四把鍵齊()
    {
        var facts = GovernanceExtraFactsService.Collect(At, events: _ => [], pnp: () => [], previousProblems: []);
        string[] expected =
        [
            GovernanceExtraFactsService.DhaKey, GovernanceExtraFactsService.MaintKey,
            GovernanceExtraFactsService.PortableKey, GovernanceExtraFactsService.ProblemDeltaKey,
        ];
        foreach (string key in expected) Assert.Contains(facts, f => f.Key == key);
    }
}

