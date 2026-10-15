using System;
using System.Linq;
using System.Threading;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-001「唯讀掃描骨架統一介面」的守門。
///
/// <para>
/// 這一組測試要釘住的不是「骨架能跑」，而是<b>骨架的四條不變式</b>：
/// ① 固定順序（步驟清單有序且同名只跑一次）；
/// ② 暫態必還原（進入暫態而未還原＝最嚴重的缺陷）；
/// ③ 中斷保留已得事實（不是整批丟棄）；
/// ④ 例外轉狀態而不是往上丟。
/// </para>
/// </summary>
public class ScanSkeletonTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly ScanContext Ctx = new(At);

    private static (string, Func<ScanStepResult>) Step(string name, ScanStepOutcome outcome,
        bool entered = false, bool restored = true) =>
        (name, () => new ScanStepResult(name, outcome, entered, restored, TimeSpan.Zero, null));

    [Fact]
    public void 骨架_步驟依宣告順序執行()
    {
        var order = new List<string>();
        var outcome = ScanSkeleton.Run(Ctx,
        [
            ("發現", () => { order.Add("發現"); return ScanStepResult.Completed("發現", TimeSpan.Zero); }),
            ("讀取", () => { order.Add("讀取"); return ScanStepResult.Completed("讀取", TimeSpan.Zero); }),
            ("解碼", () => { order.Add("解碼"); return ScanStepResult.Completed("解碼", TimeSpan.Zero); }),
        ]);

        Assert.Equal(["發現", "讀取", "解碼"], order);
        Assert.Equal(["發現", "讀取", "解碼"], outcome.Steps.Select(s => s.Name));
        Assert.Empty(ScanSkeleton.Validate(outcome));
    }

    [Fact]
    public void 骨架_同名步驟只執行一次()
    {
        int runs = 0;
        var outcome = ScanSkeleton.Run(Ctx,
        [
            ("重複", () => { runs++; return ScanStepResult.Completed("重複", TimeSpan.Zero); }),
            ("重複", () => { runs++; return ScanStepResult.Completed("重複", TimeSpan.Zero); }),
        ]);

        Assert.Equal(1, runs);                      // 重複註冊是設定錯誤，不是兩次讀取
        Assert.Single(outcome.Steps);
    }

    [Fact]
    public void 骨架_進入暫態未還原要被指名()
    {
        var outcome = ScanSkeleton.Run(Ctx,
        [
            Step("SuperIO 進出序", ScanStepOutcome.Completed, entered: true, restored: false),
        ]);

        var problems = ScanSkeleton.Validate(outcome);
        Assert.Single(problems);
        Assert.Contains("SuperIO 進出序", problems[0]);
        Assert.Contains("未還原", problems[0]);
        Assert.True(outcome.EnteredTransient);
    }

    [Fact]
    public void 骨架_進入暫態且已還原不算問題()
    {
        var outcome = ScanSkeleton.Run(Ctx,
        [
            Step("MSR 受控寫入", ScanStepOutcome.Completed, entered: true, restored: true),
        ]);

        Assert.Empty(ScanSkeleton.Validate(outcome));
        Assert.True(outcome.EnteredTransient);
    }

    [Fact]
    public void 骨架_例外轉成失敗狀態而不是往上丟()
    {
        var outcome = ScanSkeleton.Run(Ctx,
        [
            ("會炸的步驟", () => throw new InvalidOperationException("內部細節不該外洩")),
            ("後續步驟仍要跑", () => ScanStepResult.Completed("後續步驟仍要跑", TimeSpan.Zero)),
        ]);

        Assert.Equal(2, outcome.Steps.Count);       // 例外不中斷後續步驟
        Assert.Equal(ScanStepOutcome.Failed, outcome.Steps[0].Outcome);
        Assert.Equal("InvalidOperationException", outcome.Steps[0].Detail);  // 記型別，不記訊息全文
        Assert.Empty(ScanSkeleton.Validate(outcome));
    }

    [Fact]
    public void 骨架_失敗但沒留原因要被抓到()
    {
        var outcome = ScanSkeleton.Run(Ctx,
        [
            ("靜默失敗", () => ScanStepResult.Failed("靜默失敗", TimeSpan.Zero, "")),
        ]);

        var problems = ScanSkeleton.Validate(outcome);
        Assert.Single(problems);
        Assert.Contains("沒有留下原因", problems[0]);
    }

    [Fact]
    public void 骨架_中斷時保留已得事實且後續記為取消()
    {
        using var cts = new CancellationTokenSource();
        var ctx = new ScanContext(At, cts.Token);

        var outcome = ScanSkeleton.Run(ctx,
        [
            ("第一步", () => { cts.Cancel(); return ScanStepResult.Completed("第一步", TimeSpan.Zero); }),
            ("第二步", () => throw new InvalidOperationException("不該被執行")),
        ]);

        Assert.True(outcome.WasCancelled);
        Assert.Equal(ScanStepOutcome.Completed, outcome.Steps[0].Outcome);
        Assert.Equal(ScanStepOutcome.Cancelled, outcome.Steps[1].Outcome);
        Assert.Empty(ScanSkeleton.Validate(outcome));
    }

    [Fact]
    public void 骨架_不適用是環境事實不是錯誤()
    {
        var outcome = ScanSkeleton.Run(Ctx,
        [
            ("沒有 BMC 就沒有 IPMI", () => ScanStepResult.NotApplicable("沒有 BMC 就沒有 IPMI",
                TimeSpan.Zero, "本機未偵測到 BMC")),
        ]);

        Assert.Equal(ScanStepOutcome.NotApplicable, outcome.Steps[0].Outcome);
        Assert.Empty(ScanSkeleton.Validate(outcome));
    }

    [Fact]
    public void 骨架_空步驟清單也要能驗證通過()
    {
        var outcome = ScanSkeleton.Run(Ctx, []);
        Assert.Empty(outcome.Steps);
        Assert.Empty(ScanSkeleton.Validate(outcome));
        Assert.False(outcome.EnteredTransient);
        Assert.False(outcome.WasCancelled);
    }
}
