using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 第 313–322 項（儲存與情境收口）的事實服務測試。
/// 重點不是「有值」，而是<b>界線</b>：磨損是估計、掉電是累計值、sanitize 只讀能力、查無不代填。
/// </summary>
public class StorageAndScenarioReconciliationFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

    private static StorageAndScenarioReconciliationFactsService.WearSample Wear(
        byte used = 0, double gib = 0, long hours = 0, byte? spare = null)
        => new(used, gib, hours, spare);

    // ── CM-021 磨損／TBW ──────────────────────────────────────────────

    [Fact]
    public void 磨損_讀不到不猜()
    {
        var f = StorageAndScenarioReconciliationFactsService.WearFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不猜", f.UnavailableReason);
    }

    [Fact]
    public void 磨損_無磁碟不適用不代表健康()
    {
        var f = StorageAndScenarioReconciliationFactsService.WearFact(At, () => []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不適用不代表健康", f.UnavailableReason);
    }

    [Fact]
    public void 磨損_標明是裝置自報估計不是量測()
    {
        var f = StorageAndScenarioReconciliationFactsService.WearFact(At,
            () => [Wear(20, 1000, 5000, 90), Wear(45, 2000, 6000, 80)]);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("最高磨損 45%", f.Value);
        Assert.Contains("估計", f.Value);
        Assert.Contains("不是本工具的量測", f.Value);
        Assert.Equal(45.0, f.NumericValue);
    }

    // ── ST-002 掉電計數 ───────────────────────────────────────────────

    [Fact]
    public void 掉電_讀不到不畫成0次()
    {
        var f = StorageAndScenarioReconciliationFactsService.PowerLossFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不畫成 0 次", f.UnavailableReason);
    }

    [Fact]
    public void 掉電_是累計自報值不推論頻率()
    {
        var f = StorageAndScenarioReconciliationFactsService.PowerLossFact(At, () => (7UL, 100UL));
        Assert.Contains("累計", f.Value);
        Assert.Contains("沒有「多久掉一次」的推論", f.Value);
        Assert.Equal(7.0, f.NumericValue);
    }

    // ── ST-001 sanitize 能力 ──────────────────────────────────────────

    [Fact]
    public void sanitize_未對準出處不出值()
    {
        var f = StorageAndScenarioReconciliationFactsService.SanitizeFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("查無不代填", f.UnavailableReason);
    }

    [Fact]
    public void sanitize_只陳述能力永不發命令()
    {
        var f = StorageAndScenarioReconciliationFactsService.SanitizeFact(At, () => true);
        Assert.Contains("永不發 sanitize 命令", f.Value);
        Assert.Contains("這裡只陳述能力位", f.Value);
        Assert.Contains("也不會執行", f.Value);
    }

    // ── SC-005 風扇軸承哨兵 ───────────────────────────────────────────

    [Fact]
    public void 風扇_單點不判讀()
    {
        var f = StorageAndScenarioReconciliationFactsService.FanBearingFact(At,
            () => ([1000.0], [40.0]));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 風扇_退化是訊號不是判決()
    {
        var f = StorageAndScenarioReconciliationFactsService.FanBearingFact(At,
            () => ([1000.0, 1050.0, 1100.0, 1200.0], [40.0, 41.0, 42.0, 43.0]));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("退化訊號不是判決", f.Value);
    }

    // ── SC-008 溫度分位數基線 ─────────────────────────────────────────

    [Fact]
    public void 溫度基線_樣本不足不宣稱()
    {
        var f = StorageAndScenarioReconciliationFactsService.ThermalBaselineFact(At,
            () => [40.0, 41.0, 42.0]);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("樣本量不足", f.UnavailableReason);
    }

    [Fact]
    public void 溫度基線_樣本足聲明樣本量()
    {
        var series = Enumerable.Range(0, 20).Select(i => 40.0 + i).ToList();
        var f = StorageAndScenarioReconciliationFactsService.ThermalBaselineFact(At, () => series);
        Assert.Contains("樣本 20 點", f.Value);
        Assert.Contains("基線樣本量已聲明", f.Value);
    }

    // ── SC-003 開機階段 ───────────────────────────────────────────────

    [Fact]
    public void 開機階段_讀不到不猜()
    {
        var f = StorageAndScenarioReconciliationFactsService.BootPhasesFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void 開機階段_是單次觀察非現況()
    {
        var f = StorageAndScenarioReconciliationFactsService.BootPhasesFact(At,
            () => ([("韌體", 3000.0), ("驅動", 12000.0), ("服務", 4000.0)]));
        Assert.Contains("最耗時「驅動」", f.Value);
        Assert.Contains("一次開機的觀察", f.Value);
    }

    // ── SC-006 換零件對照 ─────────────────────────────────────────────

    [Fact]
    public void 換零件_缺快照不判()
    {
        var f = StorageAndScenarioReconciliationFactsService.SwapFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("缺一不判", f.UnavailableReason);
    }

    [Fact]
    public void 換零件_事實變不等於換零件()
    {
        var f = StorageAndScenarioReconciliationFactsService.SwapFact(At, () => (3, 1, 2));
        Assert.Contains("不等於換了零件", f.Value);
        Assert.Equal(3.0, f.NumericValue);
    }

    // ── BL-003 壽命預估 ───────────────────────────────────────────────

    [Fact]
    public void 壽命_缺磨損不估()
    {
        var f = StorageAndScenarioReconciliationFactsService.LifeFact(At, []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("缺一不估", f.UnavailableReason);
    }

    [Fact]
    public void 壽命_零磨損不外推為無限()
    {
        var wear = StorageAndScenarioReconciliationFactsService.WearFact(At, () => [Wear(0, 0, 100)]);
        var f = StorageAndScenarioReconciliationFactsService.LifeFact(At, [wear]);
        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("0% 不是「壽命無限」", f.Value);
    }

    [Fact]
    public void 壽命_是估計不是預言()
    {
        var wear = StorageAndScenarioReconciliationFactsService.WearFact(At, () => [Wear(30, 0, 100)]);
        var f = StorageAndScenarioReconciliationFactsService.LifeFact(At, [wear]);
        Assert.Contains("估計不是預言", f.Value);
    }

    // ── 整批鍵齊 ──────────────────────────────────────────────────────

    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = StorageAndScenarioReconciliationFactsService.Collect(At);
        string[] keys =
        [
            StorageAndScenarioReconciliationFactsService.WearKey,
            StorageAndScenarioReconciliationFactsService.PowerLossKey,
            StorageAndScenarioReconciliationFactsService.SanitizeKey,
            StorageAndScenarioReconciliationFactsService.NvmeErrorKey,
            StorageAndScenarioReconciliationFactsService.NvmeExtraKey,
            StorageAndScenarioReconciliationFactsService.FanBearingKey,
            StorageAndScenarioReconciliationFactsService.ThermalBaselineKey,
            StorageAndScenarioReconciliationFactsService.BootPhasesKey,
            StorageAndScenarioReconciliationFactsService.SwapKey,
            StorageAndScenarioReconciliationFactsService.LifeKey,
        ];
        foreach (string k in keys)
            Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(10, facts.Count);
        Assert.Equal(10, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
