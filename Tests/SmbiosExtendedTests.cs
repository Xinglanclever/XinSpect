using System;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// SMBIOS 表補完的解碼（Type 1 UUID、Type 3 機箱、Type 28／29 感測器）。
/// 重點有三：①位元組序不能弄錯（UUID 前三個欄位是小端序）；
/// ②「韌體沒填」的預設字串要如實標注而不是當成真值、也不是靜默改成空白；
/// ③感測器值欄位的 bit 15 是「值未知」，當成數值會讀出一個不存在的量測值。
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
        string uuid = SmbiosService.BufferToUuid(
            timeLow: 0x6EFA5D53, timeMid: 0xB040, timeHigh: 0xA576, clockAndNode: 0x2CC1);
        Assert.Equal("6EFA5D53-B040-A576-2CC1", uuid);
    }

    [Fact]
    public void UUID_全零與全F要如實區分不是有效UUID()
    {
        Assert.Contains("未編程", SmbiosService.BufferToUuid(0, 0, 0, 0));
        Assert.Contains("不支援", SmbiosService.BufferToUuid(0xFFFFFFFF, 0xFFFF, 0xFFFF, 0xFFFF));
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
    [InlineData(0x11, "工作站")]
    [InlineData(0x12, "伺服器")]
    [InlineData(0x17, "可攜式")]
    [InlineData(0x25, "刀鋒機箱")]
    [InlineData(0x27, "機架式")]
    [InlineData(0x1F, "主機式")]
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
    [InlineData(0x05, "過高（重大）")]
    [InlineData(0x07, "過低（重大）")]
    public void 溫度探針狀態(byte code, string expected)
        => Assert.Equal(expected, SmbiosService.TemperatureStatusName(code));

    // ── Type 29：冷卻裝置 ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0x03, "風扇")]
    [InlineData(0x06, "機箱風扇")]
    [InlineData(0x07, "電源供應器風扇")]
    [InlineData(0x10, "幫浦")]
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
        => Assert.Contains("1200", SmbiosService.CoolingValueText(1200));
}
