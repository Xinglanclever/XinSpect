using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP15 第二組：服務盤點的彙整契約。來源是 WMI Win32_Service（usermode 零特權）——
/// 通路以注入探測替代，彙整（總數／執行中／自動／停用／非系統目錄）逐項釘值；
/// WMI 不可用如實三態。判定「非系統目錄」的純邏輯（PathName 不在 \Windows\ 下）單獨釘住。
/// </summary>
public class ServiceInventoryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    private static XinSpect.SvcEntry S(string name, string startMode, string state, string path, string startName = "LocalSystem") =>
        new(name, name + " 顯示名", startMode, state, startName, path);

    [Fact]
    public void 服務盤點_總數與狀態與啟動模式逐項釘值()
    {
        var entries = new List<XinSpect.SvcEntry>
        {
            S("wuauserv", "Auto", "Running", @"C:\Windows\system32\svchost.exe"),
            S("BITS", "Auto", "Stopped", @"C:\Windows\System32\svchost.exe"),
            S(" Spooler ".Trim(), "Auto", "Running", @"C:\WINDOWS\system32\spoolsv.exe"),
            S("ThirdPartySvc", "Manual", "Running", @"C:\Program Files\Acme\svc.exe"),
            S("DisabledSvc", "Disabled", "Stopped", @"C:\Windows\foo.exe"),
            S("NoPathSvc", "Manual", "Stopped", ""),
        };

        var facts = XinSpect.ServiceInventoryService.Collect(At, probe: () => entries);

        Assert.All(facts, f => Assert.Equal("系統與軟體", f.Category));
        Assert.Equal(6u, Assert.Single(facts, f => f.Key == "svc.total").NumericValue);
        Assert.Equal(3u, Assert.Single(facts, f => f.Key == "svc.running").NumericValue);
        Assert.Equal(3u, Assert.Single(facts, f => f.Key == "svc.start.auto").NumericValue);
        Assert.Equal(1u, Assert.Single(facts, f => f.Key == "svc.start.disabled").NumericValue);

        var thirdParty = Assert.Single(facts, f => f.Key == "svc.nonwindows");
        Assert.Equal(2u, thirdParty.NumericValue);                    // Program Files 與空路徑
        Assert.Contains("ThirdPartySvc", thirdParty.Value);           // 前幾個名稱要說得出來
    }

    [Fact]
    public void 服務盤點_WMI不可用如實三態()
    {
        var facts = XinSpect.ServiceInventoryService.Collect(At, probe: () => null);
        Assert.NotEmpty(facts);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Contains("WMI", f.UnavailableReason);
            Assert.Null(f.NumericValue);
        });
    }

    [Fact]
    public void 服務盤點_空清單是零不是讀不到()
    {
        var facts = XinSpect.ServiceInventoryService.Collect(At, probe: () => []);
        var total = Assert.Single(facts, f => f.Key == "svc.total");
        Assert.Equal(FactAvailability.Present, total.Availability);
        Assert.Equal(0u, total.NumericValue);
    }
}
