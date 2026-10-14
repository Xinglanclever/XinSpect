using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 VR-001/002/003 項（視覺化第二批）的事實服務測試。</summary>
public class VisualizationDeepFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 22, 0, 0, TimeSpan.Zero);

    // ── VR-001 桑基資料流圖 ─────────────────────────────────────────

    [Fact]
    public void 桑基_無資料不畫空圖()
    {
        var f = VisualizationFactsService.SankeyFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不畫空桑基圖冒充有內容", f.UnavailableReason);
    }

    [Fact]
    public void 桑基_節點或邊為零不硬畫()
    {
        var f = VisualizationFactsService.SankeyFact(At, () => (5, 0, 100));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒有流動關係就沒有圖", f.UnavailableReason);
    }

    [Fact]
    public void 桑基_條寬只是比例不是好壞判讀()
    {
        var f = VisualizationFactsService.SankeyFact(At, () => (6, 8, 930.5));
        Assert.Contains("條寬只是比例", f.Value);
        Assert.Contains("不判斷分配得好不好", f.Value);
        Assert.Contains("閒置機器與滿載機器上可以有截然不同的意義", f.Value);
        Assert.Equal(8.0, f.NumericValue);
    }

    // ── VR-002 熱力圖 ───────────────────────────────────────────────

    [Fact]
    public void 熱力圖_無樣本時空白不是低溫()
    {
        var f = VisualizationFactsService.HeatmapFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("空白不是低溫", f.UnavailableReason);
    }

    [Fact]
    public void 熱力圖_全覆蓋如實說有值()
    {
        var f = VisualizationFactsService.HeatmapFact(At, () => (4, 10, 40));
        Assert.Contains("全部格子都有值", f.Value);
        Assert.Equal(40.0, f.NumericValue);
    }

    [Fact]
    public void 熱力圖_缺格必須是缺口不能畫成最低色()
    {
        var f = VisualizationFactsService.HeatmapFact(At, () => (4, 10, 30));
        Assert.Contains("10 格沒有值", f.Value);
        Assert.Contains("缺格在圖上必須是缺口", f.Value);
        Assert.Contains("不能畫成最低色", f.Value);
        Assert.Contains("把「沒量到」看成「很低」", f.Value);
    }

    [Fact]
    public void 熱力圖_零格不畫()
    {
        var f = VisualizationFactsService.HeatmapFact(At, () => (0, 10, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒有可畫的格", f.UnavailableReason);
    }

    // ── VR-003 拓撲樹 ───────────────────────────────────────────────

    [Fact]
    public void 樹_無階層資料不畫空樹()
    {
        var f = VisualizationFactsService.TreeFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不畫空樹", f.UnavailableReason);
    }

    [Fact]
    public void 樹_明確聲明3D不做()
    {
        var f = VisualizationFactsService.TreeFact(At, () => (42, 5, 12));
        Assert.Contains("3D 明文不做", f.Value);
        Assert.Contains("要靠旋轉才看得懂父子關係", f.Value);
        Assert.Contains("沒有換到任何東西", f.Value);
        Assert.Equal(42.0, f.NumericValue);
    }

    [Fact]
    public void 樹_零節點不畫()
    {
        var f = VisualizationFactsService.TreeFact(At, () => (0, 0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒有階層就沒有樹", f.UnavailableReason);
    }

    // ── 整批（含既有五項）───────────────────────────────────────────

    [Fact]
    public void 整批_八把鍵齊且不重複()
    {
        var facts = VisualizationFactsService.Collect(At,
            gantt: () => (6, 12345),
            radar: () => (18, 15),
            zoom: () => (240, 8),
            multiAxis: () => (3, 4),
            theme: () => ("Dark", true),
            sankey: () => (6, 8, 930.5),
            heatmap: () => (4, 10, 40),
            tree: () => (42, 5, 12));

        Assert.Equal(8, facts.Count);
        Assert.Equal(8, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (string k in new[]
        {
            VisualizationFactsService.SankeyKey, VisualizationFactsService.HeatmapKey,
            VisualizationFactsService.TreeKey, VisualizationFactsService.GanttKey,
            VisualizationFactsService.RadarKey, VisualizationFactsService.ZoomKey,
            VisualizationFactsService.MultiAxisKey, VisualizationFactsService.ThemeKey,
        })
            Assert.Contains(facts, f => f.Key == k);
    }
}
