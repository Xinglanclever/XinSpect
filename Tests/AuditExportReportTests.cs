using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-075（狀態變更審計）、P1-076（審計鏈匯出格式）、
/// P1-083（三方對帳）的守門。
/// </summary>
public class AuditExportReportTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    // ── P1-075 狀態變更審計 ────────────────────────────────────────────

    [Fact]
    public void 狀態變更_沒入鏈的變更要指名而不是只報數量()
    {
        var fact = AuditExportReport.ToStateChangeFact(At,
        [
            ("知識包換檔", true),
            ("快照匯出", false),
            ("原始傾印寫入", false),
        ]);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("快照匯出", fact.Value);       // 指名
        Assert.Contains("原始傾印寫入", fact.Value);
        Assert.Contains("該記的事沒記就等於沒發生", fact.UnavailableReason);
    }

    [Fact]
    public void 狀態變更_全部入鏈時標Present()
    {
        var fact = AuditExportReport.ToStateChangeFact(At, [("設定變更", true)]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("全部入鏈", fact.Value);
    }

    [Fact]
    public void 狀態變更_沒有變更時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            AuditExportReport.ToStateChangeFact(At, []).Availability);
    }

    [Fact]
    public void 狀態變更_必須入鏈的清單短而明確()
    {
        // 列太多的下場是每一條都沒人認真看。
        Assert.InRange(AuditExportReport.AuditableChanges.Count, 3, 8);
        Assert.True(AuditExportReport.IsAuditable("知識包換檔"));
        Assert.False(AuditExportReport.IsAuditable("讀取 CPU 型號"));
    }

    // ── P1-076 審計匯出 ────────────────────────────────────────────────

    [Fact]
    public void 匯出_不自足時要說那等於本程式的自述()
    {
        var fact = AuditExportReport.ToExportFact(At, 100, selfContained: false, includesAlgorithm: true);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("等於本程式的自述", fact.UnavailableReason);
    }

    [Fact]
    public void 匯出_沒寫演算法時要說猜錯比不驗更糟()
    {
        var fact = AuditExportReport.ToExportFact(At, 100, selfContained: true, includesAlgorithm: false);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("猜錯的驗證結果比不驗更糟", fact.UnavailableReason);
    }

    [Fact]
    public void 匯出_自足且含演算法時標Present()
    {
        var fact = AuditExportReport.ToExportFact(At, 100, true, true);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("可交第三方獨立驗證", fact.Value);
    }

    [Fact]
    public void 匯出_沒有匯出時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            AuditExportReport.ToExportFact(At, 0, true, true).Availability);
    }

    // ── P1-083 三方對帳 ────────────────────────────────────────────────

    [Fact]
    public void 三方_矩陣有而目錄沒有要標讀取失敗並指名()
    {
        // 規格宣告了而實作沒有——這一類是「規格有、實作沒有」。
        var fact = AuditExportReport.ToThreeWayFact(At,
            ["cpu.cores"], ["cpu.cores"], ["cpu.cores", "cpu.newthing"]);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("規格有而實作沒有", fact.UnavailableReason);
        Assert.Contains("cpu.newthing", fact.UnavailableReason);
    }

    [Fact]
    public void 三方_矩陣與目錄共有而掃描缺要標未確認並說出兩種可能()
    {
        // 可能是掃描器不認得那種寫法，也可能是真的沒接線——處理方式完全不同。
        var fact = AuditExportReport.ToThreeWayFact(At,
            ["cpu.cores", "scan.partial.count"], ["cpu.cores"],
            ["cpu.cores", "scan.partial.count"]);

        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("掃描器要擴充", fact.UnavailableReason);
        Assert.Contains("真的沒接線", fact.UnavailableReason);
    }

    [Fact]
    public void 三方_只有目錄獨有時標未確認()
    {
        var fact = AuditExportReport.ToThreeWayFact(At,
            ["cpu.cores", "目錄獨有"], ["cpu.cores"], ["cpu.cores"]);

        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("既有目錄守門的範圍", fact.UnavailableReason);
    }

    [Fact]
    public void 三方_三方一致時標Present()
    {
        var fact = AuditExportReport.ToThreeWayFact(At,
            ["a.b", "c.d"], ["a.b", "c.d"], ["a.b", "c.d"]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }

    [Fact]
    public void 三方_缺一方時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            AuditExportReport.ToThreeWayFact(At, [], [], ["a.b"]).Availability);
    }
}
