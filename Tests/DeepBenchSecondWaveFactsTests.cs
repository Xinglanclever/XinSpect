using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 第 333–342 項（深測第二波）的事實服務測試。
/// 重點：同意閘門未注入＝永不執行、擬合是模型聲明、推測級要標明、單機／無電池 N/A。
/// </summary>
public class DeepBenchSecondWaveFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 2, 0, 0, TimeSpan.Zero);

    private static List<double> Series(int n, double start, double step)
        => Enumerable.Range(0, n).Select(i => start + i * step).ToList();

    // TU-002 溫度響應
    [Fact]
    public void 溫度響應_樣本不足不宣稱()
    {
        var f = DeepBenchSecondWaveFactsService.ThermRespFact(At, () => (Series(3, 1, 1), 40));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不宣稱", f.UnavailableReason);
    }

    [Fact]
    public void 溫度響應_是模型聲明不是事實()
    {
        var f = DeepBenchSecondWaveFactsService.ThermRespFact(At, () => (Series(12, 10, 5), 45.5));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("模型聲明", f.Value);
    }

    // TU-003 功率-頻率
    [Fact]
    public void 功率頻率_樣本不足不擬合()
    {
        var f = DeepBenchSecondWaveFactsService.PFreqFact(At, () => (Series(3, 3000, 10), Series(3, 50, 5)));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 功率頻率_標明模型非硬體事實()
    {
        var f = DeepBenchSecondWaveFactsService.PFreqFact(At, () => (Series(12, 3000, 10), Series(12, 50, 5)));
        Assert.Contains("模型聲明", f.Value);
        Assert.Contains("不是硬體事實", f.Value);
    }

    // TU-004 Vdroop
    [Fact]
    public void Vdroop_樣本不足不宣稱()
    {
        var f = DeepBenchSecondWaveFactsService.VdroopFact(At, () => (Series(3, 1.2, 0.01), Series(3, 10, 5)));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void Vdroop_明確標推測級()
    {
        var f = DeepBenchSecondWaveFactsService.VdroopFact(At, () => (Series(12, 1.2, 0.001), Series(12, 10, 5)));
        Assert.Contains("推測級", f.Value);
    }

    // TU-005 記憶體保持（同意閘門）
    [Fact]
    public void 記憶體保持_未同意永不執行()
    {
        var f = DeepBenchSecondWaveFactsService.MemHoldFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("未同意永不執行", f.UnavailableReason);
    }

    [Fact]
    public void 記憶體保持_同意後回結果()
    {
        var f = DeepBenchSecondWaveFactsService.MemHoldFact(At, () => (1024, 2048));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("使用者明確觸發", f.Value);
        Assert.Equal(1024.0, f.NumericValue);
    }

    // TU-006 電池
    [Fact]
    public void 電池_無電池不適用()
    {
        var f = DeepBenchSecondWaveFactsService.BatteryFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒有電池", f.UnavailableReason);
    }

    [Fact]
    public void 電池_列放電區間()
    {
        var f = DeepBenchSecondWaveFactsService.BatteryFact(At, () => (90, 100, 80));
        Assert.Contains("放電 90 分鐘", f.Value);
        Assert.Contains("不是電池設計容量", f.Value);
    }

    // TU-007 UPS
    [Fact]
    public void UPS_未同意永不執行()
    {
        var f = DeepBenchSecondWaveFactsService.UpsFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("未同意永不執行", f.UnavailableReason);
    }

    // TU-009 封包損失
    [Fact]
    public void 封包損失_讀不到不猜()
    {
        var f = DeepBenchSecondWaveFactsService.PktLossFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void 封包損失_沒樣本不等於沒損失()
    {
        var f = DeepBenchSecondWaveFactsService.PktLossFact(At, () => []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒量到", f.UnavailableReason);
    }

    [Fact]
    public void 封包損失_不列介面名稱或IP()
    {
        var f = DeepBenchSecondWaveFactsService.PktLossFact(At, () => [0.1, 2.5, 0.0]);
        Assert.Contains("不列介面名稱或 IP", f.Value);
        Assert.Equal(2.5, f.NumericValue);
    }

    // TU-011 原始資料包
    [Fact]
    public void 原始包_無樣本不建構()
    {
        var f = DeepBenchSecondWaveFactsService.RawPackFact(At, () => (0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不建構空資料包", f.UnavailableReason);
    }

    [Fact]
    public void 原始包_只出遮蔽版不落檔()
    {
        var f = DeepBenchSecondWaveFactsService.RawPackFact(At, () => (100, 7));
        Assert.Contains("遮蔽", f.Value);
        Assert.Contains("只建構不落檔", f.Value);
    }

    // TU-012 多機矩陣
    [Fact]
    public void 多機矩陣_單機不假裝()
    {
        var f = DeepBenchSecondWaveFactsService.MatrixFact(At, () => (1, 10));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("單機不假裝", f.UnavailableReason);
    }

    [Fact]
    public void 多機矩陣_並排不是排名()
    {
        var f = DeepBenchSecondWaveFactsService.MatrixFact(At, () => (3, 20));
        Assert.Contains("呈現", f.Value);
        Assert.Contains("不是排名", f.Value);
        Assert.Equal(3.0, f.NumericValue);
    }

    // TU-010 夜間自跑
    [Fact]
    public void 夜間自跑_明文不做()
    {
        var f = DeepBenchSecondWaveFactsService.NightlyFact(At, () => null);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("明文不做", f.UnavailableReason);
    }

    // 整批鍵齊
    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = DeepBenchSecondWaveFactsService.Collect(At);
        string[] keys =
        [
            DeepBenchSecondWaveFactsService.ThermRespKey,
            DeepBenchSecondWaveFactsService.PFreqKey,
            DeepBenchSecondWaveFactsService.VdroopKey,
            DeepBenchSecondWaveFactsService.MemHoldKey,
            DeepBenchSecondWaveFactsService.BatteryKey,
            DeepBenchSecondWaveFactsService.UpsKey,
            DeepBenchSecondWaveFactsService.PktLossKey,
            DeepBenchSecondWaveFactsService.RawPackKey,
            DeepBenchSecondWaveFactsService.MatrixKey,
            DeepBenchSecondWaveFactsService.NightlyKey,
        ];
        foreach (string k in keys)
            Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(10, facts.Count);
        Assert.Equal(10, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
