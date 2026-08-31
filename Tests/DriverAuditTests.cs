using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 驅動程式稽核的判讀規則。這裡要守住的是「不製造假警報」——尤其是微軟隨附驅動那個
/// 2006-06-21 佔位日期：一旦拿它算年紀，上百支正常驅動會全部被標紅，整頁就沒人看了。
/// </summary>
public class DriverAuditTests
{
    private static readonly DateTime Now = new(2026, 8, 31);

    // ── CIM 日期 ──────────────────────────────────────────────────────────

    [Fact]
    public void CIM日期解析得出年月日()
    {
        Assert.Equal(new DateTime(2023, 8, 15), DriverAuditDecoder.ParseCimDate("20230815000000.000000-000"));
        Assert.Equal(new DateTime(2006, 6, 21), DriverAuditDecoder.ParseCimDate("20060621000000.000000-000"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2023")]
    [InlineData("00000000000000.000000-000")]   // 全零＝WMI 說「沒有日期」，不是西元 0 年
    [InlineData("尚未提供日期")]
    public void 日期缺漏或畸形一律回空而不是猜一個(string? raw)
    {
        Assert.Null(DriverAuditDecoder.ParseCimDate(raw));
    }

    // ── 佔位日期 ──────────────────────────────────────────────────────────

    [Fact]
    public void 微軟隨附驅動的佔位日期認得出來()
    {
        Assert.True(DriverAuditDecoder.IsInboxPlaceholder("Microsoft", new DateTime(2006, 6, 21)));
        // 提供者字串實際上長這樣
        Assert.True(DriverAuditDecoder.IsInboxPlaceholder("Microsoft Corporation", new DateTime(2006, 6, 21)));
    }

    [Fact]
    public void 同一天但不是微軟就不算佔位()
    {
        Assert.False(DriverAuditDecoder.IsInboxPlaceholder("Intel", new DateTime(2006, 6, 21)));
        Assert.False(DriverAuditDecoder.IsInboxPlaceholder("Microsoft", new DateTime(2006, 6, 22)));
        Assert.False(DriverAuditDecoder.IsInboxPlaceholder("Microsoft", null));
    }

    [Fact]
    public void 微軟隨附的老驅動不因年紀被標記()
    {
        // 這是本測試存在的主要理由：關鍵類別＋二十年前的日期，照一般規則會被標成 1
        var (verdict, severity) = DriverAuditDecoder.Judge(
            signed: true, date: new DateTime(2006, 6, 21), provider: "Microsoft", cls: "SYSTEM", now: Now);
        Assert.Equal(0, severity);
        Assert.Contains("佔位", verdict);
    }

    // ── 判讀 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 未簽章一律最高嚴重度且說明簽章檢查曾被放行()
    {
        var (verdict, severity) = DriverAuditDecoder.Judge(
            signed: false, date: new DateTime(2025, 1, 1), provider: "某人", cls: "NET", now: Now);
        Assert.Equal(2, severity);
        Assert.Contains("未經數位簽章", verdict);
    }

    [Fact]
    public void 關鍵類別的老驅動被標記但明說舊不等於壞()
    {
        var (verdict, severity) = DriverAuditDecoder.Judge(
            signed: true, date: new DateTime(2018, 3, 1), provider: "Intel", cls: "DISPLAY", now: Now);
        Assert.Equal(1, severity);
        Assert.Contains("舊不等於壞", verdict);
        Assert.Contains("顯示卡", verdict);
    }

    [Fact]
    public void 非關鍵類別的老驅動不標記()
    {
        var (_, severity) = DriverAuditDecoder.Judge(
            signed: true, date: new DateTime(2010, 1, 1), provider: "廠商", cls: "PRINTER", now: Now);
        Assert.Equal(0, severity);
    }

    [Fact]
    public void 關鍵類別但還沒到年限就不標記()
    {
        var (_, severity) = DriverAuditDecoder.Judge(
            signed: true, date: Now.AddYears(-DriverAuditDecoder.OldYears).AddDays(30), provider: "Intel", cls: "NET", now: Now);
        Assert.Equal(0, severity);
    }

    [Fact]
    public void 沒有日期就承認不知道新舊而不是當成很舊()
    {
        var (verdict, severity) = DriverAuditDecoder.Judge(
            signed: true, date: null, provider: "廠商", cls: "DISPLAY", now: Now);
        Assert.Equal(0, severity);
        Assert.Contains("不推測", verdict);
    }

    // ── 類別與年紀的文字 ──────────────────────────────────────────────────

    [Fact]
    public void 關鍵類別的名單就是會吃效能與穩定性的那幾類()
    {
        foreach (string c in new[] { "DISPLAY", "NET", "SCSIADAPTER", "HDC", "SYSTEM", "USB", "DISKDRIVE" })
            Assert.True(DriverAuditDecoder.IsCritical(c), c + " 應屬關鍵類別");
        foreach (string c in new[] { "PRINTER", "MOUSE", "MONITOR", "" })
            Assert.False(DriverAuditDecoder.IsCritical(c), c + " 不該算關鍵類別");
    }

    [Fact]
    public void 沒收錄的類別顯示原文而不是吞掉()
    {
        Assert.Equal("顯示卡", DriverAuditDecoder.ClassLabel("DISPLAY"));
        Assert.Equal("WPD", DriverAuditDecoder.ClassLabel("WPD"));
        Assert.Equal("—", DriverAuditDecoder.ClassLabel(""));
        Assert.Equal("—", DriverAuditDecoder.ClassLabel(null));
    }

    [Theory]
    [InlineData(2020, 8, 31, "6 年")]
    [InlineData(2020, 5, 31, "6 年 3 個月")]
    [InlineData(2026, 6, 30, "2 個月")]
    [InlineData(2026, 8, 20, "不到一個月")]
    public void 年紀只給年月不編造日數精度(int y, int m, int d, string expected)
    {
        Assert.Equal(expected, DriverAuditDecoder.AgeText(new DateTime(y, m, d), Now));
    }

    [Fact]
    public void 年紀缺漏或在未來都如實說()
    {
        Assert.Equal("日期未提供", DriverAuditDecoder.AgeText(null, Now));
        Assert.Equal("日期在未來", DriverAuditDecoder.AgeText(Now.AddDays(1), Now));
    }

    // ── 清單搜尋 ──────────────────────────────────────────────────────────

    [Fact]
    public void 搜尋比對裝置類別版本提供者與INF()
    {
        var row = new DriverRow
        {
            Device = "Intel(R) Ethernet Connection I219-V", DeviceClass = "NET",
            Version = "12.19.2.60", Provider = "Intel", Inf = "oem47.inf",
        };
        Assert.True(DriverAuditDecoder.Matches(row, "i219"));        // 不分大小寫
        Assert.True(DriverAuditDecoder.Matches(row, "網路介面"));     // 比對繁中類別
        Assert.True(DriverAuditDecoder.Matches(row, "NET"));         // 也比對類別原文
        Assert.True(DriverAuditDecoder.Matches(row, "oem47"));
        Assert.True(DriverAuditDecoder.Matches(row, ""));            // 空字串＝不篩選
        Assert.False(DriverAuditDecoder.Matches(row, "realtek"));
    }

    [Fact]
    public void 簽章欄的短標籤說得出被標記的理由()
    {
        Assert.Equal("未簽章", new DriverRow { Signed = false, Severity = 2 }.SignText);
        Assert.Equal("日期偏舊", new DriverRow { Signed = true, Severity = 1 }.SignText);
        Assert.Equal("已簽章", new DriverRow { Signed = true, Severity = 0 }.SignText);
    }
}
