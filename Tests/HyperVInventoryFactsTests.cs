using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Hyper-V 虛擬機盤點（DV-006）單元測試。</summary>
public class HyperVInventoryFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    private static readonly IReadOnlyList<HyperVInventoryFactsService.VmRow> TwoVms =
    [
        new(true, 8L * 1073741824, 4),
        new(false, 4L * 1073741824, 2),
    ];

    [Fact]
    public void 數量_陳述執行中與已停止()
    {
        var f = Fact(HyperVInventoryFactsService.Collect(At, () => TwoVms), "dv.vm.count");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("虛擬機 2 台", f.Value);
        Assert.Contains("執行中 1 台", f.Value);
        Assert.Contains("已停止 1 台", f.Value);
        Assert.Equal(2d, f.NumericValue);
    }

    [Fact]
    public void 數量_零台是誠實空清單不是不適用()
    {
        var f = Fact(HyperVInventoryFactsService.Collect(At, () => []), "dv.vm.count");
        // Hyper-V 裝好但還沒建 VM：0 台是值，與「沒裝 Hyper-V」是兩件事。
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0d, f.NumericValue);
        Assert.Contains("沒有任何虛擬機", f.Value);
        Assert.Contains("零台是值不是缺席", f.Value);
    }

    [Fact]
    public void 數量_不列虛擬機名稱()
    {
        var f = Fact(HyperVInventoryFactsService.Collect(At, () => TwoVms), "dv.vm.count");
        Assert.Contains("不列虛擬機名稱", f.Value);
        Assert.Contains("人名與專案名的清單", f.Value);
    }

    [Fact]
    public void 數量_已停止不代表有問題()
    {
        var f = Fact(HyperVInventoryFactsService.Collect(At, () => TwoVms), "dv.vm.count");
        Assert.Contains("已停止」不代表有問題", f.Value);
    }

    [Fact]
    public void 配置_彙總記憶體與處理器()
    {
        var f = Fact(HyperVInventoryFactsService.Collect(At, () => TwoVms), "dv.vm.inventory");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("配置記憶體合計 12.00 GiB", f.Value);
        Assert.Contains("虛擬處理器合計 6 個", f.Value);
        Assert.Contains("平均每台 6.00 GiB／3.0 個 vCPU", f.Value);
    }

    [Fact]
    public void 配置_是配置值不是使用量()
    {
        var f = Fact(HyperVInventoryFactsService.Collect(At, () => TwoVms), "dv.vm.inventory");
        Assert.Contains("這是配置值不是使用量", f.Value);
        Assert.Contains("超額配置是刻意的管理決策", f.Value);
        Assert.Contains("不判斷配置合理不合理", f.Value);
    }

    [Fact]
    public void 配置_零台時零是值()
    {
        var f = Fact(HyperVInventoryFactsService.Collect(At, () => []), "dv.vm.inventory");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0d, f.NumericValue);
        Assert.Contains("0 是值", f.Value);
    }

    [Fact]
    public void HyperV不在時兩把鍵都不適用()
    {
        var facts = HyperVInventoryFactsService.Collect(At, () => null);
        foreach (var f in facts)
        {
            Assert.Equal(FactAvailability.NotApplicable, f.Availability);
            Assert.Contains("不適用", f.UnavailableReason);
            Assert.Equal("", f.Value);
        }
        // 第二把鍵的理由要講清楚「沒裝」與「裝了但零台」的分別。
        Assert.Contains("裝了但零 VM 是兩件事", Fact(facts, "dv.vm.inventory").UnavailableReason);
    }

    [Fact]
    public void 探針擲例外也走不適用而非崩潰()
    {
        var facts = HyperVInventoryFactsService.Collect(At, () => throw new InvalidOperationException("x"));
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
    }

    [Fact]
    public void 兩把鍵齊()
    {
        var facts = HyperVInventoryFactsService.Collect(At, () => TwoVms);
        Assert.Equal(new[] { "dv.vm.count", "dv.vm.inventory" },
            facts.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void 單台VM的平均不除零()
    {
        var one = new[] { new HyperVInventoryFactsService.VmRow(true, 1073741824, 2) };
        var f = Fact(HyperVInventoryFactsService.Collect(At, () => one), "dv.vm.inventory");
        Assert.Contains("平均每台 1.00 GiB／2.0 個 vCPU", f.Value);
    }
}
