using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>CPPC 處理器效能控制（AC-002）單元測試。</summary>
public class CppcCapabilityFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(CppcCapabilityFactsService.CppcState? s)
        => CppcCapabilityFactsService.Collect(At, () => s);

    [Fact]
    public void 硬體有能力且已啟用()
    {
        var f = Fact(new CppcCapabilityFactsService.CppcState(true, true, true, 12));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("硬體回報具備 CPPC／HWP 能力", f.Value);
        Assert.Contains("已啟用", f.Value);
        Assert.Contains("EPP）設定可讀", f.Value);
        Assert.Equal(2d, f.NumericValue);
    }

    [Fact]
    public void 硬體有能力但未見啟用痕跡要分開講()
    {
        var f = Fact(new CppcCapabilityFactsService.CppcState(true, false, true, 0));
        // 這一條是本項的核心：有能力不等於正在用。
        Assert.Contains("硬體回報具備 CPPC／HWP 能力", f.Value);
        Assert.Contains("未見啟用痕跡", f.Value);
        Assert.Contains("硬體有能力不等於正在使用它", f.Value);
    }

    [Fact]
    public void 能力位未回報不代填成不支援()
    {
        var f = Fact(new CppcCapabilityFactsService.CppcState(false, true, false, 3));
        Assert.Contains("未回報", f.Value);
        Assert.Contains("讀不到不代填", f.Value);
    }

    [Fact]
    public void EPP讀不到不猜偏好值()
    {
        var f = Fact(new CppcCapabilityFactsService.CppcState(true, true, false, 5));
        Assert.Contains("EPP 設定<b>讀不到</b>", f.Value);
        Assert.Contains("不猜偏好值", f.Value);
    }

    [Fact]
    public void 完全沒有能力是不適用不是缺陷()
    {
        var f = Fact(new CppcCapabilityFactsService.CppcState(false, false, false, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不適用，不是缺陷", f.UnavailableReason);
        Assert.Contains("平台設計選擇", f.UnavailableReason);
    }

    [Fact]
    public void 值裡要說明不解碼AML的理由()
    {
        var f = Fact(new CppcCapabilityFactsService.CppcState(true, true, true, 1));
        Assert.Contains("本項不解碼 AML", f.Value);
        Assert.Contains("解不出來卻有值", f.Value);
    }

    [Fact]
    public void 讀取失敗是ReadError不從型號猜()
    {
        var f = Fact(null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不用 CPU 型號猜有沒有 CPPC", f.UnavailableReason);
        Assert.Equal("", f.Value);
    }

    [Fact]
    public void 探針擲例外也標ReadError()
    {
        var f = CppcCapabilityFactsService.Collect(At, () => throw new InvalidOperationException("x"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void 不斷言CPPC的效能品質()
    {
        var f = Fact(new CppcCapabilityFactsService.CppcState(true, true, true, 2));
        Assert.Contains("不從「有沒有支援」推論", f.Value);
    }
}
