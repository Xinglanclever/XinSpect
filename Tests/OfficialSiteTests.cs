using System;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 官方網站位址的守門：**一個位址，四個出口，全部同一個字串**。
/// </summary>
/// <remarks>
/// <para>
/// 這個測驗存在的理由很具體：官網位址會出現在四個地方（<see cref="AppInfo.OfficialSiteUrl"/>、
/// 關於頁的按鈕、內建瀏覽器起始頁的卡片、三份 README 的最上面）。散落寫死時，
/// 換網域只會改到其中幾處，而**漏掉的那一處不會紅燈**——它只是一個點不開的連結。
/// </para>
/// <para>
/// 這一組刻意讀「檔案內容」而不是驗行為：使用者按下去會發生什麼，測不了；
/// 但「這四處寫的是不是同一個 https 位址」完全測得了，而那正是會壞的部分。
/// </para>
/// </remarks>
public class OfficialSiteTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    [Fact]
    public void 位址是https而且沒有結尾斜線()
    {
        Assert.StartsWith("https://", AppInfo.OfficialSiteUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", AppInfo.OfficialSiteUrl, StringComparison.Ordinal);
        Assert.False(AppInfo.OfficialSiteUrl.EndsWith('/'), "位址結尾不帶斜線，否則字串比對會出現兩種形狀");
        // 顯示形狀就是去掉協定；兩者必須同步，否則按鈕旁的說明會指到別的地方。
        Assert.Equal(AppInfo.OfficialSiteUrl.Replace("https://", "", StringComparison.Ordinal),
            AppInfo.OfficialSiteDisplay);
        Assert.DoesNotContain("https://", AppInfo.OfficialSiteDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void 關於頁的按鈕指向AppInfo而不是寫死字串()
    {
        string xaml = Read(Path.Combine("Views", "AboutView.xaml"));
        Assert.Contains("x:Name=\"OpenSiteBtn\"", xaml);
        Assert.Contains("Click=\"OpenSite_Click\"", xaml);
        // XAML 裡不得出現完整位址：寫死之後改 AppInfo 不會生效，而且沒有任何守門會發現。
        Assert.DoesNotContain(AppInfo.OfficialSiteUrl, xaml);

        string code = Read(Path.Combine("Views", "AboutView.xaml.cs"));
        Assert.Contains("AppInfo.OfficialSiteUrl", code);
        Assert.Contains("OpenSite_Click", code);
        // 按鈕旁邊要顯示位址：按鈕本身不透露要去哪裡。
        Assert.Contains("OfficialSiteText.Text = AppInfo.OfficialSiteDisplay", code);
    }

    [Fact]
    public void 瀏覽器起始頁有官網卡片而且在第一格()
    {
        string html = BrowserHome.Html;
        Assert.Contains($"href=\"{AppInfo.OfficialSiteUrl}\"", html);

        // 放在 .grid 的第一張卡：起始頁的目的是「先給人看產品自己是什麼」，
        // 而不是再一次把使用者的注意力交給第三方工具站。
        int grid = html.IndexOf("<div class=\"grid\">", StringComparison.Ordinal);
        int site = html.IndexOf("card card-site", StringComparison.Ordinal);
        int firstOther = html.IndexOf("<h3>硬體檢測</h3>", StringComparison.Ordinal);
        Assert.True(grid >= 0 && site > grid, "官網卡片要在 .grid 裡面");
        Assert.True(site < firstOther, "官網卡片要在其他卡片之前");
    }

    [Fact]
    public void 三份README的最上面都有官網連結()
    {
        foreach (string file in new[] { "README.md", "README.zh-CN.md", "README.en.md" })
        {
            string text = Read(file);
            Assert.Contains($"({AppInfo.OfficialSiteUrl})", text);

            // 「最上面」是可檢查的：前 10 行內要出現。塞在第 300 行等於沒有。
            string head = string.Join("\n", text.Split('\n').Take(10));
            Assert.True(head.Contains(AppInfo.OfficialSiteUrl, StringComparison.Ordinal),
                $"{file}：官網連結不在前 10 行（讀者要自己往下找就失去「醒目」的意義）");
        }
    }

    [Fact]
    public void 三份README對官網的說明都要寫不掃描()
    {
        // 網站是說明頁，不執行任何本機動作——這件事必須在讀者點進去之前就講清楚，
        // 否則「官方網站」四個字本身會讓人以為要安裝什麼。
        foreach (string file in new[] { "README.md", "README.zh-CN.md", "README.en.md" })
        {
            string head = string.Join("\n", Read(file).Split('\n').Take(12));
            Assert.True(head.Contains("不會掃描你的電腦", StringComparison.Ordinal)
                        || head.Contains("不会扫描你的电脑", StringComparison.Ordinal)
                        || head.Contains("does not scan your machine", StringComparison.Ordinal),
                $"{file}：官網那段沒有寫「不會掃描你的電腦」——那是「網站」與「程式」的分界");
        }
    }

    [Fact]
    public void 位址不含任何憑證或查詢參數()
    {
        // 官網是公開說明頁。位址裡不該有 token、uid、utm 之類會追蹤或過期的東西。
        string url = AppInfo.OfficialSiteUrl;
        Assert.DoesNotContain("?", url, StringComparison.Ordinal);
        Assert.DoesNotContain("#", url, StringComparison.Ordinal);
        Assert.DoesNotContain("@", url, StringComparison.Ordinal);
        Assert.DoesNotContain("utm_", url, StringComparison.Ordinal);
    }
}
