using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 385–394 項（深測方法學）的事實服務測試。</summary>
public class MeasurementMethodologyFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 5, 30, 0, TimeSpan.Zero);

    [Fact]
    public void DPC_未啟動ETW不宣稱()
    {
        var f = MeasurementMethodologyFactsService.DpcFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("session 有開銷", f.UnavailableReason);
    }

    [Fact]
    public void DPC_標session開銷且p99非平均()
    {
        var f = MeasurementMethodologyFactsService.DpcFact(At, () => (250, 8));
        Assert.Contains("session 開銷標註", f.Value);
        Assert.Contains("p99 比平均", f.Value);
    }

    [Fact]
    public void 跨NUMA_單節點不適用()
    {
        var f = MeasurementMethodologyFactsService.XNumaFact(At, () => (500, 1));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("單一 NUMA 節點", f.UnavailableReason);
    }

    [Fact]
    public void 熱tau_擬合是模型非事實()
    {
        var f = MeasurementMethodologyFactsService.TauFact(At, () => (12.5, true));
        Assert.Contains("擬合＝模型非事實", f.Value);
        Assert.Contains("等效近似", f.Value);
    }

    [Fact]
    public void 風扇曲線_樣本不足不宣稱()
    {
        var f = MeasurementMethodologyFactsService.FanCurveFact(At, () => (2, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 風扇曲線_閾值滯後聲明()
    {
        var f = MeasurementMethodologyFactsService.FanCurveFact(At, () => (8, 3.5));
        Assert.Contains("滯後", f.Value);
        Assert.Contains("模型", f.Value);
    }

    [Fact]
    public void 核心間延遲_樣本不足不宣稱()
    {
        var f = MeasurementMethodologyFactsService.CoreLatFact(At, () => (0, 1));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 核心間延遲_標同socket範圍()
    {
        var f = MeasurementMethodologyFactsService.CoreLatFact(At, () => (45, 12));
        Assert.Contains("同 socket", f.Value);
        Assert.Contains("跨 socket 未施測", f.Value);
    }

    [Fact]
    public void 內容切換_未量不宣稱()
    {
        var f = MeasurementMethodologyFactsService.CtxSwFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 內容切換_標量測期間且相對基準()
    {
        var f = MeasurementMethodologyFactsService.CtxSwFact(At, () => (1500, 500));
        Assert.Contains("量測期間標註", f.Value);
        Assert.Contains("相對基準", f.Value);
    }

    [Fact]
    public void 中斷親和_無樣本不推論()
    {
        var f = MeasurementMethodologyFactsService.IrqAffFact(At, () => (0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 中斷親和_取樣瞬間不列裝置名()
    {
        var f = MeasurementMethodologyFactsService.IrqAffFact(At, () => (15, 4));
        Assert.Contains("取樣瞬間", f.Value);
        Assert.Contains("不列裝置名稱", f.Value);
    }

    [Fact]
    public void 睡眠_ModernStandby未施測標明()
    {
        var f = MeasurementMethodologyFactsService.SleepFact(At, () => (1200, 800, false));
        Assert.Contains("Modern Standby 未施測", f.Value);
        Assert.Contains("一律標明", f.Value);
    }

    [Fact]
    public void 環回_在零網路原則內()
    {
        var f = MeasurementMethodologyFactsService.LoopbackFact(At, () => (8.5, 200L * 1024 * 1024));
        Assert.Contains("零網路原則內", f.Value);
        Assert.Contains("不是網卡吞吐", f.Value);
    }

    [Fact]
    public void PSU效率_缺資料不估()
    {
        var f = MeasurementMethodologyFactsService.PsuEffFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("缺一不估", f.UnavailableReason);
    }

    [Fact]
    public void PSU效率_明標估計非量測()
    {
        var f = MeasurementMethodologyFactsService.PsuEffFact(At, () => (88.5, true));
        Assert.Contains("明標估計", f.Value);
        Assert.Contains("不是量測", f.Value);
    }

    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = MeasurementMethodologyFactsService.Collect(At);
        string[] keys =
        [
            MeasurementMethodologyFactsService.DpcKey, MeasurementMethodologyFactsService.XNumaKey,
            MeasurementMethodologyFactsService.TauKey, MeasurementMethodologyFactsService.FanCurveKey,
            MeasurementMethodologyFactsService.CoreLatKey, MeasurementMethodologyFactsService.CtxSwKey,
            MeasurementMethodologyFactsService.IrqAffKey, MeasurementMethodologyFactsService.SleepKey,
            MeasurementMethodologyFactsService.LoopbackKey, MeasurementMethodologyFactsService.PsuEffKey,
        ];
        foreach (string k in keys) Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(10, facts.Count);
        Assert.Equal(10, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
