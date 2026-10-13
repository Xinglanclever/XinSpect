using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 第 323–332 項（效能觀測）的事實服務測試。
/// 重點在界線：計數 0 是值、趨勢需長期、推測級要標明、不列行程名稱。
/// </summary>
public class PerformanceObservationFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 1, 0, 0, TimeSpan.Zero);

    // PO-001 分頁池
    [Fact]
    public void 分頁池_讀不到不猜()
    {
        var f = PerformanceObservationFactsService.PoolFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不猜", f.UnavailableReason);
    }

    [Fact]
    public void 分頁池_標明單次取樣看不出趨勢()
    {
        var f = PerformanceObservationFactsService.PoolFact(At, () => new(100, 50, 8000));
        Assert.Contains("分頁池 100", f.Value);
        Assert.Contains("單次取樣看不出趨勢", f.Value);
        Assert.Equal(100.0, f.NumericValue);
    }

    // PO-002 記憶體壓縮
    [Fact]
    public void 壓縮_無計數器不代填()
    {
        var f = PerformanceObservationFactsService.CompressionFact(At, () => null);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("查無不代填", f.UnavailableReason);
    }

    [Fact]
    public void 壓縮_零是值不是停用()
    {
        var f = PerformanceObservationFactsService.CompressionFact(At, () => (0, 5000));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("0 是值", f.Value);
    }

    // PO-003 頁面錯誤
    [Fact]
    public void 頁面錯誤_讀不到畫不為0()
    {
        var f = PerformanceObservationFactsService.PageFaultFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不畫成 0", f.UnavailableReason);
    }

    [Fact]
    public void 頁面錯誤_說明軟錯誤是常態()
    {
        var f = PerformanceObservationFactsService.PageFaultFact(At, () => (1000000UL, 5000));
        Assert.Contains("常態", f.Value);
        Assert.Contains("取樣瞬間", f.Value);
    }

    // PO-004 ReadyBoost
    [Fact]
    public void ReadyBoost_無裝置不適用()
    {
        var f = PerformanceObservationFactsService.ReadyBoostFact(At, () => (false, false));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("未使用不代表有問題", f.Value);
    }

    [Fact]
    public void ReadyBoost_有裝置列啟用狀態()
    {
        var f = PerformanceObservationFactsService.ReadyBoostFact(At, () => (true, true));
        Assert.Contains("已啟用", f.Value);
        Assert.Equal(1.0, f.NumericValue);
    }

    // PO-008 handle
    [Fact]
    public void handle_讀不到不猜()
    {
        var f = PerformanceObservationFactsService.HandlesFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void handle_不列handle目標或行程名稱()
    {
        var f = PerformanceObservationFactsService.HandlesFact(At, () => (500, 120000));
        Assert.Contains("不列 handle 目標或行程名稱", f.Value);
        Assert.Equal(120000.0, f.NumericValue);
    }

    // PO-009 GDI
    [Fact]
    public void GDI_是本行程用量非系統總量()
    {
        var f = PerformanceObservationFactsService.GdiFact(At, () => (120u, 80u));
        Assert.Contains("本行程", f.Value);
        Assert.Equal(120.0, f.NumericValue);
    }

    // PO-011 頻率波動
    [Fact]
    public void 頻率波動_單點不宣稱()
    {
        var f = PerformanceObservationFactsService.FreqVarFact(At, () => [3000.0]);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 頻率波動_零頻率不計算()
    {
        var f = PerformanceObservationFactsService.FreqVarFact(At, () => [0.0, 0.0]);
        Assert.Equal(FactAvailability.Unknown, f.Availability);
    }

    [Fact]
    public void 頻率波動_變異係數不是效能判決()
    {
        var f = PerformanceObservationFactsService.FreqVarFact(At, () => [3000.0, 3100.0, 2900.0, 3050.0]);
        Assert.Contains("變異係數", f.Value);
        Assert.Contains("不是效能判決", f.Value);
    }

    // PO-013 計時器解析度
    [Fact]
    public void 計時器_讀不到不猜()
    {
        var f = PerformanceObservationFactsService.TimerResFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void 計時器_聲明不呼叫timeBeginPeriod()
    {
        var f = PerformanceObservationFactsService.TimerResFact(At, () => 1.0);
        Assert.Contains("不呼叫 timeBeginPeriod", f.Value);
    }

    // PO-006 服務崩潰
    [Fact]
    public void 服務崩潰_讀不到不畫成0()
    {
        var f = PerformanceObservationFactsService.SvcCrashFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不畫成 0 次", f.UnavailableReason);
    }

    [Fact]
    public void 服務崩潰_痕跡不是根因且不列名稱()
    {
        var f = PerformanceObservationFactsService.SvcCrashFact(At, () => [("svc1", 3), ("svc2", 1)]);
        Assert.Contains("痕跡不是根因", f.Value);
        Assert.Contains("服務名稱不列入值", f.Value);
        Assert.Equal(4.0, f.NumericValue);
    }

    // PO-012 狀態遷移
    [Fact]
    public void 狀態遷移_明確標推測級()
    {
        var f = PerformanceObservationFactsService.TransitionFact(At, () => (1000000L, 50000L));
        Assert.Contains("推測級", f.Value);
        Assert.Contains("不是", f.Value);
    }

    // 整批鍵齊
    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = PerformanceObservationFactsService.Collect(At);
        string[] keys =
        [
            PerformanceObservationFactsService.PoolKey,
            PerformanceObservationFactsService.CompressionKey,
            PerformanceObservationFactsService.PageFaultKey,
            PerformanceObservationFactsService.ReadyBoostKey,
            PerformanceObservationFactsService.HandlesKey,
            PerformanceObservationFactsService.GdiKey,
            PerformanceObservationFactsService.FreqVarKey,
            PerformanceObservationFactsService.TimerResKey,
            PerformanceObservationFactsService.SvcCrashKey,
            PerformanceObservationFactsService.TransitionKey,
        ];
        foreach (string k in keys)
            Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(10, facts.Count);
        Assert.Equal(10, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
