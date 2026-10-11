using Xunit;

namespace XinSpect.Tests;

/// <summary>Vol2 SB 域（SMBIOS 表層深化）事實產出的測試：合成結構驅動，不碰韌體。
/// 位移出處＝dmidecode 3.x（Type 9/17/39/42），測試把關鍵位移釘住。</summary>
public sealed class SmbiosDeepFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Type03_摘要含四種結構的關鍵欄位()
    {
        var facts = SmbiosDeepFactsService.Collect(At, () =>
        [
            Type0("AMI", "2.31", "09/14/2024"),
            Type1("ASUS", "System Product"),
            Type2("ASUSTeK", "ROG RAMPAGE VI EXTREME OMEGA", "Rev 1.xx"),
            Type3(0x11, lockPresent: true), // 0x11＝Main Server Chassis
        ]);

        Assert.Equal("4/4", facts.Single(f => f.Key == "sb.type03.count").Value);
        string v = facts.Single(f => f.Key == "sb.type03").Value;
        Assert.Contains("AMI", v);
        Assert.Contains("ASUS", v);
        Assert.Contains("ROG RAMPAGE VI EXTREME OMEGA", v);
        Assert.Contains("機箱鎖存在", v);
    }

    [Fact]
    public void Type9_插槽計數與M2槽位對照()
    {
        var facts = SmbiosDeepFactsService.Collect(At, () =>
        [
            Slot(designation: "M.2_1", type: 0x17, width: 0x08, usage: 0x04), // M.2 Socket 3 / x4 / In Use
            Slot(designation: "PCIEX16_1", type: 0x0A, width: 0x0D, usage: 0x03), // PCIe x16
        ]);

        Assert.Equal("2", facts.Single(f => f.Key == "sb.slot.count").Value);
        Assert.Contains("PCIEX16_1", facts.Single(f => f.Key == "sb.slot.summary").Value);
        Assert.Equal("1", facts.Single(f => f.Key == "sb.m2slot.count").Value);
        Assert.Contains("M.2_1", facts.Single(f => f.Key == "sb.m2slot.summary").Value);
        Assert.Contains("M.2 Socket 3", facts.Single(f => f.Key == "sb.m2slot.summary").Value);
    }

    [Fact]
    public void Type17_容量速率與序號有無_序號內容不入事實值()
    {
        var facts = SmbiosDeepFactsService.Collect(At, () =>
        [
            Dimm(locator: "DIMM_A1", bank: "", sizeWord: 0x4000 /*16GiB*/, type: 0x1A /*DDR4*/,
                 speed: 3600, configured: 3200, rank: 1, serialIndex: 2, strings: ["Micron", "ABC12345"]),
        ]);

        Assert.Equal("1", facts.Single(f => f.Key == "sb.dim.count").Value);
        Assert.Equal("1", facts.Single(f => f.Key == "sb.dim.serial.count").Value);
        string v = facts.Single(f => f.Key == "sb.dim.summary").Value;
        Assert.Contains("16384 MB", v);
        Assert.Contains("DDR4", v);
        Assert.Contains("3600 MT/s", v);
        Assert.Contains("實跑 3200", v);
        Assert.DoesNotContain("ABC12345", v); // 序號只在「有無」計數，不進事實值
    }

    [Fact]
    public void Type17_擴充容量_0x7FFF時改讀0x1C的dword()
    {
        var facts = SmbiosDeepFactsService.Collect(At, () => [Dimm("DIMM_A1", "", 0x7FFF, 0x1A, 3600, 0, 0, 0, [], extendedMb: 32768)]);

        Assert.Contains("32768 MB", facts.Single(f => f.Key == "sb.dim.summary").Value);
    }

    [Fact]
    public void Type17_無序號時計數為零()
    {
        var facts = SmbiosDeepFactsService.Collect(At, () => [Dimm("DIMM_A1", "", 0x4000, 0x1A, 3600, 0, 0, serialIndex: 0, strings: [])]);
        Assert.Equal("0", facts.Single(f => f.Key == "sb.dim.serial.count").Value);
    }

    [Fact]
    public void Type39_電源供應器功率與在位狀態()
    {
        var facts = SmbiosDeepFactsService.Collect(At, () =>
        [
            Psu(name: "PSU1", manufacturer: "Seasonic", maxPower: 750, characteristics: 0x0002),
        ]);

        string v = facts.Single(f => f.Key == "sb.psu").Value;
        Assert.Contains("750 W", v);
        Assert.Contains("存在", v);
    }

    [Fact]
    public void Type39_功率未回報時如實說()
    {
        var facts = SmbiosDeepFactsService.Collect(At, () => [Psu("PSU1", "Seasonic", 0x8000, 0x0002)]);
        Assert.Contains("功率未回報", facts.Single(f => f.Key == "sb.psu").Value);
    }

    [Fact]
    public void Type42_管理控制器介面型別()
    {
        var facts = SmbiosDeepFactsService.Collect(At, () => [Mctl(0x02)]);
        Assert.Contains("KCS", facts.Single(f => f.Key == "sb.mctl").Value);
    }

    [Fact]
    public void Type11_OEM字串_標記為敏感()
    {
        var facts = SmbiosDeepFactsService.Collect(At, () => [OemStrings(["OEM1", "OEM2"])]);

        Assert.Equal("2", facts.Single(f => f.Key == "sb.oem.count").Value);
        Assert.Contains("OEM1", facts.Single(f => f.Key == "sb.oem.list").Value);
        Assert.True(facts.Single(f => f.Key == "sb.oem.list").Sensitive);
    }

    [Fact]
    public void 缺表時_13把鍵仍在且三態誠實()
    {
        var facts = SmbiosDeepFactsService.Collect(At, () => []);

        Assert.Equal(13, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotSupported, f.Availability));
        Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason)));
    }

    [Fact]
    public void 讀表丟例外_標ReadError()
    {
        var facts = SmbiosDeepFactsService.Collect(At, () => throw new InvalidOperationException("boom"));
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.Contains("boom", facts[0].UnavailableReason!);
    }

    [Fact]
    public void 有表但缺某型別_該項標NotApplicable而非0()
    {
        var facts = SmbiosDeepFactsService.Collect(At, () => [Type0("AMI", "2.31", "09/14/2024")]);

        Assert.Equal("1/4", facts.Single(f => f.Key == "sb.type03.count").Value);
        Assert.Equal(FactAvailability.NotApplicable, facts.Single(f => f.Key == "sb.psu").Availability);
        Assert.Equal(FactAvailability.NotApplicable, facts.Single(f => f.Key == "sb.mctl").Availability);
        Assert.Equal("0", facts.Single(f => f.Key == "sb.slot.count").Value);
    }

    // ── 合成結構工具 ─────────────────────────────────────────────────────

    private static SmbiosStruct Type0(string vendor, string version, string date)
    {
        var s = new SmbiosStruct(0, 0, new byte[0x09], [vendor, version, date]);
        s.Data[0x04] = 1; s.Data[0x05] = 2; s.Data[0x08] = 3;
        return s;
    }

    private static SmbiosStruct Type1(string manufacturer, string product)
    {
        var s = new SmbiosStruct(1, 1, new byte[0x1B], [manufacturer, product]);
        s.Data[0x04] = 1; s.Data[0x05] = 2;
        return s;
    }

    private static SmbiosStruct Type2(string manufacturer, string product, string version)
    {
        var s = new SmbiosStruct(2, 2, new byte[0x08], [manufacturer, product, version]);
        s.Data[0x04] = 1; s.Data[0x05] = 2; s.Data[0x06] = 3;
        return s;
    }

    private static SmbiosStruct Type3(byte chassisType, bool lockPresent)
    {
        var s = new SmbiosStruct(3, 3, new byte[0x07], ["ChassisMaker"]);
        s.Data[0x04] = 1;
        s.Data[0x05] = (byte)(chassisType | (lockPresent ? 0x80 : 0x00));
        return s;
    }

    private static SmbiosStruct Slot(string designation, byte type, byte width, byte usage)
    {
        var s = new SmbiosStruct(9, 9, new byte[0x0C], [designation]);
        s.Data[0x04] = 1; s.Data[0x05] = type; s.Data[0x06] = width; s.Data[0x07] = usage;
        return s;
    }

    private static SmbiosStruct Dimm(string locator, string bank, ushort sizeWord, byte type, ushort speed, ushort configured, byte rank, byte serialIndex, string[] strings, uint extendedMb = 0)
    {
        var s = new SmbiosStruct(17, 17, new byte[0x22], strings);
        s.Data[0x10] = 1;                 // Device Locator
        if (bank.Length > 0) { s.Data[0x11] = 2; } // Bank Locator（索引 2 由呼叫端保證存在）
        BitConverter.GetBytes(sizeWord).CopyTo(s.Data, 0x0C);
        s.Data[0x12] = type;
        BitConverter.GetBytes(speed).CopyTo(s.Data, 0x15);
        s.Data[0x18] = serialIndex;
        s.Data[0x1B] = rank;
        BitConverter.GetBytes(extendedMb).CopyTo(s.Data, 0x1C);
        BitConverter.GetBytes(configured).CopyTo(s.Data, 0x20);
        return s;
    }

    private static SmbiosStruct Psu(string name, string manufacturer, ushort maxPower, ushort characteristics)
    {
        var s = new SmbiosStruct(39, 39, new byte[0x10], ["Loc", name, manufacturer]);
        s.Data[0x04] = 1; s.Data[0x05] = 1; s.Data[0x06] = 2; s.Data[0x07] = 3;
        BitConverter.GetBytes(maxPower).CopyTo(s.Data, 0x0C);
        BitConverter.GetBytes(characteristics).CopyTo(s.Data, 0x0E);
        return s;
    }

    private static SmbiosStruct Mctl(byte interfaceType)
    {
        var s = new SmbiosStruct(42, 42, new byte[0x09], []);
        s.Data[0x04] = interfaceType;
        return s;
    }

    private static SmbiosStruct OemStrings(string[] strings) => new(11, 11, new byte[0x05], strings);

}
