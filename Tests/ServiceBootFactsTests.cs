using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>服務就緒時間線（PO-005）單元測試。</summary>
public class ServiceBootFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Boot = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    private static ServiceBootFactsService.BootTimeline Timeline(params double[] secs)
        => new(Boot, secs.Select((s, i) =>
            new ServiceBootFactsService.ServiceStart($"服務 #{i + 1}", s, false)).ToList());

    // ── 時間換算 ─────────────────────────────────────────────────────────

    [Fact]
    public void 相對秒數由絕對時間相減得出()
    {
        var got = ServiceBootFactsService.SecondsFromBoot(Boot, Boot.AddSeconds(4.5));
        Assert.Equal(4.5, got!.Value, 3);
    }

    [Fact]
    public void 事件早於開機時間回null不回零()
    {
        // 出 0 秒會被讀成「開機瞬間就起來了」——那比誠實說「來源對不上」糟得多。
        Assert.Null(ServiceBootFactsService.SecondsFromBoot(Boot, Boot.AddSeconds(-30)));
    }

    // ── 摘要統計 ─────────────────────────────────────────────────────────

    [Fact]
    public void 摘要取中位數而非平均()
    {
        var starts = new List<ServiceBootFactsService.ServiceStart>
        {
            new("a", 1, false), new("b", 2, false), new("c", 3, false),
            new("d", 4, false), new("e", 300, false),   // 一個極慢的
        };
        var (count, fastest, slowest, median, _) = ServiceBootFactsService.Summarize(starts);
        Assert.Equal(5, count);
        Assert.Equal(1, fastest!.Value, 3);
        Assert.Equal(300, slowest!.Value, 3);
        Assert.Equal(3, median!.Value, 3);      // 平均會是 62，被那個 300 拉走
    }

    [Fact]
    public void 偶數樣本中位數取中間兩個平均()
    {
        var starts = new List<ServiceBootFactsService.ServiceStart>
        {
            new("a", 2, false), new("b", 4, false),
        };
        var (_, _, _, median, _) = ServiceBootFactsService.Summarize(starts);
        Assert.Equal(3, median!.Value, 3);
    }

    [Fact]
    public void 零樣本摘要全部回null()
    {
        var (count, fastest, slowest, median, triggers) =
            ServiceBootFactsService.Summarize([]);
        Assert.Equal(0, count);
        Assert.Null(fastest);
        Assert.Null(slowest);
        Assert.Null(median);
        Assert.Equal(0, triggers);
    }

    [Fact]
    public void 觸發型分開計數()
    {
        var starts = new List<ServiceBootFactsService.ServiceStart>
        {
            new("a", 1, false), new("b", 2, true), new("c", 3, true),
        };
        var (_, _, _, _, triggers) = ServiceBootFactsService.Summarize(starts);
        Assert.Equal(2, triggers);
    }

    // ── 事實值 ───────────────────────────────────────────────────────────

    [Fact]
    public void 陳述中位與極值()
    {
        var f = Fact(ServiceBootFactsService.Collect(At, () => Timeline(1, 2, 3, 4, 300)),
            "po.svcboot.summary");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("量測 5 個服務", f.Value);
        Assert.Contains("最快 1.0 秒", f.Value);
        Assert.Contains("中位 3.0 秒", f.Value);
        Assert.Contains("最慢 300.0 秒", f.Value);
        Assert.Equal(3d, f.NumericValue);
    }

    [Fact]
    public void 值裡標明時間基準()
    {
        var f = Fact(ServiceBootFactsService.Collect(At, () => Timeline(2, 3)), "po.svcboot.summary");
        Assert.Contains("時間基準", f.Value);
        Assert.Contains("開機時間", f.Value);
    }

    [Fact]
    public void 基準取不到時明確標示不可靠()
    {
        var t = new ServiceBootFactsService.BootTimeline(null,
            [new ServiceBootFactsService.ServiceStart("a", 1, false)]);
        var f = Fact(ServiceBootFactsService.Collect(At, () => t), "po.svcboot.summary");
        Assert.Contains("時間基準取不到", f.Value);
        Assert.Contains("相對秒數不可靠", f.Value);
    }

    [Fact]
    public void 不評級不裁決慢()
    {
        var f = Fact(ServiceBootFactsService.Collect(At, () => Timeline(12)), "po.svcboot.summary");
        Assert.Contains("時間是事實，原因不是", f.Value);
        Assert.Contains("可能是它本來就大", f.Value);
    }

    [Fact]
    public void 說明已在跑與幾秒才跑是兩件事()
    {
        var f = Fact(ServiceBootFactsService.Collect(At, () => Timeline(1)), "po.svcboot.summary");
        Assert.Contains("「已經在跑」與「幾秒才開始跑」是兩件事", f.Value);
    }

    [Fact]
    public void 不列服務名稱()
    {
        var f = Fact(ServiceBootFactsService.Collect(At, () => Timeline(1)), "po.svcboot.summary");
        Assert.Contains("不列服務名稱", f.Value);
    }

    [Fact]
    public void 零樣本是不適用不是零秒()
    {
        var f = Fact(ServiceBootFactsService.Collect(At, () => Timeline()), "po.svcboot.summary");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0d, f.NumericValue);
        Assert.Contains("不適用", f.Value);
        Assert.Contains("不是「0 秒就緒」", f.Value);
    }

    [Fact]
    public void 觸發型與自動型不可直接比大小()
    {
        var starts = new List<ServiceBootFactsService.ServiceStart>
        {
            new("a", 1, false), new("b", 9, true),
        };
        var f = Fact(ServiceBootFactsService.Collect(At,
            () => new ServiceBootFactsService.BootTimeline(Boot, starts)), "po.svcboot.trigger");
        Assert.Contains("比的是蘋果和橘子", f.Value);
        Assert.Contains("觸發型是<b>刻意的</b>設計", f.Value);
        Assert.Contains("慢不是它的缺陷", f.Value);
        Assert.Equal(1d, f.NumericValue);
    }

    [Fact]
    public void 零樣本時觸發型數不宣稱沒有觸發型服務()
    {
        var f = Fact(ServiceBootFactsService.Collect(At, () => Timeline()), "po.svcboot.trigger");
        Assert.Contains("不是「沒有觸發型服務」", f.Value);
    }

    [Fact]
    public void 讀取失敗兩把鍵都是ReadError()
    {
        var facts = ServiceBootFactsService.Collect(At, () => null);
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.Contains("不畫成 0 個服務", Fact(facts, "po.svcboot.summary").UnavailableReason);
    }

    [Fact]
    public void 探針擲例外也是ReadError()
    {
        var facts = ServiceBootFactsService.Collect(At, () => throw new InvalidOperationException("x"));
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }

    [Fact]
    public void 兩把鍵齊()
    {
        var facts = ServiceBootFactsService.Collect(At, () => Timeline(1, 2));
        Assert.Equal(new[] { "po.svcboot.summary", "po.svcboot.trigger" },
            facts.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
    }
}
