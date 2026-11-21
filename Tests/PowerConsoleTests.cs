using System;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>強控台（Power Console）入口與誠信守門：入口醒目、未接的 SECTION 不得放假資料。</summary>
public class PowerConsoleTests
{
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
                return File.ReadAllText(Path.Combine(dir.FullName, relative));
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到原始碼樹（往上找不到 XinSpect.csproj）");
    }

    [Fact]
    public void 主介面不再提供強控台按鈕()
    {
        string xaml = RepoFile("MainWindow.xaml");
        string src = RepoFile("MainWindow.xaml.cs");
        Assert.DoesNotContain("ConsoleButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Console_Click", src, StringComparison.Ordinal);
        Assert.DoesNotContain("FindXsc", src, StringComparison.Ordinal);
    }

    [Fact]
    public void 強控台支援指令行與鍵盤操作()
    {
        string xaml = RepoFile(Path.Combine("Views", "PowerConsoleWindow.xaml"));
        string src = RepoFile(Path.Combine("Views", "PowerConsoleWindow.xaml.cs"));

        // 命令列與歷史
        Assert.Contains("x:Name=\"CmdBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CmdOutput\"", xaml, StringComparison.Ordinal);
        // 鍵盤導航：方向鍵、Enter、Ctrl+1–5、Esc
        Assert.Contains("Window_PreviewKeyDown", xaml, StringComparison.Ordinal);
        Assert.Contains("Key.Escape", src, StringComparison.Ordinal);
        Assert.Contains("Key.D1", src, StringComparison.Ordinal);
        Assert.Contains("Key.Up", src, StringComparison.Ordinal);
        Assert.Contains("Key.Down", src, StringComparison.Ordinal);
        Assert.Contains("Key.Return", src, StringComparison.Ordinal);
        // 指令集
        foreach (string cmd in new[] { "help", "pci scan", "msr", "ipc ", "hash", "theme", "clear", "exit" })
            Assert.Contains(cmd, src, StringComparison.Ordinal);
    }

    [Fact]
    public void 強控台視窗存在且有五個SECTION()
    {
        string xaml = RepoFile(Path.Combine("Views", "PowerConsoleWindow.xaml"));
        foreach (string name in new[] { "x:Name=\"S1\"", "x:Name=\"S2\"", "x:Name=\"S3\"", "x:Name=\"S4\"", "x:Name=\"S5\"" })
            Assert.Contains(name, xaml, StringComparison.Ordinal);
        // 入口與視窗都要講「唯讀」：這個視窗不寫任何硬體暫存器
        Assert.Contains("唯讀", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// CLI remains a standalone app; no desktop toolbar bridge is required.
    /// </summary>
    [Fact]
    public void StandaloneCliRemainsSeparateFromTheDesktopToolbar()
    {
        string src = RepoFile("MainWindow.xaml.cs");
        string cliProject = RepoFile(Path.Combine("XinSpectCli", "XinSpectCli.csproj"));
        Assert.DoesNotContain("Console_Click", src, StringComparison.Ordinal);
        Assert.Contains("OutputType", cliProject, StringComparison.Ordinal);
    }

    [Fact]
    public void Ring0通路只有讀_不引用任何Write()
    {
        // 強控台的 Ring0 服務不得呼叫寫入通路：守這條等於守「強控台唯讀」的承諾。
        string src = RepoFile(Path.Combine("Services", "PowerConsoleRing0Service.cs"));
        Assert.DoesNotContain("WritePciConfig", src, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteMsrPair", src, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteIoPort", src, StringComparison.Ordinal);
        Assert.Contains("ReadPciConfig", src, StringComparison.Ordinal);
        Assert.Contains("ReadMsrPair64", src, StringComparison.Ordinal);
    }
}
