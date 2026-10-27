using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 收尾事實組（Vol2 推進第 52–55 項）的守門：全部注入假件——
/// 真實通路碰 WMI 與行程模組，在單元測試裡讀真的會讓結果取決於機器。
/// </summary>
public class ClosingFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 7, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static IReadOnlyList<HardwareFact> Collect(
        ClosingFactsService.EnvProbe? env = null,
        ClosingFactsService.ModulesProbe? modules = null,
        ClosingFactsService.PagesProbe? pages = null)
        => ClosingFactsService.Collect(At,
            env ?? (() => new ClosingFactsService.MachineEnvironment("Vendor", "Model", false, "x64-based PC")),
            modules ?? (() => [@"C:\App\a.dll"]),
            pages ?? (() => [("overview", "總覽", "總覽")]));

    // ── IN-003 執行環境自我聲明 ─────────────────────────────────────────

    [Fact]
    public void 環境聲明_虛擬化存在_如實說是虛擬化環境()
    {
        var f = Fact(Collect(env: () => new ClosingFactsService.MachineEnvironment(
            "Microsoft Corporation", "Virtual Machine", true, "x64-based PC")), ClosingFactsService.EnvKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("Hypervisor 存在", f.Value);
        Assert.Contains("不是反 VM", f.Value);
    }

    [Fact]
    public void 環境聲明_Hypervisor欄位未回報_不猜()
    {
        var f = Fact(Collect(env: () => new ClosingFactsService.MachineEnvironment("V", "M", null, "x64-based PC")),
            ClosingFactsService.EnvKey);

        Assert.Contains("未回報", f.Value);
        Assert.DoesNotContain("執行在虛擬化環境", f.Value);
    }

    [Fact]
    public void 環境聲明_讀不到_標ReadError()
    {
        var f = Fact(Collect(env: () => null), ClosingFactsService.EnvKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不推測", f.UnavailableReason);
    }

    // ── QS-002 來源信任分布 ─────────────────────────────────────────────

    [Fact]
    public void 來源信任_空集_標Unknown不讀成全部可信()
    {
        var f = ClosingFactsService.TrustFact(At, []);
        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("不是「全部可信」", f.UnavailableReason);
    }

    [Fact]
    public void 來源信任_只列分布不給分數()
    {
        var facts = new List<HardwareFact>
        {
            new("a", "測試", "a", "v", "", "s", FactTrustLevel.Measured, false, At),
            new("b", "測試", "b", "v", "", "s", FactTrustLevel.Reported, false, At),
            new("c", "測試", "c", "", "", "s", FactTrustLevel.Unknown, false, At, null,
                FactAvailability.ReadError, "讀不到"),
        };
        var f = ClosingFactsService.TrustFact(At, facts);

        Assert.Contains("Measured 1", f.Value);
        Assert.Contains("Reported 1", f.Value);
        Assert.Contains("Unknown 1", f.Value);
        Assert.Contains("其中 1 項不是 Present", f.Value);
        Assert.Contains("不產生分數", f.Value);
    }

    // ── DL-010 模組衝突 ─────────────────────────────────────────────────

    [Fact]
    public void 模組衝突_同名不同路徑如實列出()
    {
        var facts = Collect(modules: () => [
            @"C:\App\x.dll", @"C:\App\plugins\x.dll", @"C:\App\y.dll"]);
        var count = Fact(facts, ClosingFactsService.ConflictCountKey);
        var summary = Fact(facts, ClosingFactsService.ConflictSummaryKey);

        Assert.Equal(1, count.NumericValue);
        Assert.Contains("x.dll", summary.Value);
        Assert.Contains("不是惡意判決", summary.Value);
    }

    [Fact]
    public void 模組衝突_沒有衝突_0是值不是缺席()
    {
        var facts = Collect(modules: () => [@"C:\App\a.dll", @"C:\App\b.dll"]);
        var count = Fact(facts, ClosingFactsService.ConflictCountKey);
        Assert.Equal(0, count.NumericValue);
        Assert.Contains("0 是值", Fact(facts, ClosingFactsService.ConflictSummaryKey).Value);
    }

    [Fact]
    public void 模組衝突_列舉失敗_ReadError()
    {
        var facts = Collect(modules: () => []);
        Assert.Equal(FactAvailability.ReadError, Fact(facts, ClosingFactsService.ConflictCountKey).Availability);
    }

    // ── NG-001 診斷樹導航 ───────────────────────────────────────────────

    [Fact]
    public void 診斷樹_分組列出_講明是導航不是背書()
    {
        var facts = Collect(pages: () => [
            ("bottleneck", "瓶頸診斷", "總覽"),
            ("history", "時間膠囊", "系統"),
            ("sensors", "感測器", "監控")]);
        var f = Fact(facts, ClosingFactsService.TreeKey);

        Assert.Equal(3, f.NumericValue);
        Assert.Contains("瓶頸診斷", f.Value);
        Assert.Contains("感測器", f.Value);
        Assert.Contains("不是診斷背書", f.Value);
    }

    [Fact]
    public void 診斷樹_讀不到頁面清單_ReadError()
    {
        var f = Fact(Collect(pages: () => []), ClosingFactsService.TreeKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不憑空生", f.UnavailableReason);
    }

    [Fact]
    public void 診斷樹_真實頁面清單不為空且都帶分組()
    {
        var pages = ClosingFactsService.ReadDiagnosticPages();

        Assert.NotEmpty(pages);
        Assert.All(pages, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Key));
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.False(string.IsNullOrWhiteSpace(p.Group));
        });
    }
}