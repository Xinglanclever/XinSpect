using System;
using System.Collections.Generic;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 346–357 項（匯流排深化）的事實服務測試。</summary>
public class BusDeepDiveFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 3, 0, 0, TimeSpan.Zero);

    // UV-001 TB 安全等級
    [Fact]
    public void TB安全_無控制器不適用()
    {
        var f = BusDeepDiveFactsService.TbSecFact(At, () => []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("未使用不代表有風險", f.UnavailableReason);
    }

    [Fact]
    public void TB安全_讀不到埠級不冒充相符()
    {
        var f = BusDeepDiveFactsService.TbSecFact(At, () => [new("Thunderbolt", false, 0), new("USB4", false, 0)]);
        Assert.Contains("不冒充「已相符」", f.Value);
        Assert.Equal(2.0, f.NumericValue);
    }

    // UV-002 TB 路由
    [Fact]
    public void TB路由_讀不到標權限不足()
    {
        var f = BusDeepDiveFactsService.TbRouteFact(At, () => (0, 0, false));
        Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability);
        Assert.Contains("不猜", f.UnavailableReason);
    }

    [Fact]
    public void TB路由_可讀範圍聲明非完整路由()
    {
        var f = BusDeepDiveFactsService.TbRouteFact(At, () => (40, 4, true));
        Assert.Contains("可讀範圍聲明", f.Value);
        Assert.Contains("不是完整路由", f.Value);
    }

    // UV-003 USB4
    [Fact]
    public void USB4_無主機路由器不適用()
    {
        var f = BusDeepDiveFactsService.Usb4Fact(At, () => (0, 0, false));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void USB4_埠速率未知如實標()
    {
        var f = BusDeepDiveFactsService.Usb4Fact(At, () => (2, 3, false));
        Assert.Contains("未知（需管理員權限）", f.Value);
        Assert.Contains("不列裝置名稱", f.Value);
    }

    // UV-004 CXL
    [Fact]
    public void CXL_無硬體不適用且代碼先行()
    {
        var f = BusDeepDiveFactsService.CxlFact(At, () => false);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("解碼器代碼已先行", f.UnavailableReason);
    }

    [Fact]
    public void CXL_有DVSEC只讀標頭不猜廠商欄位()
    {
        var f = BusDeepDiveFactsService.CxlFact(At, () => true);
        Assert.Contains("不解碼廠商特定欄位", f.Value);
    }

    // UV-006 USB 描述元
    [Fact]
    public void USB描述元_缺樣本不推論()
    {
        var f = BusDeepDiveFactsService.UsbDescFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不從缺樣本推論", f.UnavailableReason);
    }

    [Fact]
    public void USB描述元_只列結構計數不列序號()
    {
        var f = BusDeepDiveFactsService.UsbDescFact(At, () => new(1, 3, 5));
        Assert.Contains("設定 1", f.Value);
        Assert.Contains("不列廠商字串或序號", f.Value);
        Assert.Equal(5.0, f.NumericValue);
    }

    // UV-008 USB 電源角色
    [Fact]
    public void USB電源_無TypeC不適用()
    {
        var f = BusDeepDiveFactsService.UsbPwrFact(At, () => []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void USB電源_角色是協商結果取樣瞬間()
    {
        var f = BusDeepDiveFactsService.UsbPwrFact(At, () => ["Source", "Sink", "DRP", "DRP"]);
        Assert.Contains("供電方 1", f.Value);
        Assert.Contains("雙角色（DRP）2", f.Value);
        Assert.Contains("取樣瞬間", f.Value);
    }

    // UV-010 Hub
    [Fact]
    public void Hub_無Hub不適用()
    {
        var f = BusDeepDiveFactsService.HubFact(At, () => []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void Hub_宣告供電非實際耗電()
    {
        var f = BusDeepDiveFactsService.HubFact(At, () => [(3, 1500), (2, 900)]);
        Assert.Contains("不是實際耗電", f.Value);
        Assert.Equal(2.0, f.NumericValue);
    }

    // UV-011 斷連
    [Fact]
    public void USB斷連_讀不到不畫成0()
    {
        var f = BusDeepDiveFactsService.UsbErrFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不畫成 0 次", f.UnavailableReason);
    }

    [Fact]
    public void USB斷連_次數多不等於裝置壞()
    {
        var f = BusDeepDiveFactsService.UsbErrFact(At, () => (5, 2));
        Assert.Contains("不等於裝置壞", f.Value);
        Assert.Equal(7.0, f.NumericValue);
    }

    // UV-012 外接
    [Fact]
    public void 外接_無硬體不適用()
    {
        var f = BusDeepDiveFactsService.ExtFact(At, () => (0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 外接_只陳述存在性不宣稱電源()
    {
        var f = BusDeepDiveFactsService.ExtFact(At, () => (1, 2));
        Assert.Contains("只陳述存在性", f.Value);
        Assert.Equal(3.0, f.NumericValue);
    }

    // UV-007 速度歷史
    [Fact]
    public void 速度歷史_樣本不足不宣稱()
    {
        var f = BusDeepDiveFactsService.UsbSpeedHistFact(At, () => [480]);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 速度歷史_只陳述範圍不推論原因()
    {
        var f = BusDeepDiveFactsService.UsbSpeedHistFact(At, () => [480, 5000, 480, 10000]);
        Assert.Contains("480–10000 Mbps", f.Value);
        Assert.Contains("不推論原因", f.Value);
    }

    // 整批鍵齊
    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = BusDeepDiveFactsService.Collect(At);
        string[] keys =
        [
            BusDeepDiveFactsService.TbSecKey, BusDeepDiveFactsService.TbRouteKey, BusDeepDiveFactsService.Usb4Key,
            BusDeepDiveFactsService.CxlKey, BusDeepDiveFactsService.UsbDescKey, BusDeepDiveFactsService.UsbPwrKey,
            BusDeepDiveFactsService.HubKey, BusDeepDiveFactsService.UsbErrKey, BusDeepDiveFactsService.ExtKey,
            BusDeepDiveFactsService.UsbSpeedHistKey,
        ];
        foreach (string k in keys) Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(10, facts.Count);
        Assert.Equal(10, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
