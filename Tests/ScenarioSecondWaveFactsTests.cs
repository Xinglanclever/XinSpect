using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>情境引導第二波（SG-008／SG-010）單元測試。</summary>
public class ScenarioSecondWaveFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    private static UsbPortRow Row(string verdict, int severity) =>
        new("控制器 1／埠 1", 0, "裝置", "VID_1234", "USB 3.0", "大量儲存",
            "480 Mb/s", "5 Gb/s", "USB 1.1／2.0／3.0", "500 mA", verdict, severity);

    // ── USB 摘要：複用 UsbLinkDecoder 的判讀 ─────────────────────────────

    [Fact]
    public void USB摘要_線材造成的落差歸線材()
    {
        var rows = new[]
        {
            Row("⚠ 裝置支援 USB 3.x、這個埠也支援，卻只跑在 480 Mb/s——多半是線材（或延長線）裡只有 USB 2.0 的那幾條線。", 2),
        };
        var s = ScenarioSecondWaveFactsService.SummarizeUsb(rows);
        Assert.Equal(1, s.DownTo2);
        Assert.Equal(1, s.ByCable);
        Assert.Equal(0, s.ByPort);
    }

    [Fact]
    public void USB摘要_埠造成的落差歸埠()
    {
        var rows = new[]
        {
            Row("⚠ 裝置支援 USB 3.x，但這個埠只有 USB 2.0，所以只能跑 480 Mb/s——換到支援 3.x 的埠才拿得到 5 Gb/s。", 2),
        };
        var s = ScenarioSecondWaveFactsService.SummarizeUsb(rows);
        Assert.Equal(1, s.DownTo2);
        Assert.Equal(0, s.ByCable);
        Assert.Equal(1, s.ByPort);
    }

    [Fact]
    public void USB摘要_差一級不計入嚴重落差()
    {
        var rows = new[] { Row("裝置支援 10 Gb/s（SuperSpeedPlus），目前跑在 5 Gb/s——這個埠或線材只到 5 Gb/s，不是故障。", 1) };
        var s = ScenarioSecondWaveFactsService.SummarizeUsb(rows);
        Assert.Equal(0, s.DownTo2);
        Assert.Equal(1, s.OneStep);
    }

    // ── SG-008 事實值 ────────────────────────────────────────────────────

    [Fact]
    public void USB慢_分清楚線與埠且說出處置()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, usb: () =>
            new ScenarioSecondWaveFactsService.UsbSlowState(4, 2, 1, 1, 1)), "sg.usbslow");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("是<b>線材</b>造成", f.Value);
        Assert.Contains("是<b>插錯埠</b>造成", f.Value);
        // 處置不同是分開講的理由。
        Assert.Contains("一個換線、一個換埠", f.Value);
        Assert.Equal(2d, f.NumericValue);
    }

    [Fact]
    public void USB慢_沒有落差時明確排除USB這一段()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, usb: () =>
            new ScenarioSecondWaveFactsService.UsbSlowState(3, 0, 0, 0, 0)), "sg.usbslow");
        Assert.Contains("USB 這一段不是慢的原因", f.Value);
        // 排除法：不宣稱找到元凶。
        Assert.Contains("不宣稱找到元凶", f.Value);
    }

    [Fact]
    public void USB慢_空清單不得講成一切正常()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, usb: () =>
            new ScenarioSecondWaveFactsService.UsbSlowState(0, 0, 0, 0, 0)), "sg.usbslow");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("這不代表速度正常", f.Value);
        Assert.Contains("集線器不接受查詢", f.Value);
    }

    [Fact]
    public void USB慢_差一級標明不是故障()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, usb: () =>
            new ScenarioSecondWaveFactsService.UsbSlowState(2, 0, 1, 0, 0)), "sg.usbslow");
        Assert.Contains("<b>不是故障</b>", f.Value);
    }

    [Fact]
    public void USB慢_不測吞吐量()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, usb: () =>
            new ScenarioSecondWaveFactsService.UsbSlowState(1, 1, 0, 1, 0)), "sg.usbslow");
        Assert.Contains("不測吞吐量", f.Value);
        Assert.Contains("需明示同意", f.Value);
    }

    [Fact]
    public void USB慢_讀取失敗是ReadError不當成正常()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, usb: () => null), "sg.usbslow");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不把讀不到當成速度正常", f.UnavailableReason);
    }

    // ── SG-010 聲音為什麼斷 ──────────────────────────────────────────────

    [Fact]
    public void 聲音_並列三個面向()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, audio: () =>
            new ScenarioSecondWaveFactsService.AudioState(3, 1, 5, true)), "sg.audio");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("渲染端點 3 個", f.Value);
        Assert.Contains("1 個端點掛了 APO 處理鏈", f.Value);
        Assert.Contains("預設端點組態可讀", f.Value);
        Assert.Equal(3d, f.NumericValue);
    }

    [Fact]
    public void 聲音_不斷言就是APO造成的()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, audio: () =>
            new ScenarioSecondWaveFactsService.AudioState(2, 2, 8, true)), "sg.audio");
        // 這是本項的紅線：只並列面向，不下根因判決。
        Assert.Contains("不斷言元凶", f.Value);
        Assert.Contains("排除法的起點", f.Value);
        Assert.Contains("掛著不等於它在處理", f.Value);
    }

    [Fact]
    public void 聲音_點出成因多在本工具可見範圍外()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, audio: () =>
            new ScenarioSecondWaveFactsService.AudioState(1, 0, 0, true)), "sg.audio");
        Assert.Contains("可見範圍之外", f.Value);
        Assert.Contains("USB 排程與省電", f.Value);
    }

    [Fact]
    public void 聲音_沒有APO時如實說()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, audio: () =>
            new ScenarioSecondWaveFactsService.AudioState(2, 0, 0, true)), "sg.audio");
        Assert.Contains("沒有任何端點掛 APO 處理鏈", f.Value);
    }

    [Fact]
    public void 聲音_沒有端點時說清它與間歇中斷的關係()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, audio: () =>
            new ScenarioSecondWaveFactsService.AudioState(0, 0, 0, false)), "sg.audio");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("沒有輸出端點", f.Value);
        // 症狀與狀態不符時要指出來，不能讓讀者拿錯結論。
        Assert.Contains("與症狀不符", f.Value);
    }

    [Fact]
    public void 聲音_預設端點讀不到時不猜()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, audio: () =>
            new ScenarioSecondWaveFactsService.AudioState(2, 1, 3, false)), "sg.audio");
        Assert.Contains("讀不到</b>（不猜預設裝置", f.Value);
    }

    [Fact]
    public void 聲音_不錄音不播測試音()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, audio: () =>
            new ScenarioSecondWaveFactsService.AudioState(1, 1, 1, true)), "sg.audio");
        Assert.Contains("不錄音、不播放測試音、不讀任何音訊內容", f.Value);
    }

    [Fact]
    public void 聲音_讀取失敗是ReadError()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At, audio: () => null), "sg.audio");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void 聲音_探針擲例外也是ReadError()
    {
        var f = Fact(ScenarioSecondWaveFactsService.Collect(At,
            audio: () => throw new InvalidOperationException("x")), "sg.audio");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 契約 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 兩把鍵齊且互不影響()
    {
        var facts = ScenarioSecondWaveFactsService.Collect(At,
            usb: () => null,
            audio: () => new ScenarioSecondWaveFactsService.AudioState(1, 0, 0, true));
        Assert.Equal(new[] { "sg.audio", "sg.usbslow" },
            facts.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(FactAvailability.ReadError, Fact(facts, "sg.usbslow").Availability);
        Assert.Equal(FactAvailability.Present, Fact(facts, "sg.audio").Availability);
    }

    [Fact]
    public void 兩把鍵都是情境診斷類別()
    {
        var facts = ScenarioSecondWaveFactsService.Collect(At,
            usb: () => new ScenarioSecondWaveFactsService.UsbSlowState(1, 0, 0, 0, 0),
            audio: () => new ScenarioSecondWaveFactsService.AudioState(1, 0, 0, true));
        Assert.All(facts, f => Assert.Equal("情境診斷", f.Category));
    }
}
