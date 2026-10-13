using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 400–409 項（深測方法學二與攻擊面）的事實服務測試。</summary>
public class DeepBenchMethodology2FactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 6, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Ping_未指定目標絕不出網()
    {
        var f = DeepBenchMethodology2FactsService.PingFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("未指定＝絕不出網", f.UnavailableReason);
    }

    [Fact]
    public void Ping_使用者指定才出網不預設()
    {
        var f = DeepBenchMethodology2FactsService.PingFact(At, () => ("gw", 12.5, 1.2));
        Assert.Contains("使用者指定才出網", f.Value);
        Assert.Contains("不預設目標", f.Value);
    }

    [Fact]
    public void 漂移_是環境問題非硬體退化()
    {
        var f = DeepBenchMethodology2FactsService.BenchDriftFact(At, () => (8.5, 20));
        Assert.Contains("環境問題", f.Value);
        Assert.Contains("不是硬體退化", f.Value);
    }

    [Fact]
    public void GPU_無卡雙軌契約捕獲例外()
    {
        var f = DeepBenchMethodology2FactsService.GpuFp16Fact(At, () => null);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("GpuUnsupportedException", f.UnavailableReason);
    }

    [Fact]
    public void GPU_不支援是環境事實非錯誤()
    {
        var f = DeepBenchMethodology2FactsService.GpuFp16Fact(At, () => (0, 0, false));
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("不是錯誤", f.UnavailableReason);
    }

    [Fact]
    public void GPU_有卡是冒煙非排名()
    {
        var f = DeepBenchMethodology2FactsService.GpuFp16Fact(At, () => (120, 240, true));
        Assert.Contains("冒煙測試", f.Value);
        Assert.Contains("不是效能排名", f.Value);
    }

    [Fact]
    public void 風扇分區_對應未知聲明()
    {
        var f = DeepBenchMethodology2FactsService.FanZonesFact(At, () => [("Z1", 120), ("Z2", -30)]);
        Assert.Contains("分區對應未知聲明", f.Value);
        Assert.Contains("無法確認對應", f.Value);
    }

    [Fact]
    public void USB吞吐_未同意永不寫入()
    {
        var f = DeepBenchMethodology2FactsService.UsbThroughputFact(At, () => (0, 0, false));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("未同意永不執行", f.UnavailableReason);
    }

    [Fact]
    public void USB吞吐_同意後是相對比較非規格()
    {
        var f = DeepBenchMethodology2FactsService.UsbThroughputFact(At, () => (180, 60, true));
        Assert.Contains("同意閘門", f.Value);
        Assert.Contains("不是規格", f.Value);
    }

    [Fact]
    public void 開機全階段_一次觀察非現況()
    {
        var f = DeepBenchMethodology2FactsService.BootAllPhasesFact(At, () => [("總", 12000), ("韌體", 3000)]);
        Assert.Contains("一次開機的觀察非現況", f.Value);
    }

    [Fact]
    public void 攻擊面匯總_不下安全判決()
    {
        var f = DeepBenchMethodology2FactsService.SurfaceFact(At, () => (2, 5, 3));
        Assert.Contains("不下安全判決", f.Value);
        Assert.Contains("不是「有問題」", f.Value);
    }

    [Fact]
    public void SMB_Everyone寫入是風險面非判決()
    {
        var f = DeepBenchMethodology2FactsService.ShareFact(At, () => (4, 1));
        Assert.Contains("共用名稱不列入值", f.Value);
        Assert.Contains("風險面", f.Value);
    }

    [Fact]
    public void 安全基線_快照日期必印()
    {
        var f = DeepBenchMethodology2FactsService.BaselineFact(At, () => (40, "2026-10-01"));
        Assert.Contains("快照日期必印", f.Value);
        Assert.Contains("不是「符合某個安全標準」", f.Value);
    }

    [Fact]
    public void 安全分級_灰是未施測不是通過()
    {
        var f = DeepBenchMethodology2FactsService.GradeFact(At, () => (1, 3, 6, "B"));
        Assert.Contains("灰（未施測／不適用）不是「通過」", f.Value);
        Assert.Contains("不能推論為整體安全狀態", f.Value);
    }

    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = DeepBenchMethodology2FactsService.Collect(At);
        string[] keys =
        [
            DeepBenchMethodology2FactsService.PingKey, DeepBenchMethodology2FactsService.BenchDriftKey,
            DeepBenchMethodology2FactsService.GpuFp16Key, DeepBenchMethodology2FactsService.FanZonesKey,
            DeepBenchMethodology2FactsService.UsbThroughputKey, DeepBenchMethodology2FactsService.BootAllPhasesKey,
            DeepBenchMethodology2FactsService.SurfaceKey, DeepBenchMethodology2FactsService.ShareKey,
            DeepBenchMethodology2FactsService.BaselineKey, DeepBenchMethodology2FactsService.GradeKey,
        ];
        foreach (string k in keys) Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(10, facts.Count);
        Assert.Equal(10, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
