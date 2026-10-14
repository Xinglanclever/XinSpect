using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 BL-002/005/008/011/012 項（基線預測二）的事實服務測試。</summary>
public class BaselineForecastFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 14, 0, 0, TimeSpan.Zero);

    // ── BL-002 溫度趨勢外推 ───────────────────────────────────────────

    [Fact]
    public void 溫度外推_樣本不足時不適用而非猜()
    {
        var f = BaselineForecastFactsService.TempForecastFact(At,
            () => new BaselineForecastFactsService.TempSeries([40, 41, 42], 5));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("寧可說不知道", f.UnavailableReason);
    }

    [Fact]
    public void 溫度外推_有趨勢時標明站得住腳並附區間()
    {
        var rising = Enumerable.Range(0, 30).Select(i => 40.0 + i * 0.5).ToList();
        var f = BaselineForecastFactsService.TempForecastFact(At,
            () => new BaselineForecastFactsService.TempSeries(rising, 5));
        Assert.Contains("趨勢站得住腳", f.Value);
        Assert.Contains("這是估計不是預言", f.Value);
        Assert.Contains("區間比中點重要", f.Value);
    }

    [Fact]
    public void 溫度外推_無方向時如實說看不出方向()
    {
        var flat = Enumerable.Range(0, 30).Select(i => 45.0 + (i % 2 == 0 ? 0.2 : -0.2)).ToList();
        var f = BaselineForecastFactsService.TempForecastFact(At,
            () => new BaselineForecastFactsService.TempSeries(flat, 5));
        Assert.Contains("還看不出方向", f.Value);
    }

    // ── BL-005 季節模式 ──────────────────────────────────────────────

    [Fact]
    public void 季節_未達門檻時不適用並說明天數()
    {
        var f = BaselineForecastFactsService.SeasonFact(At,
            () => new BaselineForecastFactsService.SeasonSamples([40], [38], 5));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("只涵蓋 5 天", f.UnavailableReason);
        Assert.Contains("分不出季節與雜訊", f.UnavailableReason);
    }

    [Fact]
    public void 季節_無樣本時不憑空生模式()
    {
        var f = BaselineForecastFactsService.SeasonFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不憑空生一個模式出來", f.UnavailableReason);
    }

    [Fact]
    public void 季節_日間較高時標明與環溫起伏一致()
    {
        var f = BaselineForecastFactsService.SeasonFact(At,
            () => new BaselineForecastFactsService.SeasonSamples(
                Enumerable.Repeat(46.0, 20).ToList(), Enumerable.Repeat(41.0, 20).ToList(), 20));
        Assert.Contains("日間比夜間高 5", f.Value);
        Assert.Contains("粗粒度代理", f.Value);
    }

    [Fact]
    public void 季節_夜間較高時提醒值得看一眼()
    {
        var f = BaselineForecastFactsService.SeasonFact(At,
            () => new BaselineForecastFactsService.SeasonSamples(
                Enumerable.Repeat(40.0, 20).ToList(), Enumerable.Repeat(45.0, 20).ToList(), 20));
        Assert.Contains("與一般日夜起伏相反", f.Value);
    }

    // ── BL-008 開機效能基線 ──────────────────────────────────────────

    [Fact]
    public void 開機基線_少於三次不當基線()
    {
        var f = BaselineForecastFactsService.BootFact(At,
            () => new BaselineForecastFactsService.BootSamples([12000, 13000]));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不拿一次開機代表常態", f.UnavailableReason);
    }

    [Fact]
    public void 開機基線_無樣本為讀取錯誤()
    {
        var f = BaselineForecastFactsService.BootFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void 開機基線_標明是本機自基線非廠商標稱()
    {
        var f = BaselineForecastFactsService.BootFact(At,
            () => new BaselineForecastFactsService.BootSamples([11000, 12000, 11500, 14000]));
        Assert.Contains("本機自基線，不是廠商標稱值", f.Value);
        Assert.Contains("跟自己比才有意義", f.Value);
        // 中位數取 sorted[count / 2]：四個點排序後 index 2 ＝ 12000（不是兩者平均）。
        Assert.Equal(12000.0, f.NumericValue);
    }

    [Fact]
    public void 開機基線_最新明顯較慢時報出差異()
    {
        var f = BaselineForecastFactsService.BootFact(At,
            () => new BaselineForecastFactsService.BootSamples([10000, 10000, 10000, 20000]));
        Assert.Contains("最新一次比中位慢", f.Value);
    }

    // ── BL-012 資料品質聲明 ──────────────────────────────────────────

    [Fact]
    public void 資料品質_讀不到時不能拿它背書()
    {
        var f = BaselineForecastFactsService.QualityFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不能拿它背書任何預測", f.UnavailableReason);
    }

    [Fact]
    public void 資料品質_具名列缺口而不是給分數()
    {
        var f = BaselineForecastFactsService.QualityFact(At,
            () => new BaselineForecastFactsService.QualityState(3, 5, [40, 35, 10], "風扇轉速、功耗樣本不足"));
        Assert.Contains("樣本不足 1 項", f.Value);
        Assert.Contains("風扇轉速、功耗樣本不足", f.Value);
        Assert.Contains("具名列缺口是刻意的", f.Value);
        Assert.Contains("沒人知道那 13 分扣在哪", f.Value);
    }

    [Fact]
    public void 資料品質_空歷史倉如實說所有預測無從談起()
    {
        var f = BaselineForecastFactsService.QualityFact(At,
            () => new BaselineForecastFactsService.QualityState(0, 12, [], "尚未累積"));
        Assert.Contains("還沒有任何指標有讀值", f.Value);
    }

    // ── BL-011 不確定性彙總 + 整批 ───────────────────────────────────

    [Fact]
    public void 整批_五把鍵齊且不重複()
    {
        var rising = Enumerable.Range(0, 30).Select(i => 40.0 + i * 0.5).ToList();
        var facts = BaselineForecastFactsService.Collect(At,
            () => new BaselineForecastFactsService.TempSeries(rising, 5),
            () => new BaselineForecastFactsService.SeasonSamples(
                Enumerable.Repeat(46.0, 20).ToList(), Enumerable.Repeat(41.0, 20).ToList(), 20),
            () => new BaselineForecastFactsService.BootSamples([11000, 12000, 11500]),
            () => new BaselineForecastFactsService.QualityState(3, 5, [40, 35, 10], "無"));

        Assert.Equal(5, facts.Count);
        Assert.Equal(5, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (string k in new[]
        {
            BaselineForecastFactsService.TempForecastKey, BaselineForecastFactsService.SeasonKey,
            BaselineForecastFactsService.BootKey, BaselineForecastFactsService.ConfidenceKey,
            BaselineForecastFactsService.QualityKey,
        })
            Assert.Contains(facts, f => f.Key == k);
    }

    [Fact]
    public void 不確定性_如實統計不可得條數()
    {
        var facts = BaselineForecastFactsService.Collect(At);   // 全部探針缺席
        var ci = facts.Single(f => f.Key == BaselineForecastFactsService.ConfidenceKey);
        Assert.Contains("不可得 4 條", ci.Value);
        Assert.Contains("不確定性要跟數字一起交付", ci.Value);
    }
}
