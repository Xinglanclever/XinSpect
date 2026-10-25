using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 全裝置韌體版本盤點（存在面）的守門（U10-FW-0104）。
///
/// <para>
/// <b>核心：</b>不自述韌體版本的裝置也必須出現在清單裡（標「不自述」，不是消失）；
/// 「不自述」與「讀不到」與「沒有裝置」是三個不同的斷言。
/// </para>
/// </summary>
public class PeripheralFirmwareInventoryFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static PeripheralFirmwareFactsService.PeripheralInventory Inv(
        (string Kind, string Model, string Firmware)[] disks, int? nics = 1)
        => new(disks.Select(d => new PeripheralFirmwareFactsService.DeviceFirmware(d.Kind, d.Model, d.Firmware)).ToList(), nics);

    [Fact]
    public void 列舉失敗要標讀取錯誤()
    {
        var fact = PeripheralFirmwareInventoryFactsService.Collect(At, () => null).Single();
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("列舉本身失敗", fact.UnavailableReason);
    }

    [Fact]
    public void 沒有硬碟是不適用()
    {
        var fact = PeripheralFirmwareInventoryFactsService.Collect(At, () => Inv([], 2)).Single();
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("沒有列舉到實體硬碟", fact.UnavailableReason);
    }

    [Fact]
    public void 不自述的裝置不從清單消失()
    {
        var facts = PeripheralFirmwareInventoryFactsService.Collect(At,
            () => Inv([("SSD", "SSD-A", "1.0"), ("硬碟", "HDD-B", ""), ("SSD", "SSD-C", "2.3")], 3),
            () => 180);

        var agg = facts.Single(f => f.Key == PeripheralFirmwareInventoryFactsService.InventoryKey);
        Assert.Equal(FactAvailability.Present, agg.Availability);
        Assert.Equal(2, agg.NumericValue);                       // 自述版本者 2 顆
        Assert.Contains("自述韌體版本 2 顆、**不自述 1 顆", agg.Value);
        Assert.Contains("不從清單消失", agg.Value);
        Assert.Contains("PnP 現用裝置 180 個", agg.Value);
        Assert.Contains("沒有韌體欄位", agg.Value);
        Assert.Contains("存在面", agg.Value);                    // 與 version／surface 的分工

        // 逐碟家族：三顆都要有鍵，不自述的那顆也要有
        Assert.Equal(3, facts.Count(f => f.Key.StartsWith("pdev.fw.inv.")));
        var inv0 = facts.Single(f => f.Key == "pdev.fw.inv.0");
        Assert.Contains("SSD SSD-A＝1.0", inv0.Value);
        Assert.Contains("裝置自述", inv0.Value);
        var inv1 = facts.Single(f => f.Key == "pdev.fw.inv.1");
        Assert.Equal(FactAvailability.Present, inv1.Availability);   // 不自述也是 Present（清單在）
        Assert.Contains("**不自述**", inv1.Value);
        Assert.Contains("不代填", inv1.Value);
    }

    [Fact]
    public void PnP計數失敗要如實標而不是報零()
    {
        var facts = PeripheralFirmwareInventoryFactsService.Collect(At,
            () => Inv([("SSD", "SSD-A", "1.0")], null), () => null);
        var agg = facts.Single(f => f.Key == PeripheralFirmwareInventoryFactsService.InventoryKey);
        Assert.Contains("PnP 現用裝置列舉失敗", agg.Value);
        Assert.Contains("實體網卡列舉失敗", agg.Value);
    }

    [Fact]
    public void 空白與空白字元的韌體欄位都算不自述()
    {
        var facts = PeripheralFirmwareInventoryFactsService.Collect(At,
            () => Inv([("硬碟", "HDD-X", "   ")], 0), () => 7);
        var inv0 = facts.Single(f => f.Key == "pdev.fw.inv.0");
        Assert.Contains("不自述", inv0.Value);
        Assert.Equal(0, facts.Single(f => f.Key == PeripheralFirmwareInventoryFactsService.InventoryKey).NumericValue);
    }
}
