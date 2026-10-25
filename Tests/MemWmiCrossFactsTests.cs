using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// U10-OS-0030（<c>mem.wmi.cross</c>）：Win32_PhysicalMemory 作為 SPD 一致性審計第四源的對照邏輯。
/// 合成資料：WMI 列與既有正規化輸入（SpdAuditSourceInput），不讀實機；重點是
/// 配對（序號最強、料號次之、配不上不強配）、矛盾計數（兩面都在才算）、缺源如實標、讀不到三態。
/// </summary>
public class MemWmiCrossFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static WmiPhysicalMemoryRow Wmi(string? serial, string? part, long? bytes) =>
        new("ChannelA-DIMM0", "Samsung", part, serial, bytes, 5600);

    private static SpdAuditSourceInput Native(string? serial, string? part, int? mib) =>
        new(SpdAuditSourceKind.NativeSpd, "原生 SPD", "ChannelA-DIMM0", "Samsung", part, serial, mib);

    [Fact]
    public void 序號正規化後一致_容量換算一致_零矛盾()
    {
        var state = MemWmiCrossFactsService.Cross(
            [Wmi("a1-b2-c3", "M425R2GA3PB0-CWMOD", 17179869184L)],
            [Native("A1B2C3", "M425R2GA3PB0CWMOD", 16384)], null);
        Assert.Equal(1, state.WmiModules);
        Assert.Equal(1, state.NativeModules);
        Assert.Equal(1, state.ComparedModules);
        Assert.Equal(0, state.SerialConflicts);
        Assert.Equal(0, state.CapacityConflicts);
        Assert.Equal(0, state.PartConflicts);
    }

    [Fact]
    public void 序號不同但料號可唯一配對_記序號矛盾一次()
    {
        // 空殼序號（全 0／全 F）不算矛盾——那是缺資料；兩面都有真序號且不同才算。
        var state = MemWmiCrossFactsService.Cross(
            [Wmi("DEADBEEF", "M425R2GA3PB0", 17179869184L)],
            [Native("A1B2C3", "M425R2GA3PB0", 16384)], null);
        Assert.Equal(1, state.ComparedModules);
        Assert.Equal(1, state.SerialConflicts);
        Assert.Equal(0, state.CapacityConflicts);
    }

    [Fact]
    public void 空殼序號兩面都在但視為缺資料_不記序號矛盾()
    {
        var state = MemWmiCrossFactsService.Cross(
            [Wmi("FFFFFFFF", "M425R2GA3PB0", 17179869184L)],
            [Native("A1B2C3", "M425R2GA3PB0", 16384)], null);
        Assert.Equal(1, state.ComparedModules);
        Assert.Equal(0, state.SerialConflicts);
    }

    [Fact]
    public void 容量不一致_記容量矛盾()
    {
        var state = MemWmiCrossFactsService.Cross(
            [Wmi("A1B2C3", "M425R2GA3PB0", 8589934592L)],   // 8 GiB
            [Native("A1B2C3", "M425R2GA3PB0", 16384)], null); // 16 GiB
        Assert.Equal(1, state.ComparedModules);
        Assert.Equal(1, state.CapacityConflicts);
    }

    [Fact]
    public void 空殼序號不參與配對與矛盾判定()
    {
        // 兩面都只有全 F 序號＋料號可配：配得上，但序號面如實不算矛盾（空殼＝缺資料）。
        var state = MemWmiCrossFactsService.Cross(
            [Wmi("FFFFFFFFFFFFFFFF", "PART-1", 17179869184L)],
            null,
            [new SpdAuditSourceInput(SpdAuditSourceKind.Smbios, "SMBIOS Type 17", "DIMM0", null, "PART-1", "FFFFFFFF", 16384)]);
        Assert.Equal(1, state.ComparedModules);
        Assert.Equal(0, state.SerialConflicts);
    }

    [Fact]
    public void 配不上的條不強配也不記矛盾_缺源如實寫進值()
    {
        var fact = MemWmiCrossFactsService.Collect(At,
            [Native("A1B2C3", "PART-1", 16384)],
            null,
            () => [Wmi("A1B2C3", "PART-1", 17179869184L), Wmi("ZZZZZZ", "PART-2", 8589934592L)]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(2, fact.NumericValue);
        Assert.Contains("可配對 1 條", fact.Value);
        Assert.Contains("缺源：SMBIOS", fact.Value);
        Assert.False(fact.Sensitive);   // 只列計數，不列序號或料號原值
        Assert.DoesNotContain("A1B2C3", fact.Value);
    }

    [Fact]
    public void WMI讀不到_如實ReadError且不帶數值()
    {
        var fact = MemWmiCrossFactsService.Collect(At, null, null, () => null);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("讀不到", fact.UnavailableReason ?? "");
    }
}
