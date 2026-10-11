using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 維護與裝置健康事實組（Vol2 推進第 32–41 項）的守門：全部走注入假件——
/// 真實通路碰 WMI／事件記錄／登錄，單元測試讀真的會讓結果取決於機器；
/// 修復動作（IN-008）只有往返測試寫進暫存目錄。
/// </summary>
public class MaintenanceFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 5, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static IReadOnlyList<HardwareFact> Collect(
        MaintenanceFactsService.PnpProbe? pnp = null,
        MaintenanceFactsService.EventProbe? bits = null,
        MaintenanceFactsService.EventProbe? msi = null,
        MaintenanceFactsService.RegistryProbe? registry = null,
        MaintenanceFactsService.ConfigState? config = null,
        IReadOnlyList<AuditEntry>? audit = null)
        => MaintenanceFactsService.Collect(At, pnp, bits, msi, registry, config, audit);

    // ── IN-008 設定自我修復 ──────────────────────────────────────────────

    [Fact]
    public void 設定不存在_標不適用而不是錯誤()
    {
        var f = Fact(Collect(config: new MaintenanceFactsService.ConfigState(@"C:\x\settings.json", false, false, null)),
            MaintenanceFactsService.CfgRepairKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Equal("", f.Value);
        Assert.Contains("不存在", f.UnavailableReason);
    }

    [Fact]
    public void 設定合法_說無需修復()
    {
        var f = Fact(Collect(config: new MaintenanceFactsService.ConfigState(@"C:\x\settings.json", true, true, null)),
            MaintenanceFactsService.CfgRepairKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("無需修復", f.Value);
    }

    [Fact]
    public void 設定損毀_標Unknown並說明修復要先備份()
    {
        var f = Fact(Collect(config: new MaintenanceFactsService.ConfigState(@"C:\x\settings.json", true, false, "Unexpected character")),
            MaintenanceFactsService.CfgRepairKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("損毀", f.Value);
        Assert.Contains("備份", f.Value);
        Assert.Contains("明示觸發", f.Value);
    }

    [Fact]
    public void 修復合法設定_拒絕且不重寫()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinmaint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(file, "{\"k\":1}");
            long size = new FileInfo(file).Length;

            var (done, _, failure) = MaintenanceFactsService.RepairConfig(file);

            Assert.False(done);
            Assert.Contains("不需要修復", failure ?? "");
            Assert.Equal(size, new FileInfo(file).Length);   // 不做無謂重寫
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 修復損毀設定_先留證再重建且重建可解析()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinmaint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(file, "definitely-not-json");

            var (done, summary, failure) = MaintenanceFactsService.RepairConfig(file);

            Assert.True(done, failure ?? "應成功");
            Assert.Contains("留證", summary);
            // 原檔的損毀內容必須還在（改名備份），不是被刪掉
            string backup = Directory.GetFiles(dir, "settings.json.corrupt-*").Single();
            Assert.Contains("definitely-not-json", File.ReadAllText(backup));
            // 重建後的檔案必須是合法 JSON，且服務再讀一次要說合法
            var after = MaintenanceFactsService.ReadConfigState(file);
            Assert.True(after.JsonValid);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 修復損毀設定時備份名衝突_失敗且原檔不動()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinmaint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(file, "definitely-not-json");
            // 先把「下一個時間戳備份名」占住：直接造一個同名檔
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(dir, $"settings.json.corrupt-{stamp}"), "evidence");

            var (done, _, failure) = MaintenanceFactsService.RepairConfig(file);

            Assert.False(done);
            Assert.Contains("未完成", failure ?? "");
            Assert.Contains("definitely-not-json", File.ReadAllText(file));   // 原檔保持原狀
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 修復不存在的設定_拒絕且不建立()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinmaint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "settings.json");
        try
        {
            var (done, _, failure) = MaintenanceFactsService.RepairConfig(file);
            Assert.False(done);
            Assert.Contains("不存在", failure ?? "");
            Assert.False(File.Exists(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── NG-003 進度記憶 ─────────────────────────────────────────────────

    [Fact]
    public void 進度記憶_審計為空_標不適用並說明唯讀不進鏈()
    {
        var f = Fact(Collect(audit: []), MaintenanceFactsService.ProgressKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("唯讀", f.UnavailableReason);
    }

    [Fact]
    public void 進度記憶_有審計記錄_報範圍計數與最近一筆()
    {
        var audit = new List<AuditEntry>
        {
            AuditLogService.Append([], "op", "machine", "動作A", "scope-a", "結果", "hash-a", At),
            AuditLogService.Append([], "op", "machine", "動作B", "scope-b", "結果", "hash-b", At.AddMinutes(1)),
            AuditLogService.Append([], "op", "machine", "動作C", "scope-a", "結果", "hash-c", At.AddMinutes(2)),
        };
        var f = Fact(Collect(audit: audit), MaintenanceFactsService.ProgressKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("3 筆", f.Value);
        Assert.Contains("2 個範圍", f.Value);
        Assert.Contains("scope-a×2", f.Value);
        Assert.Contains("動作C", f.Value);
        Assert.Contains("不是「硬體好不好」", f.Value);
    }

    // ── NG-004 報告整合 ─────────────────────────────────────────────────

    [Fact]
    public void 報告整合_空事實集_標Unknown而不是一切都好()
    {
        var f = MaintenanceFactsService.ReportFact(At, []);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("還沒收集", f.UnavailableReason);
    }

    [Fact]
    public void 報告整合_分布如實_矛盾單獨計數()
    {
        var facts = new List<HardwareFact>
        {
            new("a", "測試", "a", "v", "", "s", FactTrustLevel.Reported, false, At),
            new("b", "測試", "b", "", "", "s", FactTrustLevel.Unknown, false, At, null,
                FactAvailability.NotSupported, "原因"),
            new("c", "測試", "c", "", "", "s", FactTrustLevel.Unknown, false, At, null,
                FactAvailability.ReadError, "原因"),
            new("reconcile.x", "交叉對帳", "x", "矛盾：原因", "", "s", FactTrustLevel.Derived, false, At),
        };
        var f = MaintenanceFactsService.ReportFact(At, facts);

        Assert.Contains("已收集 4 項", f.Value);
        // 矛盾卡本身是一條 Present 的「結論事實」，所以可採信 2 項（事實 a ＋結論卡）
        Assert.Contains("可採信 2 項", f.Value);
        Assert.Contains("NotSupported 1 項", f.Value);
        Assert.Contains("ReadError 1 項", f.Value);
        Assert.Contains("矛盾 1 條", f.Value);
        Assert.Contains("不把未知讀成正常", f.Value);
    }

    // ── HM-001／002／008 裝置健康 ───────────────────────────────────────

    private static IReadOnlyList<MaintenanceFactsService.PnpDevice> FakePnp()
    {
        uint cmProbNotConnected = 45;
        return new List<MaintenanceFactsService.PnpDevice>
        {
            new("Intel(R) Core(TM) i7", 0),
            new("Unknown Device", 28),           // 缺驅動
            new("某些裝置", 10),                  // 無法啟動
            new("幽靈裝置", cmProbNotConnected),  // 不在場（45）
            new("未知裝置", 0),                   // 名稱未知但無錯誤
        };
    }

    [Fact]
    public void 裝置健康_總覽與未知數如實_幽靈裝置不算在場()
    {
        var facts = Collect(pnp: FakePnp);

        var tree = Fact(facts, MaintenanceFactsService.TreeKey);
        Assert.Equal(5, tree.NumericValue);
        Assert.Contains("在場 4", tree.Value);

        var unknownCount = Fact(facts, MaintenanceFactsService.UnknownKey);
        Assert.Equal(2, unknownCount.NumericValue);
        Assert.Contains("缺驅動≠故障", unknownCount.Value);

        var unknownList = Fact(facts, MaintenanceFactsService.UnknownListKey);
        Assert.Contains("Unknown Device", unknownList.Value);
        Assert.Contains("未知裝置", unknownList.Value);
        Assert.DoesNotContain("幽靈裝置", unknownList.Value);
    }

    [Fact]
    public void 裝置健康_錯誤碼分組計數_只算在場裝置()
    {
        var f = Fact(Collect(pnp: FakePnp), MaintenanceFactsService.ProbKey);

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("代碼 28×1", f.Value);
        Assert.Contains("代碼 10×1", f.Value);
        Assert.Contains("不是故障判決", f.Value);
    }

    [Fact]
    public void 裝置健康_列舉拋例外_整組ReadError()
    {
        var facts = Collect(pnp: () => throw new InvalidOperationException("WMI down"));

        foreach (string key in new[] { MaintenanceFactsService.TreeKey, MaintenanceFactsService.UnknownKey,
                     MaintenanceFactsService.UnknownListKey, MaintenanceFactsService.ProbKey })
        {
            var f = Fact(facts, key);
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason));
        }
    }

    // ── FS-008／FS-011 事件窗 ───────────────────────────────────────────

    [Fact]
    public void BITS_通道不存在_標NotSupported且不讀成零()
    {
        var facts = Collect(bits: (channel, days) => null);

        var c = Fact(facts, MaintenanceFactsService.BitsCountKey);
        Assert.Equal(FactAvailability.NotSupported, c.Availability);
        Assert.Contains("沒有這個記錄通道", c.UnavailableReason);
    }

    [Fact]
    public void BITS_有事件_按標準事件碼分組()
    {
        var events = new List<MaintenanceFactsService.TraceEvent>
        {
            new(At.AddMinutes(-1), 3, "(BITS 工作)"),
            new(At.AddMinutes(-2), 59, "(BITS 工作)"),
            new(At.AddMinutes(-3), 59, "(BITS 工作)"),
            new(At.AddMinutes(-4), 16, "(BITS 工作)"),
        };
        var facts = Collect(bits: (channel, days) => events);

        var c = Fact(facts, MaintenanceFactsService.BitsCountKey);
        Assert.Equal(4, c.NumericValue);
        var s = Fact(facts, MaintenanceFactsService.BitsSummaryKey);
        Assert.Contains("開始 1", s.Value);
        Assert.Contains("完成 2", s.Value);
        Assert.Contains("錯誤 1", s.Value);
        Assert.Contains("不讀下載內容", s.Value);
    }

    [Fact]
    public void BITS_查詢拋例外_標ReadError()
    {
        var facts = Collect(bits: (channel, days) => throw new EventLogException("down"));
        var c = Fact(facts, MaintenanceFactsService.BitsCountKey);
        Assert.Equal(FactAvailability.ReadError, c.Availability);
    }

    [Fact]
    public void 安裝痕跡_產品名是敏感值_事件碼分組如實()
    {
        var events = new List<MaintenanceFactsService.TraceEvent>
        {
            new(At.AddHours(-1), 1033, "SomeProduct 1.0"),
            new(At.AddHours(-2), 1034, "OldProduct"),
            new(At.AddHours(-3), 1033, "{AB3F-...}"),
        };
        var facts = Collect(msi: (channel, days) => events);

        var c = Fact(facts, MaintenanceFactsService.InstCountKey);
        Assert.Equal(3, c.NumericValue);
        var s = Fact(facts, MaintenanceFactsService.InstSummaryKey);
        Assert.True(s.Sensitive, "產品名稱屬軟體清單（L1）——摘要必須標敏感讓匯出遮蔽");
        Assert.Contains("安裝 2", s.Value);
        Assert.Contains("移除 1", s.Value);
        Assert.Contains("(產品 GUID，名稱未提供)", s.Value);
        Assert.Contains("痕跡不是現況", s.Value);
    }

    [Fact]
    public void 安裝痕跡_通道查不到_標NotSupported不讀成沒裝過()
    {
        var facts = Collect(msi: (channel, days) => null);
        var c = Fact(facts, MaintenanceFactsService.InstCountKey);
        Assert.Equal(FactAvailability.NotSupported, c.Availability);
        Assert.Contains("不等於沒裝過", c.UnavailableReason);
    }

    // ── UF-006／009 ─────────────────────────────────────────────────────

    [Fact]
    public void 更新來源_預設公用WindowsUpdate()
    {
        var f = Fact(Collect(registry: name => null), MaintenanceFactsService.SourceKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("公用服務", f.Value);
        Assert.Contains("不講「有沒有待裝更新」", f.Value);
    }

    [Fact]
    public void 更新來源_WSUS與停用分開陳述()
    {
        var wsus = Fact(Collect(registry: name => name == "UseWUServer" ? 1 : null),
            MaintenanceFactsService.SourceKey);
        Assert.Contains("WSUS", wsus.Value);

        var blocked = Fact(Collect(registry: name => name == "DisableWindowsUpdateAccess" ? 1 : null),
            MaintenanceFactsService.SourceKey);
        Assert.Equal(FactAvailability.NotApplicable, blocked.Availability);
        Assert.Contains("不是「沒有更新可用」", blocked.Value);
    }

    [Fact]
    public void 服務堆疊_讀不到標NotSupported_讀到報版本()
    {
        var missing = Fact(Collect(registry: _ => null), MaintenanceFactsService.ServicingKey);
        Assert.Equal(FactAvailability.NotSupported, missing.Availability);

        var ok = Fact(Collect(registry: name => name == "ServicingStackVersion" ? "10.0.26100.32692" : null),
            MaintenanceFactsService.ServicingKey);
        Assert.Equal(FactAvailability.Present, ok.Availability);
        Assert.Contains("10.0.26100.32692", ok.Value);
        Assert.Contains("不判「是不是最新」", ok.Value);
    }

    // ── 整組契約 ────────────────────────────────────────────────────────

    [Fact]
    public void 整組_每個事實鍵都出現且非Present都帶原因()
    {
        var facts = Collect(
            pnp: FakePnp,
            bits: (channel, days) => null,           // 通道不存在
            msi: (channel, days) => null,
            registry: _ => null,
            config: new MaintenanceFactsService.ConfigState(@"C:\x\settings.json", true, true, null),
            audit: []);

        string[] expected =
        [
            MaintenanceFactsService.CfgRepairKey,
            MaintenanceFactsService.ProgressKey,
            MaintenanceFactsService.TreeKey,
            MaintenanceFactsService.UnknownKey,
            MaintenanceFactsService.UnknownListKey,
            MaintenanceFactsService.ProbKey,
            MaintenanceFactsService.BitsCountKey,
            MaintenanceFactsService.BitsSummaryKey,
            MaintenanceFactsService.InstCountKey,
            MaintenanceFactsService.InstSummaryKey,
            MaintenanceFactsService.SourceKey,
            MaintenanceFactsService.ServicingKey,
        ];
        foreach (string key in expected)
        {
            var f = Fact(facts, key);
            if (f.Availability != FactAvailability.Present)
                Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason), $"{key} 缺三態原因");
        }
    }
}