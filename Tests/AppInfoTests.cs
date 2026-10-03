using Xunit;

namespace XinSpect.Tests;

public class AppInfoTests
{
    [Theory]
    [InlineData(false, "Olympus")]
    [InlineData(true, "Olympus")]
    public void 版本標題與代號依語言模式選擇(bool simplified, string codename)
    {
        Assert.Equal(codename, AppInfo.VersionCodename(simplified));
        Assert.Equal($"XinSpect v{AppInfo.ShortVersion} {codename}", AppInfo.WindowTitle(simplified));
        Assert.Equal($"XinSpect v{AppInfo.Version} {codename}", AppInfo.HeaderTitle(simplified));
        Assert.Equal($"版本 {AppInfo.Version} ・ {codename} ・ 便攜單一執行檔", AppInfo.VersionMetadata(simplified));
    }
}
