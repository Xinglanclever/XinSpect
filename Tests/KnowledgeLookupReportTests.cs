using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-039（OUI 知識包）、P1-040（PCI 知識包）的查詢申報守門。
/// </summary>
public class KnowledgeLookupReportTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    // ── 查詢本身的三態 ──────────────────────────────────────────────────

    [Fact]
    public void OUI_知名前綴命中()
    {
        var (outcome, result) = KnowledgeLookupReport.LookupOui("00-1B-21-AA-BB-CC");
        Assert.Equal(KnowledgeLookupReport.LookupOutcome.Hit, outcome);
        Assert.Equal("Intel", result);
    }

    [Fact]
    public void OUI_未收錄的前綴要標未收錄而不是猜()
    {
        var (outcome, result) = KnowledgeLookupReport.LookupOui("AA-BB-CC-DD-EE-FF");
        Assert.Equal(KnowledgeLookupReport.LookupOutcome.NotListed, outcome);
        Assert.Contains("未收錄", result);
        Assert.Contains("AA-BB-CC", result);      // 原始碼要留下來，讓人自己去查
    }

    [Fact]
    public void OUI_格式壞掉與未收錄是不同結果()
    {
        var (outcome, result) = KnowledgeLookupReport.LookupOui("zz");
        Assert.Equal(KnowledgeLookupReport.LookupOutcome.Unparsable, outcome);
        Assert.Contains("無法解析", result);
    }

    [Fact]
    public void PCI_知名廠商命中()
    {
        var (outcome, result) = KnowledgeLookupReport.LookupPciVendor(0x8086);
        Assert.Equal(KnowledgeLookupReport.LookupOutcome.Hit, outcome);
        Assert.Equal("Intel", result);
    }

    [Fact]
    public void PCI_未收錄的廠商要標未收錄並附原始ID()
    {
        var (outcome, result) = KnowledgeLookupReport.LookupPciVendor(0xFFFF);
        Assert.Equal(KnowledgeLookupReport.LookupOutcome.NotListed, outcome);
        Assert.Contains("未收錄", result);
        Assert.Contains("0xFFFF", result);
    }

    // ── 彙總 ────────────────────────────────────────────────────────────

    [Fact]
    public void 彙總_三態分開計數()
    {
        var stats = KnowledgeLookupReport.Summarize(
        [
            KnowledgeLookupReport.LookupOui("00-1B-21-00-00-01"),
            KnowledgeLookupReport.LookupOui("AA-BB-CC-00-00-02"),
            KnowledgeLookupReport.LookupOui("zz"),
        ]);

        Assert.Equal(3, stats.Total);
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.NotListed);
        Assert.Equal(1, stats.Unparsable);
        Assert.Single(stats.MissedKeys);
    }

    [Fact]
    public void 彙總_沒有查詢時回空統計()
    {
        Assert.Equal(KnowledgeLookupReport.LookupStats.Empty,
            KnowledgeLookupReport.Summarize([]));
    }

    // ── 輸出鍵 ──────────────────────────────────────────────────────────

    [Fact]
    public void OUI鍵_沒有查詢時標不適用而不是回零()
    {
        var fact = KnowledgeLookupReport.ToOuiFact(At, KnowledgeLookupReport.LookupStats.Empty);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("沒有查詢", fact.Value);
    }

    [Fact]
    public void OUI鍵_全部命中時標Present()
    {
        var stats = KnowledgeLookupReport.Summarize(
            [KnowledgeLookupReport.LookupOui("00-1B-21-00-00-01")]);
        var fact = KnowledgeLookupReport.ToOuiFact(At, stats);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Null(fact.UnavailableReason);
        Assert.Contains("全部命中", fact.Value);
    }

    [Fact]
    public void OUI鍵_有未收錄時標未確認並說出要補的是知識表()
    {
        var stats = KnowledgeLookupReport.Summarize(
            [KnowledgeLookupReport.LookupOui("AA-BB-CC-00-00-02")]);
        var fact = KnowledgeLookupReport.ToOuiFact(At, stats);

        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("要補的是知識表", fact.UnavailableReason);
        Assert.Contains("AA-BB-CC", fact.Value);
    }

    [Fact]
    public void OUI鍵_有無法解析時標讀取失敗並說那是輸入的問題()
    {
        var stats = KnowledgeLookupReport.Summarize([KnowledgeLookupReport.LookupOui("zz")]);
        var fact = KnowledgeLookupReport.ToOuiFact(At, stats);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("不是知識表的缺口", fact.UnavailableReason);
    }

    [Fact]
    public void PCI鍵_有未收錄時不是錯誤狀態()
    {
        var stats = KnowledgeLookupReport.Summarize(
            [KnowledgeLookupReport.LookupPciVendor(0xFFFF)]);
        var fact = KnowledgeLookupReport.ToPciFact(At, stats);

        Assert.Equal(FactAvailability.Unknown, fact.Availability);   // 不是 ReadError
        Assert.NotEqual(FactAvailability.ReadError, fact.Availability);
    }
}
