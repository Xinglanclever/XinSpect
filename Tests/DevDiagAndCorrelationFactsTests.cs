using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 DL-006/007、BL-006/009 項（開發者診斷補完＋相關性二）的事實服務測試。</summary>
public class DevDiagAndCorrelationFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 16, 0, 0, TimeSpan.Zero);

    private static DevDiagAndCorrelationFactsService.ProcessModules Proc(string name, int mods, int uns, int unv)
        => new(name, mods, uns, unv);

    // ── DL-006 逐行程 DLL 清單 ───────────────────────────────────────

    [Fact]
    public void 逐行程DLL_未同意為L2不適用()
    {
        var f = DevDiagAndCorrelationFactsService.PerProcessDllFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("未同意則永不執行", f.UnavailableReason);
    }

    [Fact]
    public void 逐行程DLL_只列計數不列路徑()
    {
        var f = DevDiagAndCorrelationFactsService.PerProcessDllFact(At,
            () => [Proc("game", 120, 3, 1), Proc("tools", 40, 0, 0)]);
        // 彙總鍵談的是視角與邊界；「不列路徑」的聲明在逐條家族鍵裡（見下一條測試）。
        Assert.Contains("L2 且是聚合視角", f.Value);
        Assert.Contains("不是「哪些行程有問題」", f.Value);
        Assert.Contains("匯出時會遮蔽模組路徑", f.Value);
        Assert.Equal(2.0, f.NumericValue);
    }

    [Fact]
    public void 逐行程DLL_明細家族鍵帶行程名與判定()
    {
        var rows = new[] { Proc("game", 120, 3, 1), Proc("tools", 40, 0, 0) };
        var entries = DevDiagAndCorrelationFactsService.PerProcessDllEntries(At, rows).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Key == "dl.dll.game" && e.Value.Contains("未簽章 3"));
        Assert.Contains(entries, e => e.Key == "dl.dll.tools");
        Assert.All(entries, e => Assert.Contains("未簽章不是惡意判決", e.Value));
    }

    // ── DL-007 句柄哨兵 ──────────────────────────────────────────────

    [Fact]
    public void 句柄哨兵_樣本不足不宣稱洩漏()
    {
        var f = DevDiagAndCorrelationFactsService.HandleLeakFact(At,
            () => new DevDiagAndCorrelationFactsService.HandleSeries([100, 101, 102], 5));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不宣稱洩漏", f.UnavailableReason);
    }

    [Fact]
    public void 句柄哨兵_無序列為不適用()
    {
        var f = DevDiagAndCorrelationFactsService.HandleLeakFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("只在", f.UnavailableReason);
    }

    [Fact]
    public void 句柄哨兵_上升標明是訊號不是判決()
    {
        var rising = Enumerable.Range(0, 30).Select(i => 100 + i * 2).ToList();
        var f = DevDiagAndCorrelationFactsService.HandleLeakFact(At,
            () => new DevDiagAndCorrelationFactsService.HandleSeries(rising, 5));
        Assert.Contains("上升趨勢站得住腳", f.Value);
        Assert.Contains("訊號不是判決", f.Value);
        Assert.Contains("單調上升可能只是取樣期間正在載入快取", f.Value);
    }

    [Fact]
    public void 句柄哨兵_下降如實說在下降()
    {
        var falling = Enumerable.Range(0, 30).Select(i => 200 - i * 3).ToList();
        var f = DevDiagAndCorrelationFactsService.HandleLeakFact(At,
            () => new DevDiagAndCorrelationFactsService.HandleSeries(falling, 5));
        Assert.Contains("在下降", f.Value);
    }

    // ── BL-006 相關性探索 ────────────────────────────────────────────

    [Fact]
    public void 相關性_樣本不足為不適用()
    {
        var f = DevDiagAndCorrelationFactsService.CorrelationFact(At,
            () => new DevDiagAndCorrelationFactsService.PairSeries([1, 2, 3], [3, 2, 1], "A", "B", 5));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("樣本不足不算相關性", f.UnavailableReason);
    }

    [Fact]
    public void 相關性_無配對序列為不適用()
    {
        var f = DevDiagAndCorrelationFactsService.CorrelationFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("兩條同行時間軸", f.UnavailableReason);
    }

    [Fact]
    public void 相關性_標明觀察不是因果()
    {
        var x = Enumerable.Range(0, 24).Select(i => 40.0 + i * 0.5).ToList();
        var y = Enumerable.Range(0, 24).Select(i => 100.0 + i * 2 + (i % 3)).ToList();
        var f = DevDiagAndCorrelationFactsService.CorrelationFact(At,
            () => new DevDiagAndCorrelationFactsService.PairSeries(x, y, "溫度", "功耗", 5));
        Assert.Contains("觀察不是因果", f.Value);
        Assert.Contains("平滑後還剩多少", f.Value);
        Assert.Contains("不代表誰造成誰", f.Value);
    }

    // ── BL-009 負載模式分類 ──────────────────────────────────────────

    [Fact]
    public void 負載分群_樣本不足為不適用()
    {
        var f = DevDiagAndCorrelationFactsService.PatternFact(At,
            () => new DevDiagAndCorrelationFactsService.PatternState(
                Enumerable.Range(0, 10).Select(i => (double)i).ToList(), 2, [5, 5], [2, 8]));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("樣本不足就不分群", f.UnavailableReason);
    }

    [Fact]
    public void 負載分群_單一群如實說只有一種型態()
    {
        var f = DevDiagAndCorrelationFactsService.PatternFact(At,
            () => new DevDiagAndCorrelationFactsService.PatternState(
                Enumerable.Range(0, 40).Select(i => 50.0).ToList(), 1, [40], [50]));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("只有一種負載型態", f.UnavailableReason);
    }

    [Fact]
    public void 負載分群_標明是推測級聲明不貼標籤()
    {
        var f = DevDiagAndCorrelationFactsService.PatternFact(At,
            () => new DevDiagAndCorrelationFactsService.PatternState(
                Enumerable.Range(0, 60).Select(i => (double)(i % 3 == 0 ? 20 : 80)).ToList(),
                2, [20, 40], [20.0, 80.0]));
        Assert.Contains("推測級聲明", f.Value);
        Assert.Contains("不對每一群貼標籤", f.Value);
        Assert.Contains("群間距 60", f.Value);
        Assert.Equal(2.0, f.NumericValue);
    }

    // ── 整批 ─────────────────────────────────────────────────────────

    [Fact]
    public void 整批_四把鍵齊且不重複()
    {
        var rising = Enumerable.Range(0, 30).Select(i => 100.0 + i * 2).ToList();
        var facts = DevDiagAndCorrelationFactsService.Collect(At,
            () => [Proc("game", 120, 3, 1)],
            () => new DevDiagAndCorrelationFactsService.HandleSeries(rising.Select(v => (int)v).ToList(), 5),
            () => new DevDiagAndCorrelationFactsService.PairSeries(rising, rising, "A", "B", 5),
            () => new DevDiagAndCorrelationFactsService.PatternState(
                Enumerable.Range(0, 60).Select(i => (double)(i % 3 == 0 ? 20 : 80)).ToList(),
                2, [20, 40], [20.0, 80.0]));

        Assert.Equal(4, facts.Count);
        Assert.Equal(4, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (string k in new[]
        {
            DevDiagAndCorrelationFactsService.PerProcessDllKey,
            DevDiagAndCorrelationFactsService.HandleLeakKey,
            DevDiagAndCorrelationFactsService.CorrelationKey,
            DevDiagAndCorrelationFactsService.PatternKey,
        })
            Assert.Contains(facts, f => f.Key == k);
    }
}
