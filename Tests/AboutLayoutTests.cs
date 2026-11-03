using System;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>關於頁的區塊順序與主介面的迷你 HUD 入口：文件說的位置就是實際的位置。</summary>
public class AboutLayoutTests
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
    public void 關於頁的區塊順序_第三方授權_意見反饋_留言建議_鍵盤快捷鍵()
    {
        string xaml = RepoFile(Path.Combine("Views", "AboutView.xaml"));
        int licenses = xaml.IndexOf("Text=\"第三方授權\"", StringComparison.Ordinal);
        int feedback = xaml.IndexOf("Text=\"意見反饋 ・ 聯絡我們\"", StringComparison.Ordinal);
        int message = xaml.IndexOf("Text=\"留言建議\"", StringComparison.Ordinal);
        int shortcuts = xaml.IndexOf("Text=\"鍵盤快捷鍵\"", StringComparison.Ordinal);

        Assert.True(licenses >= 0 && feedback > licenses && message > feedback && shortcuts > message,
            $"關於頁區塊順序不對：第三方授權={licenses}、意見反饋={feedback}、留言建議={message}、鍵盤快捷鍵={shortcuts}");
    }

    [Fact]
    public void 主介面有醒目的迷你HUD喚醒入口_在匯出報告旁邊()
    {
        string xaml = RepoFile("MainWindow.xaml");
        int mini = xaml.IndexOf("x:Name=\"MiniButton\"", StringComparison.Ordinal);
        int export = xaml.IndexOf("x:Name=\"ExportButton\"", StringComparison.Ordinal);

        Assert.True(mini >= 0, "MainWindow.xaml 沒有 MiniButton");
        Assert.True(export > mini, "迷你 HUD 按鈕應該在匯出報告旁邊（之前）");
        Assert.Contains("迷你 HUD", xaml, StringComparison.Ordinal);
        Assert.Contains("重新喚醒", xaml, StringComparison.Ordinal);
    }
}