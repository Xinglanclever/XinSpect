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
    public void 主介面有強控台入口_緊鄰匯出報告()
    {
        string xaml = RepoFile("MainWindow.xaml");
        int console = xaml.IndexOf("x:Name=\"ConsoleButton\"", StringComparison.Ordinal);
        int mini = xaml.IndexOf("x:Name=\"MiniButton\"", StringComparison.Ordinal);
        int export = xaml.IndexOf("x:Name=\"ExportButton\"", StringComparison.Ordinal);

        Assert.True(console >= 0, "MainWindow.xaml 沒有強控台入口（ConsoleButton）");
        // 使用者裁決：強控台在「匯出報告」旁邊（前一格）；迷你 HUD 再往前。
        Assert.True(mini >= 0 && mini < console && console < export,
            $"按鈕順序不對：迷你HUD={mini}、強控台={console}、匯出報告={export}");
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
    /// 3.17：主介面強控台按鈕改為橋接 xsc.exe（同一事實引擎的終端視角）。
    /// 守門：主程式必須找得到 xsc（找不到要跳如實提示，不得靜默），
    /// 且不得在主程式內自己 new 內嵌視窗——橋接對象仍守唯讀承諾。
    /// </summary>
    [Fact]
    public void 強控台入口橋接xsc_找不到時如實提示()
    {
        string src = RepoFile("MainWindow.xaml.cs");
        Assert.Contains("FindXsc", src, StringComparison.Ordinal);
        Assert.Contains("找不到 xsc.exe", src, StringComparison.Ordinal);
        // 橋接不能退化成內嵌另一份 GUI：PowerConsoleWindow 視窗檔保留供歷史審閱，但主介面不再 new 它
        Assert.DoesNotContain("new PowerConsoleWindow()", src, StringComparison.Ordinal);
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
