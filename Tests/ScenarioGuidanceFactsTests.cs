using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 情境引導批次（Vol2 推進第 94–98 項：SG-006／007／009／013／014）的守門：全部注入假件。
/// 紅線：名稱比對不是連線測試；0 是「沒比對到」不是「確定沒有」；導航不下判決。
/// </summary>
public class ScenarioGuidanceFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 13, 0, 0, TimeSpan.Zero);

    private static HardwareFact F(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static IReadOnlyList<HardwareFact> Collect(
        ScenarioGuidanceFactsService.PnpProbe? pnp = null,
        ScenarioGuidanceFactsService.InputProbe? input = null,
        ScenarioGuidanceFactsService.DisplayProbe? display = null)
        => ScenarioGuidanceFactsService.Collect(At, pnp, input, display);

    private static IReadOnlyList<ScenarioGuidanceFactsService.PnpRow> Rows(params (string Name, string Class)[] rows) =>
        rows.Select(r => new ScenarioGuidanceFactsService.PnpRow(r.Name, r.Class)).ToList();

    // ── SG-006 藍牙 ─────────────────────────────────────────────────────

    [Fact]
    public void 藍牙_有裝置_類別與名稱比對都算()
    {
        var f = F(Collect(pnp: () => Rows(("Intel Bluetooth", "Bluetooth"), ("Bluetooth USB", "USB"))),
            ScenarioGuidanceFactsService.BtKey);

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("不是連線測試", f.Value);
    }

    [Fact]
    public void 藍牙_沒比對到_0是沒比對到不是確定沒有()
    {
        var f = F(Collect(pnp: () => Rows(("顯卡", "Display"))), ScenarioGuidanceFactsService.BtKey);

        Assert.Equal(0, f.NumericValue);
        Assert.Contains("0 是「沒比對到」", f.Value);
        Assert.DoesNotContain("壞了", f.Value);
    }

    [Fact]
    public void 藍牙_列舉失敗_ReadError()
    {
        var f = F(Collect(pnp: () => throw new IOException("x")), ScenarioGuidanceFactsService.BtKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不是「沒有這個裝置」", f.UnavailableReason);
    }

    // ── SG-007 Wi-Fi ────────────────────────────────────────────────────

    [Fact]
    public void WiFi_幾種常見名稱都能比對()
    {
        foreach (var name in new[] { "Intel Wi-Fi 6 AX201", "802.11ac NIC", "Wireless AC", "WLAN Adapter" })
        {
            var f = F(Collect(pnp: () => Rows((name, "Net"))), ScenarioGuidanceFactsService.WifiKey);
            Assert.True(f.NumericValue > 0, $"{name} 應比對到");
        }
    }

    // ── SG-009 印表機 ───────────────────────────────────────────────────

    [Fact]
    public void 印表機_類別或名稱比對()
    {
        var f = F(Collect(pnp: () => Rows(("HP Printer", "Printer"))), ScenarioGuidanceFactsService.PrintKey);
        Assert.Equal(1, f.NumericValue);
    }

    // ── SG-013 游標卡頓 ─────────────────────────────────────────────────

    [Fact]
    public void 游標_導航不下結論()
    {
        var f = F(Collect(input: () => (true, true)), ScenarioGuidanceFactsService.InputKey);

        Assert.Contains("只導航", f.Value);
        Assert.DoesNotContain("故障", f.Value);
        Assert.DoesNotContain("嚴重", f.Value);
    }

    [Fact]
    public void 游標_讀取失敗_ReadError()
    {
        var f = F(Collect(input: () => throw new IOException("x")), ScenarioGuidanceFactsService.InputKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── SG-014 白屏 ─────────────────────────────────────────────────────

    [Fact]
    public void 白屏_導航指向真正有細節的頁面()
    {
        var f = F(Collect(display: () => (true, true)), ScenarioGuidanceFactsService.DisplayKey);

        Assert.Contains("TDR", f.Value);
        Assert.Contains("只導航", f.Value);
    }

    [Fact]
    public void 白屏_讀取失敗_ReadError()
    {
        var f = F(Collect(display: () => throw new IOException("x")), ScenarioGuidanceFactsService.DisplayKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 整組契約 ────────────────────────────────────────────────────────

    [Fact]
    public void 整組_五把鍵齊_非Present都帶原因()
    {
        var facts = Collect(pnp: () => throw new IOException("x"), input: () => throw new IOException("x"),
            display: () => throw new IOException("x"));

        string[] expected =
        [
            ScenarioGuidanceFactsService.BtKey, ScenarioGuidanceFactsService.WifiKey,
            ScenarioGuidanceFactsService.PrintKey, ScenarioGuidanceFactsService.InputKey,
            ScenarioGuidanceFactsService.DisplayKey,
        ];
        foreach (string key in expected)
        {
            var f = F(facts, key);
            Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason), $"{key} 缺三態原因");
        }
    }
}
