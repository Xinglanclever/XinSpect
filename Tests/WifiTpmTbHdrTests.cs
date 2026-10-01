using Xunit;

namespace XinSpect.Tests;

public class WifiSignalServiceTests
{
    [Fact]
    public void 讀取不丟例外且回List()
    {
        var rows = WifiSignalService.Read();
        Assert.NotNull(rows);
    }

    [Fact]
    public void RSSI解讀正確()
    {
        Assert.Contains("極佳", WifiSignalService.Interpret(-30));
        Assert.Contains("良好", WifiSignalService.Interpret(-50));
        Assert.Contains("可用", WifiSignalService.Interpret(-60));
        Assert.Contains("偏弱", WifiSignalService.Interpret(-72));
        Assert.Contains("極弱", WifiSignalService.Interpret(-90));
    }
}

public class TpmSecureBootServiceTests
{
    [Fact]
    public void 讀取不丟例外且回兩列()
    {
        var rows = TpmSecureBootService.Read();
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Label == "TPM");
        Assert.Contains(rows, r => r.Label == "Secure Boot");
    }
}

public class TbUsb4ServiceTests
{
    [Fact]
    public void 讀取不丟例外且回List()
    {
        var rows = TbUsb4Service.Read();
        Assert.NotNull(rows);
    }
}

public class HdrCapabilityServiceTests
{
    [Fact]
    public void 無EDID回誠實狀態()
    {
        var rows = HdrCapabilityService.Read([]);
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Label == "Windows HDR");
    }

    [Fact]
    public void 短EDID只回HDR開關()
    {
        var rows = HdrCapabilityService.Read(new byte[128]);
        Assert.Contains(rows, r => r.Label == "Windows HDR");
        Assert.DoesNotContain(rows, r => r.Label == "最大亮度");
    }
}
