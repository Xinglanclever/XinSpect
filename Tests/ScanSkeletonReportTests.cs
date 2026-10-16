using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-001／P1-002／P1-004／P1-010／P1-012 的輸出鍵生產守門。
/// </summary>
public class ScanSkeletonReportTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static ScanOutcome Outcome(params ScanStepResult[] steps) => new([], steps);

    // ── P1-001 / P1-012 採用數 ─────────────────────────────────────────

    [Fact]
    public void 採用數_一個都沒採用時不得標Present()
    {
        var fact = ScanSkeletonReport.ToAdoptionFact(At, adopted: 0, total: 40);
        Assert.NotEqual(FactAvailability.Present, fact.Availability);
        Assert.Contains("0/40", fact.Value);
        Assert.Contains("尚未套用", fact.Value);
    }

    [Fact]
    public void 採用數_全部採用才標Present()
    {
        var fact = ScanSkeletonReport.ToAdoptionFact(At, adopted: 5, total: 5);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(5.0, fact.NumericValue);
    }

    [Fact]
    public void 採用數_沒有來源可統計時標不適用()
    {
        var fact = ScanSkeletonReport.ToAdoptionFact(At, adopted: 0, total: 0);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
    }

    // ── P1-002 逾時 ────────────────────────────────────────────────────

    [Fact]
    public void 逾時_沒有逾時要說出最慢的一步()
    {
        var fact = ScanSkeletonReport.ToTimeoutFact(At,
        [
            ScanStepResult.Completed("快", TimeSpan.FromMilliseconds(5)),
            ScanStepResult.Completed("慢", TimeSpan.FromMilliseconds(90)),
        ]);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("慢", fact.Value);
        Assert.Contains("90", fact.Value);
    }

    [Fact]
    public void 逾時_有逾時要標讀取失敗並指名步驟()
    {
        var fact = ScanSkeletonReport.ToTimeoutFact(At,
        [
            ScanStepResult.Failed("SPD 讀取", TimeSpan.FromMilliseconds(250),
                "步驟「SPD 讀取」超過宣告上界 250 毫秒"),
        ]);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("SPD 讀取", fact.Value);
        // **非 Present 的事實不得帶數字**（HardwareFact 的不變式）：1 已被收斂成 null，
        // 而「原本是多少」留在理由欄的痕跡裡（不是靜默修正）。
        Assert.Null(fact.NumericValue);
        Assert.Contains("原本帶了數字 1", fact.UnavailableReason);
    }

    [Fact]
    public void 逾時_使用未宣告的預設上界要被指出來但不算失敗()
    {
        var fact = ScanSkeletonReport.ToTimeoutFact(At,
        [
            ScanStepResult.Failed("某步", TimeSpan.FromMilliseconds(5000),
                "步驟「某步」超過預設上界 5000 毫秒（未宣告）"),
        ]);

        Assert.Contains("未宣告", fact.UnavailableReason);
    }

    [Fact]
    public void 逾時_沒有步驟時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable, ScanSkeletonReport.ToTimeoutFact(At, []).Availability);
    }

    // ── P1-004 中斷保留 ────────────────────────────────────────────────

    [Fact]
    public void 中斷保留_沒被中斷時標不適用而不是回零()
    {
        var outcome = Outcome(
            ScanStepResult.Completed("一", TimeSpan.Zero),
            ScanStepResult.Completed("二", TimeSpan.Zero));
        var fact = ScanSkeletonReport.ToPartialFact(At, outcome);

        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("未被中斷", fact.Value);
    }

    [Fact]
    public void 中斷保留_被中斷但保住部分事實時標未確認()
    {
        var outcome = Outcome(
            ScanStepResult.Completed("一", TimeSpan.Zero),
            ScanStepResult.Cancelled("二", TimeSpan.Zero));
        var fact = ScanSkeletonReport.ToPartialFact(At, outcome);

        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        // **非 Present 的事實不得帶數字**（HardwareFact 的不變式）：1 已被收斂成 null，
        // 而「原本是多少」留在理由欄的痕跡裡（不是靜默修正）。
        Assert.Null(fact.NumericValue);
        Assert.Contains("原本帶了數字 1", fact.UnavailableReason);
        Assert.Contains("保留", fact.Value);
    }

    [Fact]
    public void 中斷保留_被中斷且什麼都沒完成時標讀取失敗()
    {
        var outcome = Outcome(ScanStepResult.Cancelled("一", TimeSpan.Zero));
        var fact = ScanSkeletonReport.ToPartialFact(At, outcome);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("沒有任何步驟完成", fact.UnavailableReason);
    }

    // ── P1-010 供檔零出網 ──────────────────────────────────────────────

    [Fact]
    public void 零出網_沒有供檔時標不適用()
    {
        var fact = ScanSkeletonReport.ToZeroEgressFact(At, fileInputs: 0, egressAttempts: 0);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
    }

    [Fact]
    public void 零出網_有供檔且無出網時標Present()
    {
        var fact = ScanSkeletonReport.ToZeroEgressFact(At, fileInputs: 3, egressAttempts: 0);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("只在本機解析", fact.Value);
        Assert.Null(fact.UnavailableReason);
    }

    [Fact]
    public void 零出網_出現出網嘗試要標讀取失敗並說明是產品立場()
    {
        var fact = ScanSkeletonReport.ToZeroEgressFact(At, fileInputs: 1, egressAttempts: 1);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("產品立場", fact.UnavailableReason);
    }
}
