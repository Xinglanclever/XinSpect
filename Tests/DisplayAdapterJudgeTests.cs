using System;
using System.Collections.Generic;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 顯示轉接器真偽判讀（純函式）。核心要釘住的是：Windows 把三種東西一起列在「顯示卡」底下——
/// 真實 GPU、模擬器／遠端桌面裝的軟體轉接器、以及沒有廠商驅動時頂上的基本顯示驅動，
/// 「你有 4 張顯示卡」這句話會同時涵蓋這三種完全不同的處境。
/// </summary>
public class DisplayAdapterJudgeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    private static DisplayAdapterSample Pci(string name, string compat = "NVIDIA", string proc = "NVIDIA TITAN Xp",
        string id = @"PCI\VEN_10DE&DEV_1B02&SUBSYS_11DF10DE&REV_A1\4&1F221E6C&0&0000")
        => new(name, id, compat, proc, true, false, compat.Length > 0, proc.Length > 0,
               compat.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));

    private static DisplayAdapterSample Root(string name, string compat,
        string id = @"ROOT\DISPLAY\0000", string proc = "")
        => new(name, id, compat, proc, false, true, compat.Length > 0, proc.Length > 0,
               compat.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));

    // ── 三種轉接器要分得開 ────────────────────────────────────────────────

    [Fact]
    public void 有PCI位址_判為真實顯示卡且計入張數()
    {
        var v = DisplayAdapterJudge.Judge(Pci("NVIDIA TITAN Xp"));

        Assert.Equal(DisplayAdapterJudge.AdapterKind.Physical, v.Kind);
        Assert.True(v.CountsAsGpu);
        Assert.Contains("真實", v.Headline);
    }

    [Fact]
    public void 由ROOT列舉_判為軟體轉接器且不計入張數()
    {
        // 本機實況：MuMu／GameViewer／Virtual Display Driver 三個都是 ROOT\DISPLAY\...
        var v = DisplayAdapterJudge.Judge(Root("MuMu Virtual Display Adapter", "MuMu"));

        Assert.Equal(DisplayAdapterJudge.AdapterKind.Virtual, v.Kind);
        Assert.False(v.CountsAsGpu);
        Assert.Contains("不是硬體", v.Headline);
        Assert.Contains("ROOT", v.Evidence);
        Assert.Contains("不應計入", v.Evidence);
    }

    [Fact]
    public void 真實硬體但只有基本顯示驅動_判為BasicDisplay並說明後果()
    {
        var v = DisplayAdapterJudge.Judge(new DisplayAdapterSample(
            "Microsoft Basic Display Adapter", @"PCI\VEN_10DE&DEV_1B02&REV_A1", "Microsoft",
            "", true, false, true, false, true));

        Assert.Equal(DisplayAdapterJudge.AdapterKind.BasicDisplay, v.Kind);
        Assert.True(v.CountsAsGpu);
        Assert.Contains("基本顯示驅動", v.Headline);
        Assert.Contains("不是硬體的問題", v.Evidence);   // 必須講清楚責任歸屬
    }

    [Fact]
    public void 既非PCI也非ROOT_判為未知()
    {
        var v = DisplayAdapterJudge.Judge(new DisplayAdapterSample(
            "某裝置", @"USB\VID_1234&PID_5678", "X", "Y", false, false, true, true, false));

        Assert.Equal(DisplayAdapterJudge.AdapterKind.Unknown, v.Kind);
        Assert.False(v.CountsAsGpu);
        Assert.Contains("如實不判", v.Evidence);
    }

    [Fact]
    public void 真實卡但驅動沒回報處理器與廠商_仍為真實但註明驅動資訊不完整()
    {
        var v = DisplayAdapterJudge.Judge(Pci("未知顯示卡", compat: "", proc: ""));

        Assert.Equal(DisplayAdapterJudge.AdapterKind.Physical, v.Kind);
        Assert.Contains("驅動", v.Headline);
        Assert.Contains("保留", v.Evidence);
    }

    // ── 摘要 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 摘要_虛擬與實體分開算並提醒判讀影響()
    {
        var summary = DisplayAdapterJudge.Summarize(
        [
            Pci("NVIDIA TITAN Xp"),
            Root("MuMu Virtual Display Adapter", "MuMu"),
            Root("GameViewer Virtual Display Adapter", "GameViewer"),
            Root("Virtual Display Driver", "MikeTheTech"),
        ]);

        Assert.Contains("1 張真實顯示卡", summary);
        Assert.Contains("3 個軟體顯示轉接器", summary);
        Assert.Contains("不是硬體", summary);
        Assert.Contains("確認它在哪一個轉接器上量到的", summary);
    }

    [Fact]
    public void 摘要_沒有任何轉接器要說讀不到而不是一切正常()
    {
        var summary = DisplayAdapterJudge.Summarize([]);
        Assert.Contains("沒有讀到", summary);
        Assert.Contains("不代表機器沒有顯示裝置", summary);
    }

    [Fact]
    public void 摘要_混合實體與基本顯示驅動都要列()
    {
        var summary = DisplayAdapterJudge.Summarize(
        [
            Pci("NVIDIA TITAN Xp"),
            new DisplayAdapterSample("Microsoft Basic Display Adapter",
                @"PCI\VEN_10DE&DEV_1B02&REV_A1", "Microsoft", "", true, false, true, false, true),
        ]);

        Assert.Contains("1 張真實顯示卡", summary);
        Assert.Contains("1 張只有基本顯示驅動", summary);
    }

    // ── 事實收集 ──────────────────────────────────────────────────────────

    [Fact]
    public void 事實收集_摘要為Derived各轉接器為Measured()
    {
        var facts = DisplayAdapterFactsService.Collect(At,
            () => [Pci("NVIDIA TITAN Xp"), Root("MuMu Virtual Display Adapter", "MuMu")]);

        var summary = Assert.Single(facts, f => f.Key == "display.summary");
        Assert.Equal(FactTrustLevel.Derived, summary.Trust);
        Assert.Contains("1 張真實顯示卡", summary.Value);

        var titan = Assert.Single(facts, f => f.Key.Contains("NVIDIA TITAN Xp"));
        Assert.Equal(FactTrustLevel.Measured, titan.Trust);
        Assert.Equal(FactAvailability.Present, titan.Availability);

        var mumu = Assert.Single(facts, f => f.Key.Contains("MuMu"));
        Assert.Contains("不是硬體", mumu.Value);
    }

    [Fact]
    public void 事實收集_沒有轉接器為ReadError而非空清單()
    {
        var facts = DisplayAdapterFactsService.Collect(At, () => []);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 硬體 GPU 在場狀態（v2.57）────────────────────────────────────────

    [Fact]
    public void 硬體gpu在場_有真卡時present且帶名稱與計數()
    {
        var facts = DisplayAdapterFactsService.Collect(At,
            () => [Pci("NVIDIA TITAN Xp"), Root("MuMu Virtual Display Adapter", "MuMu")]);
        var f = Assert.Single(facts, x => x.Key == "display.hw.gpu");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal("NVIDIA TITAN Xp", f.Value);   // 虛擬轉接器不進這條
        Assert.Equal(1, f.NumericValue);
    }

    [Fact]
    public void 硬體gpu缺席_只有虛擬顯示時如實說沒有且計數為零()
    {
        // 「沒有硬體 GPU」是觀察到的 Present 事實——缺席的是卡，不是讀取；
        // 不得以三態冒充（那會把「這台沒卡」跟「沒去讀」混成一件事）。
        var facts = DisplayAdapterFactsService.Collect(At,
            () => [Root("MuMu Virtual Display Adapter", "MuMu")]);
        var f = Assert.Single(facts, x => x.Key == "display.hw.gpu");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("沒有可辨識的硬體顯示卡", f.Value, StringComparison.Ordinal);
        Assert.Equal(0, f.NumericValue);
    }

    [Fact]
    public void 硬體gpu缺席_基本顯示轉接器不算硬體gpu()
    {
        var facts = DisplayAdapterFactsService.Collect(At,
            () => [Pci("Microsoft Basic Display Adapter", compat: "Microsoft Corporation")]);
        var f = Assert.Single(facts, x => x.Key == "display.hw.gpu");
        Assert.Equal(0, f.NumericValue);
    }

    [Fact]
    public void 事實收集_擲回例外以ReadError回報()
    {
        var facts = DisplayAdapterFactsService.Collect(At, () => throw new InvalidOperationException("模擬失敗"));
        Assert.Equal(FactAvailability.ReadError, Assert.Single(facts).Availability);
    }
}
