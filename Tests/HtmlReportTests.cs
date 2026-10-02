using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// HTML 報告產生器（WP7）的契約：自足單檔（無外部資源）、所有事實文字一律 HTML 轉義、
/// 讀不到與警示列有可辨識的樣式標記、SHA-256 可離線重算驗證（改一字即現形）。
/// </summary>
public class HtmlReportTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);

    private static EvidenceFactRow Row(string category, string name, string value,
        FactAvailability availability = FactAvailability.Present, string? reason = null) =>
        new(category, name, value, "測試來源", "裝置直讀", false, availability, reason);

    [Fact]
    public void 報告自足_無外部資源且逐組成表()
    {
        var rows = new List<EvidenceFactRow>
        {
            Row("韌體安全", "BIOS 寫入保護", "最強保護：SMM_BWP=1"),
            Row("交叉對帳", "微碼一致性", "一致：兩個獨立來源一致"),
            Row("SMBus", "TSOD 溫度感測器（0x18）", "35.5°C"),
        };

        var html = HtmlReportService.Build(rows, "曦覽韌體安全報告", At);

        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase); // 無外部資源
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<meta charset=\"utf-8\">", html, StringComparison.Ordinal);
        Assert.Contains("韌體安全", html, StringComparison.Ordinal);
        Assert.Contains("SMBus", html, StringComparison.Ordinal);
        Assert.Contains("35.5°C", html, StringComparison.Ordinal);
        Assert.Contains("誠實聲明", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 事實文字一律轉義_注入不成立()
    {
        var rows = new List<EvidenceFactRow>
        {
            Row("測試", "惡意值", "<script>alert('x')</script> & \"引號\""),
        };

        var html = HtmlReportService.Build(rows, "報告", At);

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&amp;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 讀不到與警示列有樣式標記()
    {
        var rows = new List<EvidenceFactRow>
        {
            Row("韌體安全", "BIOS 寫入保護", "未保護：BLE=0"),
            Row("韌體安全", "SPI 快閃鎖定狀態", "", FactAvailability.ReadError, "讀取實體記憶體失敗"),
        };

        var html = HtmlReportService.Build(rows, "報告", At);

        Assert.Contains("class=\"warn\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"unavail\"", html, StringComparison.Ordinal);
        Assert.Contains("讀取失敗：讀取實體記憶體失敗", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 完整性_驗證通過_改一字現形_移除標記現形()
    {
        var rows = new List<EvidenceFactRow> { Row("韌體安全", "BIOS 寫入保護", "未保護：BLE=0") };
        var html = HtmlReportService.Build(rows, "報告", At);

        Assert.True(HtmlReportService.Verify(html));

        var tampered = html.Replace("SMM_BWP", "SMM_BWQ").Replace("BLE=0", "BLE=1");
        Assert.False(HtmlReportService.Verify(tampered)); // 內容被改
        Assert.False(HtmlReportService.Verify(html[..(html.Length / 2)])); // 被截斷
        Assert.False(HtmlReportService.Verify("<html><body>沒有標記</body></html>")); // 沒有完整性標記
    }
}
