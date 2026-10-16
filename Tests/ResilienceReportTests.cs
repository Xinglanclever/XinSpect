using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-085 – P1-095 的韌性、降級與失敗申報守門。
///
/// <para>
/// <b>這一組的主題是「失敗要怎麼被對待」：</b>讀不到不是錯誤（那是硬體的事實），
/// 但假裝讀到才是錯誤。
/// </para>
/// </summary>
public class ResilienceReportTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    // ── P1-085 鍵的演進 ────────────────────────────────────────────────

    [Fact]
    public void 演進_有更名就要標讀取失敗並說出後果()
    {
        var fact = ResilienceReport.ToEvolutionFact(At, "v2.70", 3, 0, ["old.key"]);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("歷史資料無法比較", fact.UnavailableReason);
    }

    [Fact]
    public void 演進_只有增減時標Present()
    {
        var fact = ResilienceReport.ToEvolutionFact(At, "v2.70", 5, 2, []);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("鍵是公開契約", fact.Value);
    }

    // ── P1-086 覆蓋缺口 ────────────────────────────────────────────────

    [Fact]
    public void 缺口_在基線內時不是缺陷()
    {
        var fact = ResilienceReport.ToGapFact(At, ["a.b", "c.d"], baseline: 10);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("未覆蓋不是錯誤", fact.UnavailableReason);
    }

    [Fact]
    public void 缺口_超過基線時要標未確認並說出數字()
    {
        var fact = ResilienceReport.ToGapFact(At, Enumerable.Range(0, 15).Select(i => $"k{i}.").ToList(), 10);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("缺口增至 15", fact.UnavailableReason);
    }

    [Fact]
    public void 缺口_沒有缺口時標Present()
    {
        Assert.Equal(FactAvailability.Present, ResilienceReport.ToGapFact(At, [], 10).Availability);
    }

    // ── P1-087 診斷分層 ────────────────────────────────────────────────

    [Fact]
    public void 診斷_未接住的例外是本程式的缺陷不是機器的問題()
    {
        var fact = ResilienceReport.ToDiagFact(At, caught: 5, uncaught: 2);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("本程式的缺陷", fact.UnavailableReason);
    }

    [Fact]
    public void 診斷_全部接住時標Present()
    {
        var fact = ResilienceReport.ToDiagFact(At, 5, 0);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }

    // ── P1-089 斷路器 ──────────────────────────────────────────────────

    [Fact]
    public void 斷路器_開啟是保護不是故障但被跳過的來源要顯示未取得()
    {
        var fact = ResilienceReport.ToCircuitFact(At,
            [("SPD", true, 3), ("ACPI", false, 0)]);

        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("保護不是故障", fact.UnavailableReason);
        Assert.Contains("必須顯示為『未取得』", fact.UnavailableReason);
    }

    [Fact]
    public void 斷路器_全部正常時標Present()
    {
        var fact = ResilienceReport.ToCircuitFact(At, [("SPD", false, 0)]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }

    // ── P1-090 降級模式 ────────────────────────────────────────────────

    [Fact]
    public void 降級_要說出使用者會把空白當成機器的實情()
    {
        var fact = ResilienceReport.ToDegradeFact(At,
            [("完整掃描", "完整"), ("深測", "降級：已停用")]);

        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("深測=降級：已停用", fact.Value);
        Assert.Contains("空白是機器的實情", fact.UnavailableReason);
    }

    [Fact]
    public void 降級_全部完整時標Present()
    {
        var fact = ResilienceReport.ToDegradeFact(At, [("a", "完整"), ("b", "完整")]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }

    // ── P1-092 失敗原因 ────────────────────────────────────────────────

    [Fact]
    public void 原因_沒有原因時要說使用者被迫自己猜()
    {
        var fact = ResilienceReport.ToErrorReasonFact(At, [("a.b", null)]);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("被迫自己猜", fact.UnavailableReason);
    }

    [Fact]
    public void 原因_只有型別名時要說那等於沒有原因()
    {
        var fact = ResilienceReport.ToErrorReasonFact(At, [("a.b", "IOException")]);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("不是一個可處理的說明", fact.UnavailableReason);
    }

    [Fact]
    public void 原因_有訊息時標Present()
    {
        var fact = ResilienceReport.ToErrorReasonFact(At,
            [("a.b", "IOException：裝置未就緒"), ("c.d", "UnauthorizedAccessException：拒絕存取")]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }

    // ── P1-094 重試 ────────────────────────────────────────────────────

    [Fact]
    public void 重試_最終失敗的要與重試成功分開算()
    {
        var fact = ResilienceReport.ToRetryFact(At, retriedAndSucceeded: 4,
            retriedAndFailed: 2, givenUp: 1);

        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("最終沒有拿到值", fact.UnavailableReason);
        Assert.Contains("不能與重試成功混成同一個數字", fact.UnavailableReason);
    }

    [Fact]
    public void 重試_全部成功時標Present()
    {
        var fact = ResilienceReport.ToRetryFact(At, 3, 0, 0);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }

    [Fact]
    public void 重試_沒有重試時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            ResilienceReport.ToRetryFact(At, 0, 0, 0).Availability);
    }

    // ── P1-095 失敗總表 ────────────────────────────────────────────────

    [Fact]
    public void 總表_沒有失敗時要明說而不是留空()
    {
        // 空白的總表與「總表沒跑」在報告上長得一樣。
        var fact = ResilienceReport.ToFailuresFact(At, steps: 10, failed: 0, []);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("沒有任何失敗", fact.Value);
        Assert.Contains("不是「沒有這張表」", fact.Value);
    }

    [Fact]
    public void 總表_有失敗時要說出這張表存在的理由()
    {
        var fact = ResilienceReport.ToFailuresFact(At, 10, 3, ["SPD", "TPM", "ME"]);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("SPD", fact.Value);
        Assert.Contains("不會掩蓋失敗的", fact.UnavailableReason);
    }

    [Fact]
    public void 總表_沒有掃描時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            ResilienceReport.ToFailuresFact(At, 0, 0, []).Availability);
    }
}
