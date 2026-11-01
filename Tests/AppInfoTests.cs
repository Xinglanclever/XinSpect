using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class AppInfoTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 版本標題與代號依語言模式選擇(bool simplified)
    {
        string codename = AppInfo.Codename;
        Assert.Equal(codename, AppInfo.VersionCodename(simplified));
        Assert.Equal($"XinSpect v{AppInfo.ShortVersion} {codename}", AppInfo.WindowTitle(simplified));
        Assert.Equal($"XinSpect v{AppInfo.Version} {codename}", AppInfo.HeaderTitle(simplified));
        Assert.Equal($"版本 {AppInfo.Version} ・ {codename} ・ 便攜單一執行檔", AppInfo.VersionMetadata(simplified));
    }

    /// <summary>
    /// 標題列與關於頁必須是同一個代號。3.13 之前兩邊各自寫死：AppInfo 是 Swiss、
    /// AboutView 被升版工具改成 Ultima XIII——同一個視窗裡兩個答案，而且都不會紅燈。
    /// 現在代號只在 <see cref="AppInfo.Codename"/> 一處，這條測試把兩邊釘在一起。
    /// </summary>
    [Fact]
    public void 關於頁的代號與AppInfo一致()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Views", "AboutView.xaml")))
            dir = dir.Parent;
        Assert.True(dir is not null, "找不到原始碼樹");

        string about = File.ReadAllText(Path.Combine(dir!.FullName, "Views", "AboutView.xaml"));
        Assert.Contains($"版本 {AppInfo.Version} ・ {AppInfo.Codename} ・", about);
    }
}
