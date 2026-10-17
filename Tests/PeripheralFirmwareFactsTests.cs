using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 周邊韌體版本盤點的守門（ULTIMATE-1000 階段二 P2-096）。
///
/// <para>
/// <b>核心是「裝置自述」與「誰沒回報」之別</b>：
/// 讀不到（列舉失敗）、沒有硬碟（確定答案）、有硬碟但裝置不自述韌體版本——三件事不同。
/// 網卡那半邊在 Windows 沒有可讀的韌體欄位，<b>缺源要說出來，且不得用驅動版本冒充</b>。
/// </para>
/// </summary>
public class PeripheralFirmwareFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static PeripheralFirmwareFactsService.PeripheralInventory Inventory(
        IReadOnlyList<PeripheralFirmwareFactsService.DeviceFirmware>? disks = null, int? nics = 4)
        => new(disks ?? [new("硬碟", "TestDisk X1", "SN1234")], nics);

    [Fact]
    public void 列舉失敗時要標讀取錯誤並說那與沒有硬碟不同()
    {
        var f = PeripheralFirmwareFactsService.VersionFact(At, () => null);

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("「讀不到」與「這台機器沒有硬碟」是不同的斷言", f.UnavailableReason);
        Assert.Contains("會讓一次查詢失敗看起來像一個硬體事實", f.UnavailableReason);
    }

    [Fact]
    public void 查詢成功但沒有硬碟_是確定答案不是讀不到()
    {
        var f = PeripheralFirmwareFactsService.VersionFact(At, () => Inventory(disks: []));

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("確定的答案", f.UnavailableReason);
        Assert.Contains("虛擬機", f.UnavailableReason);
    }

    [Fact]
    public void 有硬碟有韌體_逐項陳述並數對數量()
    {
        var f = PeripheralFirmwareFactsService.VersionFact(At, () => Inventory(
        [
            new("硬碟", "Disk A", "1A2B"),
            new("SSD", "Disk B", "3C4D"),
        ]));

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(2, f.NumericValue);
        Assert.Contains("硬碟 Disk A＝1A2B", f.Value);
        Assert.Contains("3C4D", f.Value);
        Assert.Contains("裝置自述", f.Value);
        Assert.Contains("不是對照原廠清單後判定的「最新版」", f.Value);
        // Present 的事實不得帶 unavailableReason（HardwareSnapshotService 的不變式）
        Assert.Null(f.UnavailableReason);
    }

    [Fact]
    public void 有硬碟但裝置不自述韌體_要說出來而不是代填()
    {
        var f = PeripheralFirmwareFactsService.VersionFact(At, () => Inventory(
        [
            new("硬碟", "Disk A", ""),
            new("硬碟", "Disk B", ""),
        ]));

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0, f.NumericValue);
        Assert.Contains("沒有任何一顆自述韌體版本", f.Value);
        Assert.Contains("不代填成別的推測", f.Value);
    }

    [Fact]
    public void 超過五顆只列五顆並說省略幾顆()
    {
        var disks = Enumerable.Range(0, 7)
            .Select(i => new PeripheralFirmwareFactsService.DeviceFirmware("硬碟", $"Disk{i}", $"FW{i}"))
            .ToList();

        var f = PeripheralFirmwareFactsService.VersionFact(At, () => Inventory(disks));

        Assert.Equal(7, f.NumericValue);
        Assert.Contains("Disk4＝FW4", f.Value);
        Assert.DoesNotContain("Disk5＝FW5", f.Value);
        Assert.Contains("省略 2 顆", f.Value);
    }

    [Fact]
    public void 網卡沒有韌體欄位_缺源要說出來且不得用驅動版本冒充()
    {
        var f = PeripheralFirmwareFactsService.VersionFact(At, () => Inventory(nics: 3));

        Assert.Contains("實體 3 張", f.Value);
        Assert.Contains("Win32_NetworkAdapter 沒有韌體欄位", f.Value);
        Assert.Contains("DriverVersion 是驅動程式的版本，不是網卡韌體", f.Value);
        Assert.Contains("缺源", f.Value);
        Assert.Contains("不是「沒有」", f.Value);
    }

    [Fact]
    public void 連網卡列舉都失敗時_那一邊如實標缺源且不影響硬碟那半邊()
    {
        var f = PeripheralFirmwareFactsService.VersionFact(At, () => Inventory(nics: null));

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("連實體網卡的列舉都失敗", f.Value);
        Assert.Contains("SN1234", f.Value);
    }

    [Fact]
    public void 不列序號_型號與韌體可公開()
    {
        // L3 禁項掃描的鄰居：序號是識別碼，不在盤點事實的輸出裡。
        var f = PeripheralFirmwareFactsService.VersionFact(At, () => Inventory(
            [new("硬碟", "Disk A", "1A2B")]));

        Assert.DoesNotContain("序號", f.Value);
        Assert.Contains("Disk A", f.Value);
    }
}
