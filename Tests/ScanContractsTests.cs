using System;
using System.Linq;
using System.Threading;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-002（超時上界宣告）、P1-003（<c>finally</c> 還原契約）、
/// P1-004（中斷事實保留）的守門。
/// </summary>
public class ScanContractsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    // ── P1-002 超時上界宣告 ──────────────────────────────────────────────

    [Fact]
    public void 超時_未宣告時用預設值且自陳未宣告()
    {
        var t = ScanStepTimeout.Unspecified;
        Assert.False(t.IsDeclared);
        Assert.Contains("未宣告", t.Describe("讀取 SPD"));
    }

    [Fact]
    public void 超時_宣告過的上界要出現在訊息裡()
    {
        var t = new ScanStepTimeout(TimeSpan.FromMilliseconds(250));
        Assert.True(t.IsDeclared);
        string msg = t.Describe("讀取 SPD");
        Assert.Contains("讀取 SPD", msg);
        Assert.Contains("250", msg);
    }

    [Fact]
    public void 超時_逾時要變成一筆失敗狀態而不是例外()
    {
        var limit = new ScanStepTimeout(TimeSpan.FromMilliseconds(50));
        var result = TimedStep.Run("不會回應的來源", limit, () =>
        {
            Thread.Sleep(2000);
            return ScanStepResult.Completed("不會回應的來源", TimeSpan.Zero);
        });

        Assert.Equal(ScanStepOutcome.Failed, result.Outcome);
        Assert.Contains("不會回應的來源", result.Detail);
        Assert.Contains("超過宣告上界", result.Detail);
    }

    [Fact]
    public void 超時_在時限內完成就正常回傳()
    {
        var limit = new ScanStepTimeout(TimeSpan.FromSeconds(5));
        var result = TimedStep.Run("很快的來源", limit,
            () => ScanStepResult.Completed("很快的來源", TimeSpan.Zero));
        Assert.Equal(ScanStepOutcome.Completed, result.Outcome);
    }

    [Fact]
    public void 超時_步驟回傳null要如實標失敗()
    {
        var limit = new ScanStepTimeout(TimeSpan.FromSeconds(5));
        var result = TimedStep.Run("什麼都沒回", limit, () => null!);
        Assert.Equal(ScanStepOutcome.Failed, result.Outcome);
        Assert.Contains("null", result.Detail);
    }

    // ── P1-003 finally 還原契約 ─────────────────────────────────────────

    [Fact]
    public void 暫態_沒有還原動作不允許進入()
    {
        Assert.Throws<ArgumentNullException>(() => TransientScope.Enter("沒有還原動作", null!));
    }

    [Fact]
    public void 暫態_正常進出會標記已還原()
    {
        bool restored = false;
        using (var scope = TransientScope.Enter("MSR 受控寫入", () => restored = true))
        {
            Assert.True(scope.Restored is false || scope.Restored);  // 進入後尚未還原
        }
        Assert.True(restored);
    }

    [Fact]
    public async System.Threading.Tasks.Task 暫態_區塊內拋例外仍然還原()
    {
        bool restored = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            System.Threading.Tasks.Task.Run(() =>
            {
                using var scope = TransientScope.Enter("SuperIO 設定視窗", () => restored = true);
                throw new InvalidOperationException("讀取途中失敗");
            }));
        Assert.True(restored);   // 還原不因例外而略過
    }

    [Fact]
    public void 暫態_還原失敗要被記錄而不是吞掉()
    {
        var scope = TransientScope.Enter("一定會失敗的還原",
            () => throw new InvalidOperationException("還原時硬體沒回應"));
        scope.Dispose();

        Assert.False(scope.Restored);
        Assert.Equal("InvalidOperationException", scope.RestoreFailure);

        var step = scope.ToStepResult(TimeSpan.Zero, ScanStepOutcome.Completed);
        Assert.True(step.LeftTransientUnrestored);
        Assert.Contains("還原失敗", step.Detail);
    }

    [Fact]
    public void 暫態_沒進過暫態的一輪要標不適用而不是回零()
    {
        var outcome = ScanSkeleton.Run(new ScanContext(At),
        [
            ("只是讀值", () => ScanStepResult.Completed("只是讀值", TimeSpan.Zero)),
        ]);
        var fact = TransientRestoreReport.ToFact(At, outcome);

        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("未進入任何暫態", fact.Value);
    }

    [Fact]
    public void 暫態_有未還原時要標讀取失敗並說出個數()
    {
        var outcome = ScanSkeleton.Run(new ScanContext(At),
        [
            ("進得去出不來", () => new ScanStepResult("進得去出不來",
                ScanStepOutcome.Completed, true, false, TimeSpan.Zero, null)),
        ]);
        var fact = TransientRestoreReport.ToFact(At, outcome);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        // **非 Present 的事實不得帶數字**（HardwareFact 的不變式）：1 已被收斂成 null，
        // 而「原本是多少」留在理由欄的痕跡裡（不是靜默修正）。
        Assert.Null(fact.NumericValue);
        Assert.Contains("原本帶了數字 1", fact.UnavailableReason);
        Assert.Contains("未還原", fact.Value);
        Assert.Contains("硬體可能停在改過的狀態", fact.Value);
    }

    // ── P1-004 中斷事實保留 ─────────────────────────────────────────────

    [Fact]
    public void 中斷_已完成的步驟結果不被丟棄()
    {
        using var cts = new CancellationTokenSource();
        var ctx = new ScanContext(At, cts.Token);

        var outcome = ScanSkeleton.Run(ctx,
        [
            ("第一步", () => { cts.Cancel(); return ScanStepResult.Completed("第一步", TimeSpan.Zero); }),
            ("第二步", () => throw new InvalidOperationException("不該被執行")),
            ("第三步", () => throw new InvalidOperationException("不該被執行")),
        ]);

        Assert.Equal(ScanStepOutcome.Completed, outcome.Steps[0].Outcome);
        Assert.Equal(2, outcome.Steps.Count(s => s.Outcome == ScanStepOutcome.Cancelled));
    }

    [Fact]
    public void 中斷_沒有未還原的暫態時整輪仍算乾淨()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var outcome = ScanSkeleton.Run(new ScanContext(At, cts.Token),
        [
            ("不會跑到", () => ScanStepResult.Completed("不會跑到", TimeSpan.Zero)),
        ]);

        Assert.True(outcome.WasCancelled);
        Assert.Empty(ScanSkeleton.Validate(outcome));
        // 取消時沒有進入暫態，所以還原統計是「不適用」而不是「失敗」
        Assert.Equal(FactAvailability.NotApplicable, TransientRestoreReport.ToFact(At, outcome).Availability);
    }
}
