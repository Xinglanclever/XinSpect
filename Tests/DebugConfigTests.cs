using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP16 第三組：核心除錯與測試簽章設定（登錄檔 SystemStartOptions 開機參數的純解析）。
/// 開機參數是 Windows 自己記的，解析只認關鍵字：DEBUG／DEBUGPORT／TESTSIGNING；
/// 沒有對應關鍵字＝未啟用（Present 的「沒有」），登錄值讀不到才是 ReadError。
/// </summary>
public class DebugConfigTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 開機參數_除錯與測試簽章關鍵字逐項釘值()
    {
        var full = XinSpect.DebugConfigService.Collect(At,
            probe: () => "NOEXECUTE=OPTIN DEBUG DEBUGPORT=COM1 BAUDRATE=115200 TESTSIGNING");

        var dbg = Assert.Single(full, f => f.Key == "dbg.kernel");
        Assert.Contains("啟用", dbg.Value);
        Assert.Contains("DEBUGPORT=COM1", dbg.Value);

        var ts = Assert.Single(full, f => f.Key == "dbg.testsigning");
        Assert.Contains("TESTSIGNING 關鍵字存在", ts.Value);

        Assert.Contains("NOEXECUTE=OPTIN", Assert.Single(full, f => f.Key == "dbg.start_options").Value);
    }

    [Fact]
    public void 開機參數_沒有關鍵字是未啟用不是讀不到()
    {
        var clean = XinSpect.DebugConfigService.Collect(At, probe: () => "NOEXECUTE=OPTIN");
        var dbg = Assert.Single(clean, f => f.Key == "dbg.kernel");
        Assert.Equal(FactAvailability.Present, dbg.Availability);
        Assert.Contains("未啟用", dbg.Value);

        var ts = Assert.Single(clean, f => f.Key == "dbg.testsigning");
        Assert.Equal(FactAvailability.Present, ts.Availability);
        Assert.Contains("未啟用", ts.Value);
    }

    [Fact]
    public void 開機參數_登錄讀不到如實三態()
    {
        var fail = XinSpect.DebugConfigService.Collect(At, probe: () => null);
        Assert.All(fail, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.Contains("SystemStartOptions", Assert.Single(fail, f => f.Key == "dbg.kernel").UnavailableReason);
    }
}
