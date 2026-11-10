using System;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// SMBIOS 表補完的解碼（Type 1 UUID、Type 3 機箱、Type 28／29 感測器）。
/// 重點有三：①位元組序不能弄錯（UUID 前三個欄位是小端序）；
/// ②「韌體沒填」的預設字串要如實標注而不是當成真值、也不是靜默改成空白；
/// ③溫度是有號值，只有 0x8000 是未知；冷卻裝置為 Type 27，Type 29 是電流探針。
/// </summary>
public class SmbiosExtendedTests
{
    // ── Type 1：UUID 的位元組序 ───────────────────────────────────────────

    [Fact]
    public void UUID_前三個欄位要依小端序解()
    {
        // 本機實況：Win32_ComputerSystemProduct.UUID = 6EFA5D53-B040-A576-2CC1-40B076A52CC0
        // SMBIOS 格式區的前三欄位以 little-endian 存放：time_low、time_mid、time_hi
        // 若照位元組順序直接印，第一個欄位會變成 535D_FA6E（看起來像 UUID 但是錯的）
        byte[] bytes = [0x53, 0x5D, 0xFA, 0x6E, 0x40, 0xB0, 0x76, 0xA5,
            0x2C, 0xC1, 0x40, 0xB0, 0x76, 0xA5, 0x2C, 0xC0];
        const string expected = "6EFA5D53-B040-A576-2CC1-40B076A52CC0";
        Assert.Equal(expected, SmbiosService.BufferToUuid(bytes));
        Assert.Equal("535DFA6E-40B0-76A5-2CC1-40B076A52CC0", SmbiosService.BufferToUuid(bytes, false));
        var data = new byte[0x18];
        data[0x04] = 1; data[0x05] = 2; data[0x06] = 3; data[0x07] = 4;
        bytes.CopyTo(data, 0x08);
        var decoded = new SmbiosService([new(1, 1, data, ["Maker", "Model", "Version", "Serial"])]);
        Assert.Equal(expected, Assert.Single(decoded.System, r => r.Key == "UUID").Value);
        var shortRecord = new SmbiosService([new(1, 1, new byte[8], [])]);
        Assert.DoesNotContain(shortRecord.System, r => r.Key == "UUID");
    }

    [Fact]
    public void UUID_全零與全F要如實區分不是有效UUID()
    {
        Assert.Contains("不可設定", SmbiosService.BufferToUuid(new byte[16]));
        Assert.Contains("目前不存在", SmbiosService.BufferToUuid(Enumerable.Repeat((byte)0xFF, 16).ToArray()));
        var tailOnly = new byte[16];
        tailOnly[15] = 1;
        Assert.Equal("00000000-0000-0000-0000-000000000001", SmbiosService.BufferToUuid(tailOnly));
    }

    // ── Type 1/3：韌體未填的預設字串 ──────────────────────────────────────

    [Theory]
    [InlineData("Default string")]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("System Serial Number")]
    [InlineData("System Product Name")]
    [InlineData("System manufacturer")]
    [InlineData("Not Specified")]
    [InlineData("None")]
    [InlineData("")]
    public void 韌體預設字串_要認出來(string text)
        => Assert.True(SmbiosService.IsPlaceholder(text));

    [Theory]
    [InlineData("181242565700856")]     // 本機主機板真實序號
    [InlineData("ROG RAMPAGE VI EXTREME OMEGA")]
    [InlineData("ASUSTeK COMPUTER INC.")]
    public void 真正的值_不得被當成預設字串(string text)
        => Assert.False(SmbiosService.IsPlaceholder(text));

    [Fact]
    public void 預設字串_要標注但不改掉原字串()
    {
        // 直接照登會讓人以為那串字是序號；靜默改掉又會隱藏「韌體沒填」這個事實
        string note = SmbiosService.PlaceholderNote("Default string");
        Assert.Contains("韌體未填", note);
        Assert.Equal("", SmbiosService.PlaceholderNote("181242565700856"));
    }

    // ── Type 3：機箱類型 ──────────────────────────────────────────────────

    [Theory]
    [InlineData(0x03, "桌上型")]
    [InlineData(0x07, "塔式")]
    [InlineData(0x11, "主伺服器")]
    [InlineData(0x12, "擴充機箱")]
    [InlineData(0x17, "機架式")]
    [InlineData(0x1C, "刀鋒機箱")]
    [InlineData(0x1D, "刀鋒容納機箱")]
    [InlineData(0x1F, "可轉換式")]
    public void 機箱類型_常見值要對得上(byte code, string expected)
        => Assert.Contains(expected, SmbiosService.ChassisTypeName(code));

    [Fact]
    public void 機箱類型_未收錄的代碼如實帶出()
        => Assert.Contains("未收錄", SmbiosService.ChassisTypeName(0x7E));

    [Theory]
    [InlineData(0x01, "其他")]
    [InlineData(0x03, "安全")]
    [InlineData(0x05, "重大")]
    public void 機箱開機狀態(byte code, string expected)
        => Assert.Equal(expected, SmbiosService.ChassisStateName(code));

    // ── Type 28：溫度探針的值欄位 ─────────────────────────────────────────

    [Fact]
    public void 溫度值_以十分之一度為單位()
    {
        // 450 → 45.0 °C（規格刻度是 1/10 度）
        Assert.Contains("45.0", SmbiosService.TemperatureText(450));
        Assert.Contains("0.0", SmbiosService.TemperatureText(0));
        Assert.Contains("-10.0", SmbiosService.TemperatureText(unchecked((ushort)-100)));

        var data = new byte[0x16];
        data[0x04] = 1; data[0x05] = 0x63;
        BitConverter.GetBytes((ushort)1000).CopyTo(data, 0x06);
        BitConverter.GetBytes(unchecked((ushort)-100)).CopyTo(data, 0x08);
        BitConverter.GetBytes((ushort)450).CopyTo(data, 0x14);
        var service = new SmbiosService([new(28, 1, data, ["CPU probe"])]);
        string text = Assert.Single(service.TemperatureProbes).Value;
        Assert.Contains("標稱 45.0", text);
        Assert.Contains("最小 -10.0", text);
        Assert.Contains("最大 100.0", text);
        Assert.Contains("正常", text);
        Assert.Contains("非即時", text);
    }

    [Fact]
    public void 溫度值_bit15為值未知_不得當成數值()
    {
        // 0x8000 是「值未知」。當成數值會讀出 3276.8 °C 這種荒謬的值，
        // 或（遮掉 bit 15 後）讀出 0.0 °C 這種看起來正常但其實沒量到的值。
        string text = SmbiosService.TemperatureText(0x8000);
        Assert.Contains("值未知", text);
        Assert.DoesNotContain("3276", text);
        Assert.DoesNotContain("0.0", text);
    }

    [Theory]
    [InlineData(0x03, "正常")]
    [InlineData(0x05, "重大")]
    [InlineData(0x06, "不可回復")]
    public void 溫度探針狀態(byte code, string expected)
        => Assert.Equal(expected, SmbiosService.TemperatureStatusName(code));

    // ── Type 27：冷卻裝置 ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0x03, "風扇")]
    [InlineData(0x06, "機箱風扇")]
    [InlineData(0x07, "電源供應器風扇")]
    [InlineData(0x10, "主動散熱")]
    public void 冷卻裝置類型(byte code, string expected)
        => Assert.Contains(expected, SmbiosService.CoolingTypeName(code));

    [Fact]
    public void 冷卻裝置值_bit15為值未知_不得當成轉速()
    {
        string text = SmbiosService.CoolingValueText(0x8000);
        Assert.Contains("值未知", text);
        Assert.DoesNotContain("32768", text);
    }

    [Fact]
    public void 冷卻裝置值_正常轉速如實帶出()
    {
        Assert.Equal("1200 RPM", SmbiosService.CoolingValueText(1200));
        Assert.Equal("40000 RPM", SmbiosService.CoolingValueText(40000));
        var data = new byte[0x0F];
        data[0x04] = 0xFF; data[0x05] = 0xFF;
        data[0x06] = 0x63;
        BitConverter.GetBytes((ushort)1200).CopyTo(data, 0x0C);
        data[0x0E] = 1;
        var service = new SmbiosService([
            new(27, 1, data, ["CPU fan"]), new(29, 2, new byte[0x16], ["Current probe"])]);
        var row = Assert.Single(service.CoolingDevices);
        Assert.Equal("CPU fan", row.Key);
        Assert.Contains("風扇", row.Value);
        Assert.Contains("正常", row.Value);
        Assert.Contains("標稱轉速 1200 RPM", row.Value);
        Assert.DoesNotContain("目前", row.Value);
    }

    [Fact]
    public void MemoryArrayMaximumCapacityConvertsKilobytesToGigabytes()
    {
        var array = new byte[0x0F];
        BitConverter.GetBytes(64u * 1024 * 1024).CopyTo(array, 0x07);
        var memory = new SmbiosService([new(16, 3, array, [])]);
        Assert.Equal("64 GB", Assert.Single(memory.MemoryArray, r => r.Key == "最大容量").Value);
    }
}
