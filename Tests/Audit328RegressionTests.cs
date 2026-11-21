using System.IO;
using Xunit;

namespace XinSpect.Tests;

public sealed class Audit328RegressionTests
{
    [Fact]
    public void 網址工具保留路由片段和尾斜線且不誤合併()
    {
        var service = new ToolboxService();
        string[] urls = ["https://example.com/app/#/scan", "https://example.com/app/#/results", "https://example.com/app"];
        foreach (var (url, index) in urls.Select((url, index) => (url, index)))
        {
            var added = service.AddCustomTool(new ToolboxCustomTool
            {
                Name = "route " + index, Kind = ToolKind.WebLink, Target = url,
            });
            Assert.True(added.Added);
            Assert.Equal(url, added.Item.Target);
        }
    }

    [Fact]
    public void 空參數與沒有參數是不同動作()
    {
        string path = Path.Combine(Path.GetTempPath(), "audit328-args.exe");
        Assert.NotEqual(ToolboxIdentity.ForExecutable(path, []), ToolboxIdentity.ForExecutable(path, [""]));
        Assert.NotEqual(ToolboxIdentity.ForExecutable(path, [""]), ToolboxIdentity.ForExecutable(path, ["", ""]));
    }

    [Fact]
    public void 自訂程式參數保留刻意的前後空白()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, []);
        try
        {
            var service = new ToolboxService();
            var added = service.AddCustomTool(new ToolboxCustomTool
            {
                Name = "spaced args", Kind = ToolKind.CustomApp, Target = path, Arguments = ["  value  ", ""],
            });
            Assert.Equal(new[] { "  value  ", "" }, added.Item.LaunchArguments);
        }
        finally { File.Delete(path); }
    }
}
