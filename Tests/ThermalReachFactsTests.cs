using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 熱管理可讀項盤點的守門（ULTIMATE-1000 階段二 P2-094）。
///
/// <para>
/// <b>這一組的核心是「哪一種沒讀到」的四種分法：</b>
/// ①這條路失敗（權限／查詢錯誤）；②這條路查成功但機器沒有這一項；
/// ③查到了但值回 0（驅動未填，<b>0 K 不是溫度</b>）；④<b>本專案根本沒有那條路</b>（EC）。
/// 四者在畫面上如果都顯示成「—」，那一項事實就沒有價值了。
/// </para>
/// </summary>
public class ThermalReachFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static ThermalReachFactsService.ThermalReachState State(
        int probes = 0, int probesBad = 0, int cooling = 0, int coolingBad = 0,
        int zones = 0, int unreported = 0, double? warmest = null,
        string? firmwareFailure = null, string? wmiFailure = null)
        => new(probes, probesBad, cooling, coolingBad, zones, unreported, warmest,
            firmwareFailure, wmiFailure);

    [Fact]
    public void 探針回null_標讀取錯誤並說與沒有感測器不同()
    {
        var f = ThermalReachFactsService.ReachFact(At, () => null);

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("兩條路都不通", f.UnavailableReason);
        Assert.Contains("「讀不到」與「這台機器沒有溫度感測器」是不同的斷言", f.UnavailableReason);
    }

    [Fact]
    public void 兩條路都失敗_才算整條讀取錯誤()
    {
        var f = ThermalReachFactsService.ReachFact(At,
            () => State(firmwareFailure: "SMBIOS 讀不到", wmiFailure: "權限不足"));

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("SMBIOS 讀不到", f.UnavailableReason);
        Assert.Contains("權限不足", f.UnavailableReason);
    }

    [Fact]
    public void 只有一條路失敗_另一條照常陳述且失敗那條標缺源()
    {
        var f = ThermalReachFactsService.ReachFact(At,
            () => State(probes: 6, cooling: 3, zones: 0, wmiFailure: "權限不足（ACPI 熱區通常需要提權）"));

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("查詢失敗（權限不足", f.Value);
        Assert.Contains("缺源如實列", f.Value);
        Assert.Contains("SMBIOS Type 28 溫度探針 6 個", f.Value);
        Assert.Contains("一條失敗不代表另一條也失敗", f.Value);
    }

    [Fact]
    public void 平台不支援與查詢失敗是不同的斷言()
    {
        // 實測：本機 WMI 熱區回「不受支援」——那是平台不提供，不是查詢出錯、
        // 也不是「這台機器沒有溫度感測器」（韌體那兩個探針就在那裡）。
        var f = ThermalReachFactsService.ReachFact(At,
            () => State(probes: 2, cooling: 2,
                wmiFailure: "平台不支援（WMI 回「不受支援」——這台機器沒有可用的 ACPI 熱區物件，與權限不足不同）"));

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("平台不支援", f.Value);
        Assert.Contains("與權限不足不同", f.Value);
        Assert.Contains("溫度探針 2 個", f.Value);
    }

    [Fact]
    public void 三條口徑都要分開講_EC那一條是沒有路不是沒有資料()
    {
        var f = ThermalReachFactsService.ReachFact(At, () => State(probes: 2, cooling: 1, zones: 3, warmest: 45.0));

        Assert.Contains("①**韌體自述**", f.Value);
        Assert.Contains("②**OS 端 ACPI 熱區**", f.Value);
        Assert.Contains("③**EC（嵌入式控制器）**", f.Value);
        Assert.Contains("本專案沒有直接讀 EC 的通路", f.Value);
        Assert.Contains("不是「EC 沒有資料」，而是「我們沒有那條路」", f.Value);
    }

    [Fact]
    public void 熱區回零_不當成零度且不計入最高溫()
    {
        var f = ThermalReachFactsService.ReachFact(At, () => State(zones: 3, unreported: 1, warmest: 44.9));

        Assert.Contains("其中 1 個回 0＝未回報", f.Value);
        Assert.Contains("不當成 0 °C", f.Value);
        Assert.Contains("44.9", f.Value);
        Assert.DoesNotContain("0.0 °C", f.Value);
    }

    [Fact]
    public void 熱區一個都沒回報可用值_要說清楚而不是留白()
    {
        var f = ThermalReachFactsService.ReachFact(At, () => State(zones: 2, unreported: 2, warmest: null));

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("沒有任何一個回報了可用的值", f.Value);
    }

    [Fact]
    public void 三邊都零筆_是查證後的沒有不是讀不到()
    {
        var f = ThermalReachFactsService.ReachFact(At, () => State());

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0, f.NumericValue);
        Assert.Contains("查證後的「沒有」，不是讀不到", f.Value);
        Assert.Null(f.UnavailableReason);
    }

    [Fact]
    public void 解不開的探針要能數出來_兩句話不同()
    {
        var f = ThermalReachFactsService.ReachFact(At, () => State(probes: 8, probesBad: 2, cooling: 4, coolingBad: 1));

        Assert.Contains("溫度探針 8 個", f.Value);
        Assert.Contains("其中 2 個結構長度不足、解不開", f.Value);
        Assert.Contains("冷卻裝置 4 個", f.Value);
        Assert.Contains("其中 1 個解不開", f.Value);
        // 可讀項合計＝(8−2)＋(4−1)＋0
        Assert.Equal(9, f.NumericValue);
    }

    [Fact]
    public void 單位要寫清楚_零點一K換算且零不是溫度()
    {
        var f = ThermalReachFactsService.ReachFact(At, () => State(zones: 1, warmest: 45.0));

        Assert.Contains("0.1 K", f.Value);
        Assert.Contains("減 273.15", f.Value);
        Assert.Contains("0 K 不是溫度", f.Value);
    }

    [Fact]
    public void 這一項盤點的是路不是溫度()
    {
        var f = ThermalReachFactsService.ReachFact(At, () => State(probes: 1));

        Assert.Contains("盤點的是「哪些讀得到」，不是溫度本身", f.Value);
        Assert.Contains("並列陳述不互相取代", f.Value);
    }
}
