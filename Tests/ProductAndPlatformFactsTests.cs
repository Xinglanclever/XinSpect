using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 378–384 項（產品與平台）的事實服務測試。</summary>
public class ProductAndPlatformFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 行程樹_未optin不列舉()
    {
        var f = ProductAndPlatformFactsService.ProcTreeFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("未觸發永不列舉", f.UnavailableReason);
    }

    [Fact]
    public void 行程樹_瞬時快照且不列名稱()
    {
        var f = ProductAndPlatformFactsService.ProcTreeFact(At, () => (120, 3, true));
        Assert.Contains("瞬時快照聲明", f.Value);
        Assert.Contains("不列行程名稱、路徑或父行程", f.Value);
        Assert.Equal(120.0, f.NumericValue);
    }

    [Fact]
    public void 工作集_未optin不列舉()
    {
        var f = ProductAndPlatformFactsService.WsFact(At, () => (10, false));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 工作集_只列規模不列名稱()
    {
        var f = ProductAndPlatformFactsService.WsFact(At, () => (30, true));
        Assert.Contains("不列行程名稱", f.Value);
        Assert.Equal(30.0, f.NumericValue);
    }

    [Fact]
    public void 系統呼叫_未量不宣稱()
    {
        var f = ProductAndPlatformFactsService.SyscallFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 系統呼叫_標量測期間非固定成本()
    {
        var f = ProductAndPlatformFactsService.SyscallFact(At, () => (150, 1000));
        Assert.Contains("量測期間標註", f.Value);
        Assert.Contains("不是核心的固定成本", f.Value);
    }

    [Fact]
    public void UI品質_零是值不是沒掃()
    {
        var f = ProductAndPlatformFactsService.UiQualityFact(At, () => (0, 0));
        Assert.Contains("0 是值", f.Value);
        Assert.Contains("不是「沒掃」", f.Value);
    }

    [Fact]
    public void 截圖基線_不符是訊號需人確認()
    {
        var f = ProductAndPlatformFactsService.ShotBaselineFact(At, () => (3, 3, false));
        Assert.Contains("版面漂移", f.Value);
        Assert.Contains("需人工確認", f.Value);
    }

    [Fact]
    public void 首載預算_超預算非壞掉()
    {
        var f = ProductAndPlatformFactsService.PageBudgetFact(At, () => (20, 2));
        Assert.Contains("效能門檻", f.Value);
        Assert.Contains("不是「壞掉」", f.Value);
        Assert.Equal(2.0, f.NumericValue);
    }

    [Fact]
    public void 雙包裝_未定案明文需討論()
    {
        var f = ProductAndPlatformFactsService.PackageFact(At, () => (true, true, false));
        Assert.Contains("需討論", f.Value);
        Assert.Contains("不假裝已決定", f.Value);
    }

    [Fact]
    public void 雙包裝_性格改變聲明()
    {
        var f = ProductAndPlatformFactsService.PackageFact(At, () => (true, true, true));
        Assert.Contains("性格改變聲明", f.Value);
        Assert.Contains("零寫入宣稱不同", f.Value);
    }

    [Fact]
    public void 本批七把鍵齊()
    {
        var facts = ProductAndPlatformFactsService.Collect(At);
        string[] keys =
        [
            ProductAndPlatformFactsService.ProcTreeKey, ProductAndPlatformFactsService.WsKey,
            ProductAndPlatformFactsService.SyscallKey, ProductAndPlatformFactsService.UiQualityKey,
            ProductAndPlatformFactsService.ShotBaselineKey, ProductAndPlatformFactsService.PageBudgetKey,
            ProductAndPlatformFactsService.PackageKey,
        ];
        foreach (string k in keys) Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(7, facts.Count);
        Assert.Equal(7, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
