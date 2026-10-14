using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol1 第 ST-007/009/013/014/016/018 項（儲存深化二）的事實服務測試。</summary>
public class StorageDeepFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static StorageDeepFactsService.ControllerRow Ctrl(string name, string mode, int disks)
        => new(name, mode, disks);

    private static StorageDeepFactsService.BridgeRow Br(string vidpid, string? chip, bool uasp)
        => new(vidpid, chip, uasp);

    // ── ST-007 控制器拓撲樹 ───────────────────────────────────────────

    [Fact]
    public void 控制器_讀不到不猜()
    {
        var f = StorageDeepFactsService.ControllerTreeFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("猜錯比留白糟", f.UnavailableReason);
    }

    [Fact]
    public void 控制器_空清單為不適用非錯誤()
    {
        var f = StorageDeepFactsService.ControllerTreeFact(At, () => []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不適用，不是錯誤", f.UnavailableReason);
    }

    [Fact]
    public void 控制器_標明模式誤判聲明()
    {
        var f = StorageDeepFactsService.ControllerTreeFact(At,
            () => [Ctrl("Intel RST VMD", "RAID", 2), Ctrl("Standard AHCI", "AHCI", 1)]);
        Assert.Contains("模式誤判聲明", f.Value);
        Assert.Contains("不是硬體手冊上的預期模式", f.Value);
        Assert.Equal(2.0, f.NumericValue);
    }

    // ── ST-009 外接盒橋接 ─────────────────────────────────────────────

    [Fact]
    public void 橋接_未收錄如實說不猜型號()
    {
        var f = StorageDeepFactsService.BridgeFact(At,
            () => [Br("152D:0562", null, false), Br("0BDA:9210", "RTL9210", true)]);
        Assert.Contains("知識表未收錄", f.Value);
        Assert.Contains("不用相近型號猜", f.Value);
        Assert.Contains("UASP 1 個", f.Value);
    }

    [Fact]
    public void 橋接_無外接裝置為不適用()
    {
        var f = StorageDeepFactsService.BridgeFact(At, () => []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    // ── ST-013 Storage Spaces ────────────────────────────────────────

    [Fact]
    public void 儲存池_無池為不適用非錯誤()
    {
        var f = StorageDeepFactsService.SpacesFact(At,
            () => new StorageDeepFactsService.SpacesState(0, 0, 0, "—"));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("直連碟不需要儲存池", f.UnavailableReason);
    }

    [Fact]
    public void 儲存池_標明虛擬碟容量不可直接相比()
    {
        var f = StorageDeepFactsService.SpacesFact(At,
            () => new StorageDeepFactsService.SpacesState(1, 4, 2, "Mirror"));
        Assert.Contains("不可直接相比", f.Value);
        Assert.Contains("Mirror", f.Value);
        Assert.Equal(1.0, f.NumericValue);
    }

    // ── ST-014 韌體問題表 ────────────────────────────────────────────

    [Fact]
    public void 韌體表_無表為不適用不假裝比對過()
    {
        var f = StorageDeepFactsService.FirmwareKnownFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不假裝比對過", f.UnavailableReason);
    }

    [Fact]
    public void 韌體表_快照日期必印()
    {
        var f = StorageDeepFactsService.FirmwareKnownFact(At,
            () => new StorageDeepFactsService.FirmwareTableState("2026-10-01", 42, 0, null));
        Assert.Contains("表快照 2026-10-01", f.Value);
        Assert.Contains("快照日期必印", f.Value);
        Assert.Contains("未命中", f.Value);
    }

    [Fact]
    public void 韌體表_韌體版本讀不到不等於未命中()
    {
        var f = StorageDeepFactsService.FirmwareKnownFact(At,
            () => new StorageDeepFactsService.FirmwareTableState("2026-10-01", 42, null, null));
        Assert.Contains("讀不到不等於未命中", f.Value);
    }

    // ── ST-016 WMI 交叉核對 ──────────────────────────────────────────

    [Fact]
    public void 交叉核對_一致時零差異是值()
    {
        var f = StorageDeepFactsService.CrossCheckFact(At,
            () => new StorageDeepFactsService.CrossCheckState(0, 0, 5, ""));
        Assert.Contains("差異 0 是值", f.Value);
        Assert.Equal(5.0, f.NumericValue);
    }

    [Fact]
    public void 交叉核對_不一致是矛盾不是錯誤()
    {
        var f = StorageDeepFactsService.CrossCheckFact(At,
            () => new StorageDeepFactsService.CrossCheckState(1, 2, 3, "WMI 獨有：MSFT Virtual Disk"));
        Assert.Contains("不一致是矛盾不是錯誤", f.Value);
        Assert.Contains("差異本身就是要呈現的事實", f.Value);
    }

    // ── ST-018 DWPD 估計 ────────────────────────────────────────────

    [Fact]
    public void DWPD_讀不到不編一個出來()
    {
        var f = StorageDeepFactsService.DwpdFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不編一個 DWPD 出來", f.UnavailableReason);
    }

    [Fact]
    public void DWPD_通電時數為零不拿一小時硬除()
    {
        var f = StorageDeepFactsService.DwpdFact(At,
            () => new StorageDeepFactsService.DwpdInput(0, 100, 600));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不拿 1 小時硬除", f.UnavailableReason);
    }

    [Fact]
    public void DWPD_標稱未知時只給每日寫入量()
    {
        var f = StorageDeepFactsService.DwpdFact(At,
            () => new StorageDeepFactsService.DwpdInput(2400, 1024, null));
        Assert.Contains("標稱 TBW 未知", f.Value);
        Assert.Contains("不給 DWPD 比值", f.Value);
        Assert.Equal(10.24, f.NumericValue!.Value, 2);
    }

    [Fact]
    public void DWPD_標稱已知時推算比值並聲明是估計()
    {
        var f = StorageDeepFactsService.DwpdFact(At,
            () => new StorageDeepFactsService.DwpdInput(2400, 512, 512));
        Assert.Contains("推算 DWPD", f.Value);
        Assert.Contains("明標估計", f.Value);
        Assert.Contains("不是量測、也不是預測", f.Value);
    }

    // ── 整批 ─────────────────────────────────────────────────────────

    [Fact]
    public void 整批_六把鍵齊且不重複()
    {
        var facts = StorageDeepFactsService.Collect(At,
            () => [Ctrl("C0", "AHCI", 2)],
            () => [Br("0BDA:9210", "RTL9210", true)],
            () => new StorageDeepFactsService.SpacesState(1, 4, 2, "Mirror"),
            () => new StorageDeepFactsService.FirmwareTableState("2026-10-01", 42, 0, null),
            () => new StorageDeepFactsService.CrossCheckState(0, 0, 5, ""),
            () => new StorageDeepFactsService.DwpdInput(2400, 512, 512));

        Assert.Equal(6, facts.Count);
        Assert.Equal(6, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (string k in new[]
        {
            StorageDeepFactsService.ControllerTreeKey, StorageDeepFactsService.BridgeKey,
            StorageDeepFactsService.SpacesKey, StorageDeepFactsService.FirmwareKnownKey,
            StorageDeepFactsService.CrossCheckKey, StorageDeepFactsService.DwpdKey,
        })
            Assert.Contains(facts, f => f.Key == k);
    }
}
