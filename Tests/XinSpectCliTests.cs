using System;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// xsc（XinSpect 終端機）的守門：獨立專案要留在版控、主專案的 glob 不得把它吃回去。
/// </summary>
/// <remarks>
/// 2026-11-04 的實際事故：XinSpectCli/ 是主專案目錄的子目錄，主 csproj 的預設
/// `**/*.cs` glob 把 Program.cs 編進主程式，兩個 Main 直接 CS0017。
/// 修法與 Bridge/ 相同——主 csproj 明確 Remove；這裡守住那個 Remove 不被拿掉。
/// </remarks>
public class XinSpectCliTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void 主專案必須排除XinSpectCli的原始碼()
    {
        string csproj = File.ReadAllText(Path.Combine(RepoRoot(), "XinSpect.csproj"));
        Assert.Contains("Compile Remove=\"XinSpectCli\\**\\*.cs\"", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void CLI專案與進入點存在()
    {
        string root = RepoRoot();
        Assert.True(File.Exists(Path.Combine(root, "XinSpectCli", "XinSpectCli.csproj")), "缺 XinSpectCli.csproj");
        string src = File.ReadAllText(Path.Combine(root, "XinSpectCli", "Program.cs"));
        // Claude Code 式體驗的最低門檻：互動迴圈＋歷史＋補全＋唯讀承諾
        Assert.Contains("ReadLineWithHistory", src, StringComparison.Ordinal);
        Assert.Contains("ConsoleKey.Tab", src, StringComparison.Ordinal);
        Assert.Contains("ConsoleKey.UpArrow", src, StringComparison.Ordinal);
        Assert.Contains("唯讀", src, StringComparison.Ordinal);
        // 不得引入寫入通路
        Assert.DoesNotContain("WritePciConfig", src, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteMsrPair", src, StringComparison.Ordinal);
    }
}
