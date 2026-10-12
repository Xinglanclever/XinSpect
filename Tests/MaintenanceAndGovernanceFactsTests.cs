using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 推進第 104–112 項（維護排程／BitLocker／查詢治理）的守門：全部注入假件。
/// 紅線：BitLocker 不讀金鑰；查詢歷史不落盤且標敏感；維護工作不觸發。
/// </summary>
public class MaintenanceAndGovernanceFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    private static HardwareFact F(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static IReadOnlyList<HardwareFact> Dm(
        MaintenanceDashboardFactsService.ValueProbe? values = null,
        MaintenanceDashboardFactsService.SubKeysProbe? subKeys = null,
        MaintenanceDashboardFactsService.TaskEventProbe? events = null)
        => MaintenanceDashboardFactsService.Collect(At, values, subKeys, events);

    // ── DM-002 Storage Sense ────────────────────────────────────────────

    [Fact]
    public void StorageSense_已啟用與未設定分開()
    {
        var on = F(Dm(values: (p, v) => v == "01" ? 1 : (v == "2048" ? 30 : null)), MaintenanceDashboardFactsService.SenseKey);
        Assert.Contains("已啟用", on.Value);

        var unset = F(Dm(values: (p, v) => null), MaintenanceDashboardFactsService.SenseKey);
        Assert.Equal(FactAvailability.NotApplicable, unset.Availability);
        Assert.Contains("0 是值", unset.Value);
    }

    [Fact]
    public void StorageSense_啟用但字串值也能判讀()
    {
        var f = F(Dm(values: (p, v) => v == "01" ? "1" : null), MaintenanceDashboardFactsService.SenseKey);
        Assert.Contains("已啟用", f.Value);
    }

    // ── DM-004 維護視窗 ─────────────────────────────────────────────────

    [Fact]
    public void 維護視窗_有覆寫列出_沒有標不適用()
    {
        var withWindow = F(Dm(values: (p, v) => v == "WakeUpTime" ? "02:00" : (v == "RandomDelay" ? "PT1H" : null)),
            MaintenanceDashboardFactsService.ConflictKey);
        Assert.Contains("WakeUpTime=02:00", withWindow.Value);
        Assert.Contains("只陳述視窗設定", withWindow.Value);

        var none = F(Dm(values: (p, v) => null), MaintenanceDashboardFactsService.ConflictKey);
        Assert.Equal(FactAvailability.NotApplicable, none.Availability);
    }

    // ── DM-005 清理處理器 ───────────────────────────────────────────────

    [Fact]
    public void 清理處理器_只數不執行()
    {
        var f = F(Dm(subKeys: _ => new List<string> { "Active Setup Temp Folders", "Downloaded Program Files", "Recycle Bin" }),
            MaintenanceDashboardFactsService.CleanKey);

        Assert.Equal(3, f.NumericValue);
        Assert.Contains("不執行清理", f.Value);
    }

    [Fact]
    public void 清理處理器_列舉失敗_ReadError()
    {
        Assert.Equal(FactAvailability.ReadError,
            F(Dm(subKeys: _ => throw new IOException("x")), MaintenanceDashboardFactsService.CleanKey).Availability);
    }

    // ── DM-006 任務運行時間線 ───────────────────────────────────────────

    [Fact]
    public void 任務時間線_200與201分開計數_痕跡不是現況()
    {
        var f = F(Dm(events: _ => new List<(DateTimeOffset, int, string)>
        {
            (At.AddHours(-1), 200, "\\Microsoft\\Windows\\TaskScheduler\\Maintenance"),
            (At.AddHours(-1), 201, "\\Microsoft\\Windows\\TaskScheduler\\Maintenance"),
            (At.AddHours(-2), 200, "\\Microsoft\\Windows\\TaskScheduler\\Maintenance"),
        }), MaintenanceDashboardFactsService.TimelineKey);

        Assert.Contains("開始 2", f.Value);
        Assert.Contains("完成 1", f.Value);
        Assert.Contains("痕跡不是現況", f.Value);
    }

    [Fact]
    public void 任務時間線_通道不存在_NotSupported()
    {
        var f = F(Dm(events: _ => null), MaintenanceDashboardFactsService.TimelineKey);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("不等於「沒有任務跑過」", f.UnavailableReason);
    }

    // ── DM-008 摘要 ─────────────────────────────────────────────────────

    [Fact]
    public void 維護摘要_分母不含自己且不是判決()
    {
        var facts = Dm(values: (p, v) => null, subKeys: _ => new List<string> { "a" }, events: _ => []);
        var f = F(facts, MaintenanceDashboardFactsService.DashKey);

        Assert.Contains("不是「維護做得好不好」的判決", f.Value);
        Assert.DoesNotContain(MaintenanceDashboardFactsService.DashKey, f.Value);
    }

    // ── DS-001 BitLocker ────────────────────────────────────────────────

    [Fact]
    public void BitLocker_卷狀態如實_且不含金鑰字樣()
    {
        var f = VolumeEncryptionFactsService.Collect(At, () => VolumeEncryptionFactsService.ProbeResult.Ok(
        [
            new VolumeEncryptionFactsService.VolumeState("C:", 1, 1, 7, 100),
            new VolumeEncryptionFactsService.VolumeState("D:", 0, 0, null, null),
        ]));

        Assert.Contains("C:：保護 開", f.Value);
        Assert.Contains("XTS-AES-256", f.Value);
        Assert.Contains("不讀復原金鑰", f.Value);
        Assert.DoesNotContain("RecoveryPassword", f.Value);
    }

    [Fact]
    public void BitLocker_提供者不存在_NotSupported不是0卷()
    {
        var f = VolumeEncryptionFactsService.Collect(At, () =>
            VolumeEncryptionFactsService.ProbeResult.Fail("沒有提供者", FactAvailability.NotSupported));

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("沒有提供者", f.UnavailableReason);
    }

    [Fact]
    public void BitLocker_權限不足與讀取失敗分開()
    {
        var denied = VolumeEncryptionFactsService.Collect(At, () =>
            VolumeEncryptionFactsService.ProbeResult.Fail("需管理員", FactAvailability.InsufficientPrivilege));
        var broken = VolumeEncryptionFactsService.Collect(At, () =>
            VolumeEncryptionFactsService.ProbeResult.Fail("壞了", FactAvailability.ReadError));

        Assert.Equal(FactAvailability.InsufficientPrivilege, denied.Availability);
        Assert.Equal(FactAvailability.ReadError, broken.Availability);
    }

    [Fact]
    public void BitLocker_零卷_0是值不是缺席()
    {
        var f = VolumeEncryptionFactsService.Collect(At, () => VolumeEncryptionFactsService.ProbeResult.Ok([]));
        Assert.Equal(0, f.NumericValue);
        Assert.Contains("0 個可加密卷", f.Value);
    }

    [Fact]
    public void BitLocker_探針拋例外_ReadError()
    {
        var f = VolumeEncryptionFactsService.Collect(At, () => throw new InvalidOperationException("x"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── QS-008 查詢歷史 ─────────────────────────────────────────────────

    [Fact]
    public void 查詢歷史_沒查過_標不適用且講明不落盤()
    {
        var f = QueryGovernanceFactsService.HistoryFact(At, []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不落盤", f.UnavailableReason);
    }

    [Fact]
    public void 查詢歷史_有查詢_標敏感且只記字串()
    {
        var f = QueryGovernanceFactsService.HistoryFact(At, ["cpu.*", "mem.*"]);
        Assert.True(f.Sensitive, "查詢字串屬 L1，必須交遮蔽政策");
        Assert.Contains("cpu.*", f.Value);
        Assert.Contains("不含結果", f.Value);
    }

    [Fact]
    public void 查詢歷史_由快取累積_上限50()
    {
        var cache = new QueryResultCache();
        for (int i = 0; i < 60; i++) cache.GetOrBuild("q" + i, "fp", () => []);
        Assert.Equal(50, cache.History.Count);
        Assert.Equal("q10", cache.History[0]);
    }

    // ── QS-010 文法版本 ─────────────────────────────────────────────────

    [Fact]
    public void 文法版本_semver格式且事實說明規則()
    {
        var parts = QueryGovernanceFactsService.GrammarVersion.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.All(parts, p => Assert.True(int.TryParse(p, out _), $"{p} 不是數字"));

        var f = QueryGovernanceFactsService.GrammarFact(At);
        Assert.Contains("semver", f.Value);
        Assert.Contains("只有文法改變才動 major／minor", f.Value);
    }

    // ── UF-005 更新時間軸（收口）────────────────────────────────────────

    [Fact]
    public void 更新時間軸_沒有來源事實_標不適用()
    {
        var f = QueryGovernanceFactsService.TimelineFact(At, []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不等於沒有更新", f.UnavailableReason);
    }

    [Fact]
    public void 更新時間軸_收口wu_history四把鍵()
    {
        var facts = new List<HardwareFact>
        {
            new("wu.history.latest", "更新", "最近", "2026-10-01 KB5064081", "", "s", FactTrustLevel.Reported, false, At),
            new("wu.history.total", "更新", "總數", "42", "", "s", FactTrustLevel.Reported, false, At),
            new("wu.history.30d", "更新", "近30天", "3", "", "s", FactTrustLevel.Reported, false, At),
            new("wu.history.failed", "更新", "失敗", "1", "", "s", FactTrustLevel.Reported, false, At),
        };
        var f = QueryGovernanceFactsService.TimelineFact(At, facts);

        Assert.Contains("最近 2026-10-01 KB5064081", f.Value);
        Assert.Contains("失敗 1", f.Value);
        Assert.Contains("收口", f.Value);
    }

    // ── PL-009 單一實例轉發 ─────────────────────────────────────────────

    [Fact]
    public void 單一實例轉發_事實描述機制()
    {
        var f = QueryGovernanceFactsService.ForwardFact(At);
        Assert.Contains("第二份實例", f.Value);
        Assert.Contains("喚回主視窗", f.Value);
    }

    [Fact]
    public void 單一實例轉發_App原始碼仍保有具名訊號()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "XinSpect.csproj")))
            root = root.Parent;
        Assert.NotNull(root);
        string app = File.ReadAllText(Path.Combine(root!.FullName, "App.xaml.cs"));

        Assert.Contains("XinSpect.SingleInstance", app);
        Assert.Contains("EventWaitHandle", app);
    }
}
