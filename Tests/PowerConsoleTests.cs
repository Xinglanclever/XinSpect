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
    public void 主介面有強控台入口_在迷你HUD旁邊()
    {
        string xaml = RepoFile("MainWindow.xaml");
        int console = xaml.IndexOf("x:Name=\"ConsoleButton\"", StringComparison.Ordinal);
        int mini = xaml.IndexOf("x:Name=\"MiniButton\"", StringComparison.Ordinal);
        int export = xaml.IndexOf("x:Name=\"ExportButton\"", StringComparison.Ordinal);

        Assert.True(console >= 0, "MainWindow.xaml 沒有強控台入口（ConsoleButton）");
        Assert.True(console < mini && mini < export, "強控台按鈕應在迷你 HUD 之前、匯出報告之前");
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
