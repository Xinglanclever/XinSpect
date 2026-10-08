using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// setupapi.dev.log 解析器與服務的契約：標記符號／裝置 ID／時間戳為語言中立（在地化標籤混入也照樣解出）、
/// 解析不出如實為 null、!!! 錯誤行計入所在區段、Boot Session 歸屬、檔案讀不到三態。
/// 記錄樣本以本機真實記錄的形狀為據（>>> 標頭、Section start、Exit status 行）。
/// </summary>
public class SetupApiLogTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    /// <summary>照真實記錄的形狀造樣本（標籤是英文；本地化版本的標籤不同但標記相同）。</summary>
    private const string RealShape =
        "[Device Install Log]\r\n" +
        "     OS Version = 10.0.26100\r\n" +
        "\r\n" +
        "[Boot Session: 2026/10/07 22:46:06.500]\r\n" +
        "\r\n" +
        ">>>  [Device Install (Hardware initiated) - USB\\VID_22D9&PID_2764\\74dbf3b0]\r\n" +
        ">>>  Section start 2026/10/07 20:52:40.082\r\n" +
        "     utl: {Select Drivers - USB\\VID_22D9&PID_2764\\74dbf3b0}\r\n" +
        "<<<  Section end 2026/10/07 20:52:40.562\r\n" +
        "<<<  [Exit status: SUCCESS]\r\n" +
        "\r\n" +
        ">>>  [Device Install (Hardware initiated) - PCI\\VEN_8086&DEV_A0BC&SUBSYS_86941043&REV_01\\3&11583659&0&A2]\r\n" +
        ">>>  Section start 2026/10/08 01:23:45.678\r\n" +
        "     dvi: {Build Driver List}\r\n" +
        "!!!  dvi: Something failed [0x80070002]\r\n" +
        "<<<  Section end 2026/10/08 01:23:46.000\r\n" +
        "<<<  [Exit status: FAILURE(0x80070002)]\r\n";

    [Fact]
    public void 真實形狀_逐欄解出起訖狀態與裝置ID()
    {
        var sections = SetupApiLog.Parse(RealShape);

        Assert.Equal(2, sections.Count);
        Assert.Equal("2026/10/07 20:52:40.082", sections[0].StartTime);
        Assert.Equal("2026/10/07 20:52:40.562", sections[0].EndTime);
        Assert.Equal("SUCCESS", sections[0].Status);
        Assert.Equal("USB\\VID_22D9&PID_2764\\74dbf3b0", sections[0].DeviceId);
        Assert.Equal("2026/10/07 22:46:06.500", sections[0].BootSession);

        Assert.Equal("PCI\\VEN_8086&DEV_A0BC&SUBSYS_86941043&REV_01\\3&11583659&0&A2", sections[1].DeviceId);
        Assert.Equal("FAILURE(0x80070002)", sections[1].Status);
        Assert.Equal(1, sections[1].ErrorLines);
        Assert.Equal(0, sections[0].ErrorLines);
    }

    [Fact]
    public void 在地化標籤混入_時間照樣解出_不靠標籤文字()
    {
        // 標籤換成繁中在地化形狀（標記符號與時間戳不變）——解析不得依賴「Section start」字樣
        var localized =
            ">>>  [裝置安裝 (硬體起始) - ACPI\\PNP0C0A\\1]\r\n" +
            ">>>  區段開始 2026/05/01 20:52:40.082\r\n" +
            "<<<  區段結束 2026/05/01 20:52:41.000\r\n" +
            "<<<  [結束狀態: 成功]\r\n";
        var sections = SetupApiLog.Parse(localized);

        var s = Assert.Single(sections);
        Assert.Equal("2026/05/01 20:52:40.082", s.StartTime);
        Assert.Equal("2026/05/01 20:52:41.000", s.EndTime);
        Assert.Equal("成功", s.Status);
        Assert.Equal("ACPI\\PNP0C0A\\1", s.DeviceId);
    }

    [Fact]
    public void 沒有時間戳的區段_如實為null_不猜()
    {
        var bare = ">>>  [Device Install - SWD\\WPDBUSENUM\\{1d7e66d9-465f-11f1-94ea-806e6f6e6963}]\r\n" +
                   "     utl: no timestamps here\r\n";
        var s = Assert.Single(SetupApiLog.Parse(bare));
        Assert.Null(s.StartTime);
        Assert.Null(s.EndTime);
        Assert.Null(s.Status);
        Assert.Equal("SWD\\WPDBUSENUM\\{1d7e66d9-465f-11f1-94ea-806e6f6e6963}", s.DeviceId);
    }

    [Fact]
    public void 破折號日期格式也認得()
    {
        var dash = ">>>  [Device Install - USB\\VID_1234&PID_5678\\ABC]\r\n" +
                   ">>>  Section start 2026-05-01 20:52:40\r\n" +
                   "<<<  Section end 2026-05-01 20:52:41\r\n";
        var s = Assert.Single(SetupApiLog.Parse(dash));
        Assert.Equal("2026-05-01 20:52:40", s.StartTime);
        Assert.Equal("2026-05-01 20:52:41", s.EndTime);
    }

    [Fact]
    public void 完全不是記錄形狀_如實回空_不是錯誤()
    {
        Assert.Empty(SetupApiLog.Parse("hello world\r\nnot a setupapi log\r\n"));
    }

    // ── 服務層：三態與事實形狀 ──────────────────────────────────────────────

    [Fact]
    public void 服務_給出總數與最近區段_誠實界線在文字裡()
    {
        var facts = SetupApiTimelineService.CollectFromContent(RealShape, At);

        var count = facts.Single(x => x.Key == SetupApiTimelineService.CountKey);
        Assert.Equal(FactAvailability.Present, count.Availability);
        Assert.Equal(2, count.NumericValue);
        Assert.Contains("2 個安裝區段", count.Value);
        Assert.Contains("不解析在地化文字", count.Value);
        Assert.Contains("安裝歷史不是線上狀態", count.Value);

        var recent = Assert.Single(facts, x => x.Key == "setuptl.recent");
        Assert.Contains("USB\\VID_22D9", recent.Value);
        Assert.Contains("SUCCESS", recent.Value);
    }

    [Fact]
    public void 服務_記錄讀到但沒有區段_如實標讀取失敗不冒充()
    {
        var fact = Assert.Single(SetupApiTimelineService.CollectFromContent("nothing here", At));
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("解析不出任何區段", fact.UnavailableReason);
    }

    [Fact]
    public void 服務_檔案不存在_如實標不適用()
    {
        var fact = Assert.Single(SetupApiTimelineService.Collect(
            At, basePath: "不存在-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("不存在", fact.UnavailableReason);
    }

    [Fact]
    public void 服務_注入來源回null_如實標讀取失敗()
    {
        var fact = Assert.Single(SetupApiTimelineService.Collect(At, readLog: () => null!));
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
    }
}
