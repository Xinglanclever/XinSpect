using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 裝置合規基線（ZR-004）單元測試。
/// 最重要的一條：<b>缺資料不能偽裝成通過</b>——指向不存在鍵或來源讀不到時算「無法判定」。
/// </summary>
public class DeviceComplianceFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Good(string key) =>
        new(key, "測試", key, "值", "", "測試", FactTrustLevel.Reported, false, At, 1);

    private static HardwareFact Bad(string key, FactAvailability a) =>
        new(key, "測試", key, "", "", "測試", FactTrustLevel.Unknown, false, At, null, a, "來源讀不到");

    private static KnowledgePack Pack(string snapshot, Dictionary<string, string> entries) =>
        new(1, "2026-10-14", [new KnowledgeTable("device-compliance", snapshot, entries)], "sha256:x");

    // ── 評估核心 ─────────────────────────────────────────────────────────

    [Fact]
    public void 全部有值時全部符合()
    {
        var facts = new Dictionary<string, HardwareFact>(StringComparer.Ordinal)
        {
            ["in.self"] = Good("in.self"),
            ["zr.mdm"] = Good("zr.mdm"),
        };
        var e = DeviceComplianceFactsService.Evaluate("2026-10-01",
            new Dictionary<string, string> { ["C-1"] = "in.self", ["C-2"] = "zr.mdm" }, facts);

        Assert.Equal(2, e.Total);
        Assert.Equal(2, e.Met);
        Assert.Equal(0, e.Unresolved);
    }

    [Fact]
    public void 指向不存在的鍵算無法判定而不是符合()
    {
        var facts = new Dictionary<string, HardwareFact>(StringComparer.Ordinal)
        {
            ["in.self"] = Good("in.self"),
        };
        var e = DeviceComplianceFactsService.Evaluate("2026-10-01",
            new Dictionary<string, string> { ["C-1"] = "in.self", ["C-2"] = "gone.forever" }, facts);

        // 這是本項的核心紅線：鍵表改版後「查不到」絕不能變成「通過」。
        Assert.Equal(1, e.Met);
        Assert.Equal(1, e.Unresolved);
        Assert.Contains(e.Detail, d => d.RequirementId == "C-2" && !d.Met && d.Reason.Contains("鍵表可能改版"));
    }

    [Fact]
    public void 來源讀不到算無法判定而不是符合()
    {
        var facts = new Dictionary<string, HardwareFact>(StringComparer.Ordinal)
        {
            ["in.self"] = Good("in.self"),
            ["zr.mdm"] = Bad("zr.mdm", FactAvailability.InsufficientPrivilege),
        };
        var e = DeviceComplianceFactsService.Evaluate("2026-10-01",
            new Dictionary<string, string> { ["C-1"] = "in.self", ["C-2"] = "zr.mdm" }, facts);

        Assert.Equal(1, e.Met);
        Assert.Equal(1, e.Unresolved);
        Assert.Contains(e.Detail, d => d.Reason.Contains("缺資料不算符合"));
    }

    [Theory]
    [InlineData(FactAvailability.NotApplicable)]
    [InlineData(FactAvailability.NotSupported)]
    [InlineData(FactAvailability.ReadError)]
    [InlineData(FactAvailability.Unknown)]
    public void 任何非Present狀態都不算符合(FactAvailability a)
    {
        var facts = new Dictionary<string, HardwareFact>(StringComparer.Ordinal) { ["k"] = Bad("k", a) };
        var e = DeviceComplianceFactsService.Evaluate("2026-10-01",
            new Dictionary<string, string> { ["C"] = "k" }, facts);
        Assert.Equal(0, e.Met);
        Assert.Equal(1, e.Unresolved);
    }

    [Fact]
    public void 逐條結果只帶識別碼不帶要求全文()
    {
        var facts = new Dictionary<string, HardwareFact>(StringComparer.Ordinal) { ["in.self"] = Good("in.self") };
        var e = DeviceComplianceFactsService.Evaluate("2026-10-01",
            new Dictionary<string, string> { ["AC-2(1)"] = "in.self" }, facts);
        // 機構的控制編號可能敏感，逐條結果只帶識別碼與依據。
        Assert.Equal("AC-2(1)", e.Detail[0].RequirementId);
        Assert.Contains("in.self", e.Detail[0].Reason);
    }

    // ── 事實值 ───────────────────────────────────────────────────────────

    [Fact]
    public void 沒有清單時兩把鍵都不適用()
    {
        var facts = DeviceComplianceFactsService.Collect(At, Pack("2026-10-01", []).Tables.Count == 1
            ? new KnowledgePack(1, "2026-10-14",
                [new KnowledgeTable("other-table", "2026-10-01",
                    new Dictionary<string, string> { ["x"] = "y" })], "sha256:x")
            : null, null);

        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.Contains("不等於合規", facts.Single(f => f.Key == "zr.compliance.table").UnavailableReason);
    }

    [Fact]
    public void 沒有知識包也是不適用()
    {
        var facts = DeviceComplianceFactsService.Collect(At, null, null);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.Contains("本專案不代為維護", facts[0].UnavailableReason);
    }

    [Fact]
    public void 缺快照日期整包拒收()
    {
        var facts = DeviceComplianceFactsService.Collect(At,
            Pack("", new Dictionary<string, string> { ["C-1"] = "in.self" }), null);
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.Contains("更危險", facts[0].UnavailableReason);
    }

    [Fact]
    public void 有清單時快照日期必印()
    {
        var facts = DeviceComplianceFactsService.Collect(At,
            Pack("2026-08-15", new Dictionary<string, string> { ["C-1"] = "in.self" }), null);
        var table = facts.Single(f => f.Key == "zr.compliance.table");
        Assert.Equal(FactAvailability.Present, table.Availability);
        Assert.Contains("快照 2026-08-15", table.Value);
        Assert.Contains("快照日期必印", table.Value);
    }

    [Fact]
    public void 空清單時基線不適用()
    {
        var facts = DeviceComplianceFactsService.Collect(At,
            Pack("2026-10-01", new Dictionary<string, string>()), null);
        Assert.Equal(FactAvailability.Present, facts[0].Availability);
        Assert.Equal(FactAvailability.NotApplicable, facts[1].Availability);
        Assert.Contains("沒有要求可評估", facts[1].UnavailableReason);
    }

    [Fact]
    public void 有清單有事實時陳述符合數與無法判定數()
    {
        var facts = new Dictionary<string, HardwareFact>(StringComparer.Ordinal)
        {
            ["in.self"] = Good("in.self"),
            ["zr.mdm"] = Bad("zr.mdm", FactAvailability.InsufficientPrivilege),
        };
        var result = DeviceComplianceFactsService.Collect(At,
            Pack("2026-09-30", new Dictionary<string, string> { ["C-1"] = "in.self", ["C-2"] = "zr.mdm" }),
            facts);

        var baseline = result.Single(f => f.Key == "zr.compliance.baseline");
        Assert.Equal(FactAvailability.Present, baseline.Availability);
        Assert.Contains("1／2 項符合", baseline.Value);
        Assert.Contains("1 項無法判定", baseline.Value);
        Assert.Contains("不算符合", baseline.Value);
        Assert.Equal(1d, baseline.NumericValue);
    }

    [Fact]
    public void 值裡聲明符合不是安全背書()
    {
        var result = DeviceComplianceFactsService.Collect(At,
            Pack("2026-09-30", new Dictionary<string, string> { ["C-1"] = "in.self" }),
            new Dictionary<string, HardwareFact>(StringComparer.Ordinal) { ["in.self"] = Good("in.self") });

        var baseline = result.Single(f => f.Key == "zr.compliance.baseline");
        Assert.Contains("「符合」不是安全背書", baseline.Value);
        Assert.Contains("不做分數、不做等級", baseline.Value);
    }

    [Fact]
    public void 不列清單條目文字()
    {
        var result = DeviceComplianceFactsService.Collect(At,
            Pack("2026-09-30", new Dictionary<string, string> { ["內部控制-07"] = "in.self" }), null);
        Assert.Contains("不列清單的具體條目文字", result[0].Value);
        // 識別碼不應出現在摘要值裡（逐條結果在評估物件裡，供程式用）。
        Assert.DoesNotContain("內部控制-07", result[0].Value);
    }

    [Fact]
    public void 沒有事實可比對時基線仍出值但不報符合數()
    {
        var result = DeviceComplianceFactsService.Collect(At,
            Pack("2026-09-30", new Dictionary<string, string> { ["C-1"] = "in.self" }), null);
        var baseline = result.Single(f => f.Key == "zr.compliance.baseline");
        Assert.Equal(FactAvailability.Present, baseline.Availability);
        Assert.Contains("本輪沒有事實可比對", baseline.Value);
        Assert.Equal(0d, baseline.NumericValue);
    }
}
