using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 366–370 項（視覺化）的事實服務測試。</summary>
public class VisualizationFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 甘特_無資料不畫空圖()
    {
        var f = VisualizationFactsService.GanttFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不畫空甘特圖", f.UnavailableReason);
    }

    [Fact]
    public void 甘特_是呈現不新增判決()
    {
        var f = VisualizationFactsService.GanttFact(At, () => (5, 12000));
        Assert.Contains("呈現", f.Value);
        Assert.Contains("不新增任何判決", f.Value);
        Assert.Equal(5.0, f.NumericValue);
    }

    [Fact]
    public void 雷達_無資料不畫空圖()
    {
        var f = VisualizationFactsService.RadarFact(At, () => (0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不畫空雷達圖", f.UnavailableReason);
    }

    [Fact]
    public void 雷達_缺席不讀成不支援()
    {
        var f = VisualizationFactsService.RadarFact(At, () => (9, 6));
        Assert.Contains("缺席不讀成不支援", f.Value);
        Assert.Contains("不是評分", f.Value);
    }

    [Fact]
    public void 縮放_放大不增加樣本()
    {
        var f = VisualizationFactsService.ZoomFact(At, () => (100, 2));
        Assert.Contains("檢視", f.Value);
        Assert.Contains("放大不會增加樣本數", f.Value);
        Assert.Equal(100.0, f.NumericValue);
    }

    [Fact]
    public void 多軸_提醒虛假相關()
    {
        var f = VisualizationFactsService.MultiAxisFact(At, () => (3, 4));
        Assert.Contains("虛假的相關", f.Value);
        Assert.Contains("不能靠目視", f.Value);
    }

    [Fact]
    public void 多軸_無資料不適用()
    {
        var f = VisualizationFactsService.MultiAxisFact(At, () => (0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 主題_只描述不落檔且不影響事實()
    {
        var f = VisualizationFactsService.ThemeFact(At, () => ("Dark", true));
        Assert.Contains("描述字串", f.Value);
        Assert.Contains("不落檔", f.Value);
        Assert.Contains("不會改變任何事實值", f.Value);
    }

    [Fact]
    public void 本批八把鍵齊()
    {
        // 2026-10-14：VR-001/002/003（桑基／熱力圖／拓撲樹）併入同一支服務，故 5 → 8。
        var facts = VisualizationFactsService.Collect(At);
        string[] keys =
        [
            VisualizationFactsService.GanttKey, VisualizationFactsService.RadarKey,
            VisualizationFactsService.ZoomKey, VisualizationFactsService.MultiAxisKey,
            VisualizationFactsService.ThemeKey, VisualizationFactsService.SankeyKey,
            VisualizationFactsService.HeatmapKey, VisualizationFactsService.TreeKey,
        ];
        foreach (string k in keys) Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(8, facts.Count);
        Assert.Equal(8, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
