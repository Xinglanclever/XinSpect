using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>滑鼠輪詢率（GP-004）單元測試。</summary>
public class MousePollRateFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(MousePollRateFactsService.PollSample? s)
        => MousePollRateFactsService.Collect(At, () => s);

    // ── 判讀：中位數而非平均 ──────────────────────────────────────────────

    [Fact]
    public void 中位數不受停頓長間隔拉低()
    {
        // 五個 1000 µs 樣本 + 一個 100 ms 停頓：平均值會被拉低到 ~17k µs，
        // 中位數仍是 1000 µs（＝1000 Hz）。這正是用中位數的理由。
        var samples = new List<double> { 1000, 1000, 1000, 1000, 1000, 100_000 };
        var s = MousePollRateFactsService.Summarize(samples, 500);
        Assert.Equal(1000, s.MedianIntervalUs);
        Assert.Equal(1000, s.Hz, 0);
    }

    [Fact]
    public void 偶數樣本中位數取中間兩個平均()
    {
        var s = MousePollRateFactsService.Summarize([1000, 2000, 3000, 4000], 500);
        Assert.Equal(2500, s.MedianIntervalUs);
        Assert.Equal(400, s.Hz, 0);
    }

    [Fact]
    public void 零樣本回零樣本數()
    {
        var s = MousePollRateFactsService.Summarize([], 500);
        Assert.Equal(0, s.Samples);
        Assert.Equal(0, s.Hz);
    }

    [Fact]
    public void 間隔為零不會除以零()
    {
        var s = MousePollRateFactsService.Summarize([0], 500);
        Assert.Equal(0, s.Hz);
    }

    // ── 檔位標籤 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1000, "≈1,000 Hz 檔")]
    [InlineData(998, "≈1,000 Hz 檔")]
    [InlineData(1002, "≈1,000 Hz 檔")]
    [InlineData(125, "≈125 Hz 檔")]
    [InlineData(500, "≈500 Hz 檔")]
    [InlineData(8000, "≈8,000 Hz 檔")]
    public void 常見檔位對得上(double hz, string expected)
        => Assert.Equal(expected, MousePollRateFactsService.NearestRateLabel(hz));

    [Fact]
    public void 偏離檔位太多時不硬歸檔()
    {
        // 700 Hz 距 500 是 40%、距 1000 是 30%，兩者都超過 10% 門檻 → 不硬歸。
        var label = MousePollRateFactsService.NearestRateLabel(700);
        Assert.Contains("未對到常見檔位", label);
        Assert.Contains("700 Hz", label);
    }

    [Fact]
    public void 未量到時標籤是未量到()
        => Assert.Equal("未量到", MousePollRateFactsService.NearestRateLabel(0));

    // ── 事實值 ───────────────────────────────────────────────────────────

    [Fact]
    public void 量到時陳述中位間隔與樣本數()
    {
        var f = Fact(MousePollRateFactsService.Summarize(
            Enumerable.Repeat(1000.0, 60).ToList(), 500));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("≈1,000 Hz 檔", f.Value);
        Assert.Contains("中位間隔 1,000 µs", f.Value);
        Assert.Contains("樣本 60 筆", f.Value);
        Assert.Contains("取樣窗 500 ms", f.Value);
        Assert.Equal(1000d, f.NumericValue);
    }

    [Fact]
    public void 零樣本是不適用不是零Hz()
    {
        var f = Fact(new MousePollRateFactsService.PollSample(0, 0, 0, true, 500));
        // 紅線①：「沒接滑鼠」與「回報率 0」是兩件事，後者物理上不存在。
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不是 0 Hz", f.UnavailableReason);
        Assert.Contains("物理上不存在", f.UnavailableReason);
        Assert.Equal("", f.Value);
    }

    [Fact]
    public void 值裡要標註取樣期間的限制()
    {
        var f = Fact(MousePollRateFactsService.Summarize(
            Enumerable.Repeat(8000.0, 20).ToList(), 500));
        Assert.Contains("取樣期間必須被記住", f.Value);
        Assert.Contains("省電模式下作業系統會主動降低取樣率", f.Value);
    }

    [Fact]
    public void 值裡要說不從型號推論()
    {
        var f = Fact(MousePollRateFactsService.Summarize(
            Enumerable.Repeat(1000.0, 20).ToList(), 500));
        Assert.Contains("不從型號推論", f.Value);
        Assert.Contains("行銷數字", f.Value);
    }

    [Fact]
    public void 裝置名稱讀不到如實標明()
    {
        var f = Fact(MousePollRateFactsService.Summarize(
            Enumerable.Repeat(1000.0, 10).ToList(), 500, deviceReadable: false));
        Assert.Contains("裝置名稱讀不到", f.Value);
    }

    [Fact]
    public void 讀取失敗是ReadError不從型號猜()
    {
        var f = Fact(null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不從裝置型號推論回報率", f.UnavailableReason);
    }

    [Fact]
    public void 探針擲例外也標ReadError()
    {
        var f = MousePollRateFactsService.Collect(At, () => throw new InvalidOperationException("x"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }
}
