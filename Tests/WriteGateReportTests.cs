using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-065（守門清冊）、P1-067 – P1-074
/// （寫入閘門、寫入記帳、審計鏈頭／驗證、證據包 manifest／互驗、寫入白名單、撤銷回復）的守門。
/// </summary>
public class WriteGateReportTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static WriteGateReport.WriteRecord Rec(string verb, string origin = "掃描器") =>
        new(verb, "某路徑", At, origin);

    // ── P1-073 寫入白名單 ──────────────────────────────────────────────

    [Fact]
    public void 白名單_每一條登記都要完整()
    {
        foreach (var w in WriteGateReport.Allowlist)
            Assert.True(w.IsWellFormed,
                $"{w.Verb}：白名單條目缺動詞／目標／理由——不完整的白名單等於沒有白名單");
    }

    [Fact]
    public void 白名單_未登記的寫入一律拒收()
    {
        var (allowed, reason) = WriteGateReport.Check("偷偷改 MSR");
        Assert.False(allowed);
        Assert.Contains("不在寫入白名單內", reason);
        Assert.Contains("未登記的寫入一律拒收", reason);
    }

    [Fact]
    public void 白名單_已登記的寫入放行()
    {
        var (allowed, reason) = WriteGateReport.Check("審計日誌追加");
        Assert.True(allowed);
        Assert.Contains("已登記", reason);
    }

    [Fact]
    public void 白名單_不可撤銷的寫入要逐一列出來()
    {
        var fact = WriteGateReport.ToAllowlistFact(At);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("不可撤銷", fact.Value);
        Assert.Contains("審計日誌追加", fact.Value);
        Assert.Contains("必須被逐一列出來讓人看見", fact.UnavailableReason);
    }

    // ── P1-067 寫入計數 ────────────────────────────────────────────────

    [Fact]
    public void 寫入計數_被拒不是壞事但一定要被看見()
    {
        var fact = WriteGateReport.ToCountFact(At, writes: 5, rejected: 2);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("被閘門拒收", fact.Value);
        Assert.Contains("閘門正常運作", fact.UnavailableReason);
    }

    [Fact]
    public void 寫入計數_全部合規時標Present()
    {
        var fact = WriteGateReport.ToCountFact(At, writes: 3, rejected: 0);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Null(fact.UnavailableReason);
    }

    [Fact]
    public void 寫入計數_沒有寫入時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            WriteGateReport.ToCountFact(At, 0, 0).Availability);
    }

    // ── P1-068 寫入記帳 ────────────────────────────────────────────────

    [Fact]
    public void 寫入記帳_四欄缺一不可()
    {
        var incomplete = new WriteGateReport.WriteRecord("審計日誌追加", "", At, "掃描器");
        Assert.False(incomplete.IsComplete);

        var fact = WriteGateReport.ToLedgerFact(At, [Rec("審計日誌追加"), incomplete]);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("四欄缺一不可", fact.UnavailableReason);
    }

    [Fact]
    public void 寫入記帳_完整時按動詞分組申報()
    {
        var fact = WriteGateReport.ToLedgerFact(At,
            [Rec("審計日誌追加"), Rec("審計日誌追加"), Rec("時間膠囊存檔")]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("審計日誌追加 2", fact.Value);
    }

    // ── P1-074 撤銷與回復 ──────────────────────────────────────────────

    [Fact]
    public void 撤銷_未登記的寫入沒有回復路徑可言()
    {
        var fact = WriteGateReport.ToRollbackFact(At, [Rec("偷偷改 MSR")]);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("先登記才有得判斷", fact.UnavailableReason);
    }

    [Fact]
    public void 撤銷_不可撤銷的寫入要明說它不可回復()
    {
        var fact = WriteGateReport.ToRollbackFact(At, [Rec("審計日誌追加")]);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("不可撤銷", fact.Value);
        Assert.Contains("知道它不可回復", fact.UnavailableReason);
    }

    [Fact]
    public void 撤銷_全部可撤銷時標Present()
    {
        var fact = WriteGateReport.ToRollbackFact(At, [Rec("時間膠囊存檔")]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }

    // ── P1-069／P1-070 審計鏈 ──────────────────────────────────────────

    [Fact]
    public void 審計鏈_空鏈要說出創始雜湊()
    {
        var fact = WriteGateReport.ToHeadFact(At, []);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains(AuditEntry.Genesis, fact.Value);
    }

    [Fact]
    public void 審計鏈_有紀錄時鏈頭可對外公告()
    {
        var entries = new List<AuditEntry>
        {
            AuditLogService.Append([], "tester", "", "測試動作", "範圍", "摘要", "", At),
        };
        var fact = WriteGateReport.ToHeadFact(At, entries);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains(entries[0].EntryHash, fact.Value);
        Assert.Contains("任何後續改動都會讓它對不上", fact.UnavailableReason);
    }

    [Fact]
    public void 審計驗證_通過時標Present()
    {
        var entries = new List<AuditEntry>
        {
            AuditLogService.Append([], "tester", "", "A", "S", "r", "", At),
        };
        entries.Add(AuditLogService.Append(entries, "tester", "", "B", "S", "r", "", At));

        var fact = WriteGateReport.ToVerifyFact(At, AuditVerifier.Verify(entries));
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(2.0, fact.NumericValue);
    }

    [Fact]
    public void 審計驗證_被竄改時標讀取失敗並指出第幾筆()
    {
        var entries = new List<AuditEntry>
        {
            AuditLogService.Append([], "tester", "", "A", "S", "r", "", At),
        };
        entries.Add(AuditLogService.Append(entries, "tester", "", "B", "S", "r", "", At));
        entries[0] = entries[0] with { ResultSummary = "被改過了" };

        var fact = WriteGateReport.ToVerifyFact(At, AuditVerifier.Verify(entries));
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("雜湊不符", fact.UnavailableReason);
    }

    [Fact]
    public void 審計驗證_空鏈標不適用而不是通過()
    {
        var fact = WriteGateReport.ToVerifyFact(At, AuditVerifier.Verify([]));
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
    }

    // ── P1-071／P1-072 證據包 ──────────────────────────────────────────

    [Fact]
    public void 證據包_空的清單不能證明任何事()
    {
        var fact = WriteGateReport.ToManifestFact(At, [], []);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("只證明「沒有東西」", fact.UnavailableReason);
    }

    [Fact]
    public void 證據包_清單有而包裡沒有時要標讀取失敗()
    {
        var fact = WriteGateReport.ToManifestFact(At,
            [("a.json", "sha256:aa")], ["b.json"]);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("b.json", fact.Value);
    }

    [Fact]
    public void 證據包_完整時標Present()
    {
        var fact = WriteGateReport.ToManifestFact(At,
            [("a.json", "sha256:aa"), ("b.json", "sha256:bb")], []);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(2.0, fact.NumericValue);
    }

    [Fact]
    public void 互驗_兩者都通過才算出報告()
    {
        var ok = WriteGateReport.ToChainVerifyFact(At, chainValid: true, manifestValid: true, null);
        Assert.Equal(FactAvailability.Present, ok.Availability);

        var bad = WriteGateReport.ToChainVerifyFact(At, chainValid: true, manifestValid: false, null);
        Assert.Equal(FactAvailability.ReadError, bad.Availability);
        Assert.Contains("manifest", bad.Value);
        Assert.Contains("單獨一個通過不足以證明", bad.UnavailableReason);
    }

    // ── P1-065 守門清冊輸出 ────────────────────────────────────────────

    [Fact]
    public void 清冊輸出_要按模式分組並說明正對照的分布()
    {
        var fact = WriteGateReport.ToInventoryFact(At);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("條登記", fact.Value);
        Assert.Contains("附正對照", fact.UnavailableReason);
    }
}
