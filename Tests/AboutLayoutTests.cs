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
    public void 關於頁的區塊順序_意見反饋_留言建議_第三方授權()
    {
        // 3.15 之後的使用者裁決：第三方授權挪到留言建議下面；快捷鍵留在意見反饋區之後。
        string xaml = RepoFile(Path.Combine("Views", "AboutView.xaml"));
        int feedback = xaml.IndexOf("Text=\"意見反饋 ・ 聯絡我們\"", StringComparison.Ordinal);
        int message = xaml.IndexOf("Text=\"留言建議\"", StringComparison.Ordinal);
        int shortcuts = xaml.IndexOf("Text=\"鍵盤快捷鍵\"", StringComparison.Ordinal);
        int licenses = xaml.IndexOf("Text=\"第三方授權\"", StringComparison.Ordinal);
        int disclaimer = xaml.IndexOf("Text=\"免責聲明\"", StringComparison.Ordinal);

        Assert.True(feedback >= 0, "找不到意見反饋");
        Assert.True(feedback < message && message < shortcuts && shortcuts < licenses && licenses < disclaimer,
            $"關於頁區塊順序不對：意見反饋={feedback}、留言建議={message}、鍵盤快捷鍵={shortcuts}、"
            + $"第三方授權={licenses}、免責聲明={disclaimer}");
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