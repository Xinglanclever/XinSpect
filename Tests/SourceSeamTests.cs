using System;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-005（來源可用性查詢接縫）、
/// P1-006（來源列舉與去重）、P1-007（來源優先序與取代鏈）的守門。
/// </summary>
public class SourceSeamTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeSource(string name, Func<SourceAvailability>? availability = null)
        : ISourceAvailability, IReadOnlyScanner
    {
        public string SourceName => name;
        public string BackendName => name;
        public SourceAvailability QueryAvailability() =>
            availability?.Invoke() ?? SourceAvailability.Yes("測試來源一律可用");
        public ScanOutcome Scan(ScanContext context) => new([], []);
    }

    // ── P1-005 來源可用性 ───────────────────────────────────────────────

    [Fact]
    public void 可用性_不可用時要說出原因()
    {
        var fact = SourceAvailabilityFact.ToFact(At,
            new FakeSource("SPD 通道 0", () => SourceAvailability.NotSupported("本機沒有 SPD Hub")));

        Assert.Equal(FactAvailability.NotSupported, fact.Availability);
        Assert.Equal("本機沒有 SPD Hub", fact.UnavailableReason);
        Assert.Contains("不可用", fact.Value);
    }

    [Fact]
    public void 可用性_權限不足要明說提權後可得()
    {
        var fact = SourceAvailabilityFact.ToFact(At,
            new FakeSource("MSR", () => SourceAvailability.Privilege("需要核心驅動")));

        Assert.Equal(FactAvailability.InsufficientPrivilege, fact.Availability);
        Assert.Contains("提權後可得", fact.Value);
    }

    [Fact]
    public void 可用性_查詢本身拋例外要標讀取失敗而不是假裝可用()
    {
        var fact = SourceAvailabilityFact.ToFact(At,
            new FakeSource("壞掉的來源", () => throw new InvalidOperationException("內部細節")));

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Equal("InvalidOperationException", fact.UnavailableReason);
        Assert.DoesNotContain("內部細節", fact.Value);   // 訊息不外洩
    }

    [Fact]
    public void 可用性_可用時是Present且沒有不可用原因()
    {
        var fact = SourceAvailabilityFact.ToFact(At, new FakeSource("正常來源"));
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Null(fact.UnavailableReason);
        Assert.Equal(1.0, fact.NumericValue);
    }

    // ── P1-006 來源列舉與去重 ───────────────────────────────────────────

    [Fact]
    public void 註冊_名稱重複要拒絕後來的並記錄()
    {
        var reg = new SourceRegistry();
        Assert.True(reg.Register(new FakeSource("SPD")));
        Assert.False(reg.Register(new FakeSource("SPD")));

        Assert.Equal(1, reg.Count);
        Assert.Contains("SPD", reg.Rejected);
        Assert.Contains("被拒", reg.Validate()[0]);
    }

    [Fact]
    public void 註冊_同名只留第一支不靜默覆蓋()
    {
        var reg = new SourceRegistry();
        var first = new FakeSource("ACPI");
        reg.Register(first);
        reg.Register(new FakeSource("ACPI"));
        Assert.Same(first, reg.Sources[0]);
    }

    [Fact]
    public void 註冊_未命名來源要被拒且記錄()
    {
        var reg = new SourceRegistry();
        Assert.False(reg.Register(new FakeSource("   ")));
        Assert.Equal(0, reg.Count);
        Assert.Contains("（未命名來源）", reg.Rejected);
    }

    [Fact]
    public void 註冊_乾淨的一輪要標Present且沒有問題()
    {
        var reg = new SourceRegistry();
        reg.Register(new FakeSource("A"));
        reg.Register(new FakeSource("B"));

        Assert.Empty(reg.Validate());
        Assert.Equal(FactAvailability.Present, reg.ToFact(At).Availability);
        Assert.Equal(2.0, reg.ToFact(At).NumericValue);
    }

    [Fact]
    public void 註冊_沒有任何來源時標不適用而不是回零()
    {
        var fact = new SourceRegistry().ToFact(At);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("沒有任何來源被註冊", fact.Value);
    }

    // ── P1-007 優先序與取代鏈 ───────────────────────────────────────────

    [Fact]
    public void 取代鏈_用第一段成功的並留下每一段嘗試()
    {
        var chain = new SourceChain<string>()
            .Then("MSR", () => (string?)null)
            .Then("WMI", () => "3.4 GHz");

        var outcome = chain.Resolve();
        Assert.True(outcome.Resolved);
        Assert.Equal("WMI", outcome.WinningSource);
        Assert.Equal("3.4 GHz", outcome.Value);
        Assert.Equal(2, outcome.Attempts.Count);
        Assert.Equal("讀取回傳空值", outcome.Attempts[0].Reason);
    }

    [Fact]
    public void 取代鏈_不可用的來源要被跳過且記下原因()
    {
        var chain = new SourceChain<string>()
            .Then("MSR", () => "不該被讀到", () => SourceAvailability.Privilege("需要核心驅動"))
            .Then("WMI", () => "3.4 GHz");

        var outcome = chain.Resolve();
        Assert.Equal("WMI", outcome.WinningSource);
        Assert.Contains("需要核心驅動", outcome.Attempts[0].Reason);

        var fact = outcome.ToFact(At, "CPU 頻率");
        Assert.Contains("WMI", fact.Value);
        Assert.Contains("MSR不可用", fact.Value);
    }

    [Fact]
    public void 取代鏈_全部失敗時要輸出三態不得用預設值()
    {
        var chain = new SourceChain<string>()
            .Then("MSR", () => (string?)null)
            .Then("WMI", () => throw new InvalidOperationException("內部細節"));

        var outcome = chain.Resolve();
        Assert.False(outcome.Resolved);
        Assert.Null(outcome.Value);

        var fact = outcome.ToFact(At, "CPU 頻率");
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("全部 2 段都不可用", fact.Value);
        Assert.DoesNotContain("內部細節", fact.Value);
    }

    [Fact]
    public void 取代鏈_空鏈要標不適用而不是讀取失敗()
    {
        var fact = new SourceChain<string>().Resolve().ToFact(At, "CPU 頻率");
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("沒有註冊任何來源", fact.UnavailableReason);
    }

    [Fact]
    public void 取代鏈_可用性查詢自己炸掉也算這一段失敗()
    {
        var chain = new SourceChain<string>()
            .Then("壞來源", () => "不該被讀到", () => throw new InvalidOperationException("炸"))
            .Then("備援", () => "ok");

        var outcome = chain.Resolve();
        Assert.Equal("備援", outcome.WinningSource);
        Assert.Contains("可用性查詢失敗", outcome.Attempts[0].Reason);
    }
}
