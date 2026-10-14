using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 幀呈現觀測（GP-003）的單元測試。
/// 這一項最容易講錯的一句話是「沒有畫面在動」——它其實有三種可能：真的沒動、這個堆疊不發事件、
/// 沒觀測。測試主要在釘住「不知道」不被寫成「沒有」。
/// </summary>
public class PresentObservationFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    private static PresentObservationFactsService.PresentInputs Inputs(
        double window = 10, long dxgi = 600, long dwm = 600, long unparsed = 0,
        params (int Pid, long Events)[] perProcess)
        => new(window, perProcess.Length == 0 ? [(1234, 600)] : perProcess, dxgi, dwm, unparsed);

    // ── 未觀測 ───────────────────────────────────────────────────────────

    [Fact]
    public void 未注入取樣是不適用不是零幀()
    {
        var facts = PresentObservationFactsService.Collect(At);

        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.All(facts, f => Assert.Null(f.NumericValue));
        Assert.All(facts, f => Assert.Contains("未觀測不等於 0 幀", f.UnavailableReason));
    }

    [Fact]
    public void 未觀測時說清楚不自行開工作階段()
    {
        var reason = Fact(PresentObservationFactsService.Collect(At), "gp.present.summary").UnavailableReason;
        Assert.Contains("不自行建立新的 ETW 工作階段", reason);
        Assert.Contains("量測期間", reason);
    }

    [Fact]
    public void 觀測窗為零不換算速率()
    {
        var f = Fact(PresentObservationFactsService.Collect(At, Inputs(window: 0)), "gp.present.summary");

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("不以 0 冒充", f.UnavailableReason);
    }

    // ── liveness 三態 ────────────────────────────────────────────────────

    [Fact]
    public void 有事件時判為有畫面在動()
    {
        var p = PresentObservationFactsService.Summarize(Inputs(dxgi: 600, dwm: 600));
        Assert.Equal(PresentObservationFactsService.Liveness.Flowing, p.State);
    }

    [Fact]
    public void 完全沒事件時是沒看到不是沒有()
    {
        var facts = PresentObservationFactsService.Collect(At, Inputs(dxgi: 0, dwm: 0, perProcess: (0, 0)));
        var f = Fact(facts, "gp.present.summary");

        Assert.Contains("沒有看到", f.Value);
        Assert.Contains("沒看到", f.Value);
        Assert.Contains("不等於「沒有畫面」", f.Value);
        Assert.Contains("Server 2025", f.Value);      // 實測過的堆疊差異要寫出來
    }

    [Fact]
    public void 零星事件不足以宣稱在動也不足以宣稱沒動()
    {
        var f = Fact(PresentObservationFactsService.Collect(At,
            Inputs(dxgi: 1, dwm: 0, perProcess: (42, 1))), "gp.present.summary");

        Assert.Contains("不足以宣稱在動", f.Value);
        Assert.Contains("也不足以宣稱沒動", f.Value);
    }

    [Fact]
    public void liveness不是布林值()
    {
        // 三態是刻意的：把「不知道」壓成 false 就是把沒看到說成沒有
        var values = Enum.GetValues<PresentObservationFactsService.Liveness>();
        Assert.Equal(3, values.Length);
        Assert.Contains(PresentObservationFactsService.Liveness.Unknown, values);
    }

    // ── 兩個來源的差異 ───────────────────────────────────────────────────

    [Fact]
    public void DXGI全靜而DWM有事件時說明這是堆疊差異()
    {
        var f = Fact(PresentObservationFactsService.Collect(At,
            Inputs(dxgi: 0, dwm: 900, perProcess: (7, 900))), "gp.present.summary");

        Assert.Contains("DXGI 全靜而 DWM 有事件", f.Value);
        Assert.Contains("不是異常", f.Value);
    }

    [Fact]
    public void 兩來源都靜時直接講明()
    {
        var f = Fact(PresentObservationFactsService.Collect(At,
            Inputs(dxgi: 0, dwm: 0, perProcess: (0, 0))), "gp.present.summary");

        Assert.Contains("DXGI 與 DWM 兩個來源在觀測窗內都沒有事件", f.Value);
    }

    // ── 呈現模式不猜 ─────────────────────────────────────────────────────

    [Fact]
    public void 呈現模式未解析且只有一種值()
    {
        var p = PresentObservationFactsService.Summarize(Inputs());
        Assert.Equal(PresentObservationFactsService.PresentMode.Unresolved, p.Mode);
        Assert.Single(Enum.GetValues<PresentObservationFactsService.PresentMode>());
    }

    [Fact]
    public void 值裡明說模式未解析且說出為什麼()
    {
        var f = Fact(PresentObservationFactsService.Collect(At, Inputs()), "gp.present.summary");
        Assert.Contains("呈現模式未解析", f.Value);
        Assert.Contains("回推模式是猜的", f.Value);
    }

    // ── 隱私與分工 ───────────────────────────────────────────────────────

    [Fact]
    public void 逐行程計數不列行程名稱()
    {
        var f = Fact(PresentObservationFactsService.Collect(At, Inputs()), "gp.present.count");
        Assert.Contains("不列行程名稱", f.Value);
        Assert.Contains("匿名序號", f.Value);
    }

    [Fact]
    public void 事件數不當FPS用()
    {
        var f = Fact(PresentObservationFactsService.Collect(At, Inputs()), "gp.present.count");
        Assert.Contains("事件數不是幀數保證", f.Value);
        Assert.Contains("不當 FPS 用", f.Value);
    }

    [Fact]
    public void 摘要說出與既有幀時間頁的分工()
    {
        var f = Fact(PresentObservationFactsService.Collect(At, Inputs()), "gp.present.summary");
        Assert.Contains("幀時間漂亮不等於畫面呈現得出來", f.Value);
    }

    [Fact]
    public void 觀測窗與未解事件都寫進值裡()
    {
        var f = Fact(PresentObservationFactsService.Collect(At,
            Inputs(window: 7.5, unparsed: 12)), "gp.present.summary");

        Assert.Contains("7.5 秒", f.Value);
        Assert.Contains("12 個未解事件", f.Value);
    }

    [Fact]
    public void 速率由觀測窗換算()
    {
        var p = PresentObservationFactsService.Summarize(Inputs(window: 10, dxgi: 300, dwm: 200));
        Assert.Equal(50, p.EventsPerSecond);
    }

    [Fact]
    public void 鍵名就是目錄登記的那兩把()
    {
        var keys = PresentObservationFactsService.Collect(At).Select(f => f.Key).ToList();
        Assert.Equal(new[] { "gp.present.count", "gp.present.summary" },
            keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void 沒有逐行程事件時如實說空()
    {
        var f = Fact(PresentObservationFactsService.Collect(At,
            new PresentObservationFactsService.PresentInputs(10, [], 100, 100, 0)), "gp.present.count");
        Assert.Contains("沒有逐行程的呈現事件", f.Value);
    }
}
