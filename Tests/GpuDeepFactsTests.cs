using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 440–453 項（GPU 深化）的事實服務測試。</summary>
public class GpuDeepFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void NVML全狀態_無卡不適用非錯誤()
    {
        var f = GpuDeepFactsService.NvStateFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不適用，非錯誤", f.UnavailableReason);
    }

    [Fact]
    public void NVML全狀態_只讀不設時脈()
    {
        var f = GpuDeepFactsService.NvStateFact(At, () => (12, 3));
        Assert.Contains("只讀不設", f.Value);
        Assert.Equal(12.0, f.NumericValue);
    }

    [Fact]
    public void NVML風扇_不可讀如實標()
    {
        var f = GpuDeepFactsService.NvFanFact(At, () => (40, 1800));
        Assert.Contains("不可讀如實標", f.Value);
        Assert.Contains("不改風扇", f.Value);
    }

    [Fact]
    public void NVML功率_只讀不調整()
    {
        var f = GpuDeepFactsService.NvPlFact(At, () => (250, 180));
        Assert.Contains("只讀現值不調整", f.Value);
        Assert.Equal(180.0, f.NumericValue);
    }

    [Fact]
    public void NVML_ECC不支援是能力事實非缺陷()
    {
        var f = GpuDeepFactsService.NvEccFact(At, () => (false, 0));
        Assert.Contains("能力事實", f.Value);
        Assert.Contains("不是缺陷", f.Value);
    }

    [Fact]
    public void ADLX_無卡不適用()
    {
        var f = GpuDeepFactsService.AmdFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void Intel_無iGPU不適用()
    {
        var f = GpuDeepFactsService.IntelFact(At, () => (0, false));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void VBIOS_途徑不足如實標()
    {
        var f = GpuDeepFactsService.VbiosFact(At, () => null);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("途徑不足", f.UnavailableReason);
    }

    [Fact]
    public void VBIOS_版本不代表安全()
    {
        var f = GpuDeepFactsService.VbiosFact(At, () => "94.02.42.00.5B");
        Assert.Contains("版本本身不代表更新或安全", f.Value);
    }

    [Fact]
    public void VRAM歷史_單點不宣稱()
    {
        var f = GpuDeepFactsService.VramHistFact(At, () => [512]);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void VRAM歷史_入倉歷史非單點()
    {
        var f = GpuDeepFactsService.VramHistFact(At, () => [512, 800, 1200, 900]);
        Assert.Contains("入倉的歷史", f.Value);
        Assert.Equal(1200.0, f.NumericValue);
    }

    [Fact]
    public void 驅動鏈_只列版本不列路徑()
    {
        var f = GpuDeepFactsService.DrvChainFact(At, () => ("31.0.15", "3.0", "12.1"));
        Assert.Contains("只列版本不列路徑", f.Value);
    }

    [Fact]
    public void 多GPU_不推論目前模式()
    {
        var f = GpuDeepFactsService.MultiFact(At, () => (4, false));
        Assert.Contains("只陳述轉接器數量", f.Value);
        Assert.Contains("不推論目前是哪種模式", f.Value);
    }

    [Fact]
    public void 虛擬顯示器_判定非惡意()
    {
        var f = GpuDeepFactsService.VdispFact(At, () => (4, 3));
        Assert.Contains("判定≠惡意", f.Value);
        Assert.Contains("環境事實", f.Value);
        Assert.Equal(3.0, f.NumericValue);
    }

    [Fact]
    public void 色深_缺途徑不猜()
    {
        var f = GpuDeepFactsService.DepthFact(At, () => null);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
    }

    [Fact]
    public void 色深_列出位元()
    {
        var f = GpuDeepFactsService.DepthFact(At, () => 8);
        Assert.Contains("色深 8 bit", f.Value);
    }

    [Fact]
    public void RTAI_無卡不猜能力()
    {
        var f = GpuDeepFactsService.RtFact(At, () => null);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("不猜能力", f.UnavailableReason);
    }

    [Fact]
    public void 驅動史_缺記錄不推論()
    {
        var f = GpuDeepFactsService.GpuDrvHistFact(At, () => []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不從缺記錄推論", f.UnavailableReason);
    }

    [Fact]
    public void 本批十四把鍵齊()
    {
        var facts = GpuDeepFactsService.Collect(At);
        string[] keys =
        [
            GpuDeepFactsService.NvStateKey, GpuDeepFactsService.NvFanKey, GpuDeepFactsService.NvPlKey,
            GpuDeepFactsService.NvEccKey, GpuDeepFactsService.AmdKey, GpuDeepFactsService.IntelKey,
            GpuDeepFactsService.VbiosKey, GpuDeepFactsService.VramHistKey, GpuDeepFactsService.DrvChainKey,
            GpuDeepFactsService.MultiKey, GpuDeepFactsService.VdispKey, GpuDeepFactsService.DepthKey,
            GpuDeepFactsService.RtKey, GpuDeepFactsService.GpuDrvHistKey,
        ];
        foreach (string k in keys) Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(14, facts.Count);
        Assert.Equal(14, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
