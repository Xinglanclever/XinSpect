using System;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>背景服務模式（AG-005）單元測試。</summary>
public class ServiceModeFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(ServiceModeFactsService.ServiceModeInputs? s)
        => ServiceModeFactsService.Collect(At, () => s);

    // ── 成本估算（純函式）──────────────────────────────────────────────

    [Theory]
    [InlineData(1, 600, 600)]
    [InlineData(10, 600, 60)]
    [InlineData(0, 600, 0)]        // 間隔 0 不除零
    [InlineData(5, 0, 0)]
    public void 每秒查詢量估算(int seconds, int facts, double expected)
        => Assert.Equal(expected, ServiceModeFactsService.ReadsPerSecond(seconds, facts), 3);

    [Theory]
    [InlineData(100, 40, 0.6)]
    [InlineData(100, 0, 1.0)]
    [InlineData(100, 100, 0.0)]
    [InlineData(0, 0, 0.0)]        // 事實 0 不除零
    [InlineData(10, 20, 0.0)]      // clamp 下限
    public void 可快取比率(int facts, int vol, double expected)
        => Assert.Equal(expected, ServiceModeFactsService.CacheableRatio(facts, vol), 3);

    // ── 事實值 ───────────────────────────────────────────────────────────

    [Fact]
    public void 陳述事實數與可快取比率()
    {
        var f = Fact(new ServiceModeFactsService.ServiceModeInputs(600, 150, 3, true, 3));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("本輪事實 600 項", f.Value);
        Assert.Contains("75% 可快取", f.Value);
        Assert.Equal(600d, f.NumericValue);
    }

    [Fact]
    public void 說出常駐成本量級()
    {
        var f = Fact(new ServiceModeFactsService.ServiceModeInputs(600, 150, 0, true, 0));
        Assert.Contains("1 秒輪詢約 600 次查詢／秒", f.Value);
        Assert.Contains("10 秒輪詢約 60 次／秒", f.Value);
    }

    [Fact]
    public void 明說不建立服務不註冊排程()
    {
        var f = Fact(new ServiceModeFactsService.ServiceModeInputs(100, 20, 0, true, 0));
        // 這是本項的存在理由：回答「如果要，缺什麼」，不是「已經有了」。
        Assert.Contains("不建立服務、不註冊排程、不寫入任何東西", f.Value);
        Assert.Contains("「可以做」與「正在做」是兩件事", f.Value);
    }

    [Fact]
    public void 寫入型能力在服務模式下風險放大()
    {
        var f = Fact(new ServiceModeFactsService.ServiceModeInputs(100, 20, 5, true, 2));
        Assert.Contains("5 項寫入型能力", f.Value);
        Assert.Contains("<b>3 項沒有</b>", f.Value);
        Assert.Contains("互動模式有人看著，服務模式沒有", f.Value);
    }

    [Fact]
    public void 寫入能力全部有閘門時不嚇人()
    {
        var f = Fact(new ServiceModeFactsService.ServiceModeInputs(100, 20, 4, true, 4));
        Assert.Contains("全部都有閘門保護", f.Value);
        // 不能只找「沒有」二字——值裡本來就有「沒有寫入型能力」與「服務模式沒有」這類句子。
        // 要斷的是「未受閘門保護的項數」那個具體說法。
        Assert.DoesNotContain("項沒有", f.Value);
    }

    [Fact]
    public void 沒有寫入能力時明說()
    {
        var f = Fact(new ServiceModeFactsService.ServiceModeInputs(100, 20, 0, true, 0));
        Assert.Contains("沒有寫入型能力", f.Value);
    }

    [Fact]
    public void 沒有headless出口時標明是前提缺口()
    {
        var f = Fact(new ServiceModeFactsService.ServiceModeInputs(100, 20, 0, false, 0));
        Assert.Contains("沒有不經 GUI 的輸出路徑", f.Value);
        Assert.Contains("必須先補的一塊", f.Value);
    }

    [Fact]
    public void 有headless出口時如實說()
    {
        var f = Fact(new ServiceModeFactsService.ServiceModeInputs(100, 20, 0, true, 0));
        Assert.Contains("有不經 GUI 的輸出路徑（CLI）", f.Value);
    }

    [Fact]
    public void 點出快取是常駐的第一個問題()
    {
        var f = Fact(new ServiceModeFactsService.ServiceModeInputs(600, 150, 0, true, 0));
        Assert.Contains("哪些事實該重讀、哪些該快取", f.Value);
        Assert.Contains("拿 CPU 換同一個答案", f.Value);
    }

    [Fact]
    public void 不判哪種執行身分才對()
    {
        var f = Fact(new ServiceModeFactsService.ServiceModeInputs(100, 20, 0, true, 0));
        Assert.Contains("三種不同的權限輪廓", f.Value);
        Assert.Contains("不判哪個對", f.Value);
    }

    [Fact]
    public void 讀取失敗是ReadError不宣稱可以或不可以()
    {
        var f = Fact(null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不宣稱可以或不可以當服務跑", f.UnavailableReason);
        Assert.Equal("", f.Value);
    }

    [Fact]
    public void 探針擲例外也是ReadError()
    {
        var f = ServiceModeFactsService.Collect(At, () => throw new InvalidOperationException("x"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }
}
