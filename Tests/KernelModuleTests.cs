using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP14 核心模組面：EnumDeviceDrivers 載入清單的彙整契約。\\Windows\\ 下的模組只計數
/// （簽章面由 DriverAudit 的 Win32_PnPSignedDriver 涵蓋）；非系統目錄的載入模組逐檔
/// Authenticode 驗證（DRIVER_ACTION_VERIFY）——結果如實帶原始碼不解讀。
/// </summary>
public class KernelModuleTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    private static XinSpect.KernelModuleEntry M(string path, bool? sig = null, string note = "") =>
        new(path, sig, note);

    [Fact]
    public void 核心模組_計數與非系統目錄與驗證結果逐項釘值()
    {
        var entries = new List<XinSpect.KernelModuleEntry>
        {
            M(@"C:\Windows\system32\ntoskrnl.exe"),
            M(@"C:\Windows\System32\drivers\tcpip.sys"),
            M(@"C:\Program Files\Acme\acme.sys", true),
            M(@"C:\Program Files\Beta\beta.sys", false, "0x800B0100（TRUST_E_NOSIGNATURE）"),
            M(@"C:\Program Files\Gamma\gamma.sys", null, "檔案不存在——無法驗證"),
        };

        var facts = XinSpect.KernelModuleService.Collect(At, probe: () => entries);

        Assert.All(facts, f => Assert.Equal("系統與軟體", f.Category));
        Assert.Equal(5u, Assert.Single(facts, f => f.Key == "kmod.total").NumericValue);

        var nonWin = Assert.Single(facts, f => f.Key == "kmod.nonwindows");
        Assert.Equal(3u, nonWin.NumericValue);
        Assert.Contains("acme.sys", nonWin.Value);
        Assert.Contains("beta.sys", nonWin.Value);

        var sig = Assert.Single(facts, f => f.Key == "kmod.nonwindows.sig");
        Assert.Contains("未通過", sig.Value);
        Assert.Contains("beta.sys", sig.Value);
        Assert.Contains("0x800B0100", sig.Value);
        Assert.Contains("檔案不存在", sig.Value);
        Assert.DoesNotContain("acme.sys", sig.Value);    // 通過的只計數不逐名
    }

    [Fact]
    public void 核心模組_全部有效與讀不到分得清楚()
    {
        var allGood = XinSpect.KernelModuleService.Collect(At, probe: () => new List<XinSpect.KernelModuleEntry>
        {
            M(@"C:\Windows\system32\ntoskrnl.exe"),
            M(@"C:\Program Files\Acme\acme.sys", true),
        });
        var sig = Assert.Single(allGood, f => f.Key == "kmod.nonwindows.sig");
        Assert.Contains("均通過", sig.Value);

        var fail = XinSpect.KernelModuleService.Collect(At, probe: () => null);
        Assert.All(fail, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.Contains("EnumDeviceDrivers", Assert.Single(fail, f => f.Key == "kmod.total").UnavailableReason);
    }

    [Fact]
    public void 核心模組_路徑分類大小寫與根目錄()
    {
        Assert.True(XinSpect.KernelModuleService.IsWindowsDirectory(@"c:\windows\system32\drivers\tcpip.sys"));
        Assert.True(XinSpect.KernelModuleService.IsWindowsDirectory(@"C:\Windows\System32\drivers\acme.sys"));
        Assert.False(XinSpect.KernelModuleService.IsWindowsDirectory(@"C:\Program Files\Acme\acme.sys"));
        Assert.False(XinSpect.KernelModuleService.IsWindowsDirectory(""));

        // patch-35：嚴格前綴——子字串命中不再是系統模組（免驗 Authenticode 的豁免面收緊）
        Assert.False(XinSpect.KernelModuleService.IsWindowsDirectory(@"C:\Tools\Windows\implant.sys"));
        Assert.False(XinSpect.KernelModuleService.IsWindowsDirectory(@"C:\WindowsFoo\bar.sys"));
        Assert.False(XinSpect.KernelModuleService.IsWindowsDirectory(@"C:\Windows\system32\..\..\Tools\evade.sys"));
        Assert.False(XinSpect.KernelModuleService.IsWindowsDirectory(@"system32\drivers\evil.sys"));   // 相對路徑不賭 CWD

        // EnumDeviceDrivers 的 NT 原生路徑語形照樣分得到（\SystemRoot\ 語形同時是連帶修正：
        // 舊版不命中、把核心模組送去驗不存在的 Win32 路徑）
        Assert.True(XinSpect.KernelModuleService.IsWindowsDirectory(@"\??\C:\Windows\System32\drivers\tcpip.sys"));
        Assert.True(XinSpect.KernelModuleService.IsWindowsDirectory(@"\SystemRoot\System32\ntoskrnl.exe"));
    }

    [Fact]
    public void 核心模組_列舉截斷_如實標記不少報()
    {
        var facts = XinSpect.KernelModuleService.Collect(At,
            probe: () => new List<XinSpect.KernelModuleEntry> { M(@"C:\Program Files\Acme\acme.sys", true) },
            enumerationTruncated: true);

        var total = Assert.Single(facts, f => f.Key == "kmod.total");
        Assert.Contains("截斷", total.Value);
        Assert.Contains("實際載入更多", total.Value);
    }
}
