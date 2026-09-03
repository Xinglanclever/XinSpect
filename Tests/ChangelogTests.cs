using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 版本更新紀錄（<see cref="ChangelogCatalog"/>）的靜態檢查。
///
/// 這一份存在的理由很單純：使用者要求「以後每次更新都要更新這個」。靠記性守不住，
/// 所以把規矩寫成測試——改了版號卻沒補紀錄，或三處版號沒同步，這裡就會紅。
/// </summary>
public class ChangelogTests
{
    /// <summary>Git 紀錄裡真的存在過的版號（`git log -p XinSpect.csproj` 可證），一個都不能漏。</summary>
    private static readonly string[] Versioned =
    [
        "1.9.1", "1.9.0", "1.8.2", "1.8.1", "1.8.0", "1.7.7", "1.7.6", "1.7.5", "1.7.4", "1.7.3", "1.7.2", "1.7.1", "1.6.2", "1.6.1", "1.6.0",
        "1.5.1", "1.5.0", "1.4.0", "1.3.2", "1.3.1", "1.3.0", "1.2.0",
    ];

    /// <summary>倉庫建立前的四個階段，使用者指定要從這裡開始寫。</summary>
    private static readonly string[] Stages = ["1.1", "1.0", "Release candidate", "Beta"];

    // ── 這條是重點：漏寫紀錄就紅 ──────────────────────────────────────────

    /// <summary>
    /// 最新一筆的版本必須等於 <c>XinSpect.csproj</c> 的 <c>&lt;Version&gt;</c>。
    /// 改版號卻忘了補紀錄，就是在這裡被抓。
    /// </summary>
    [Fact]
    public void 最新一筆必須等於專案版號()
    {
        Assert.Equal(CsprojVersion(), ChangelogCatalog.Latest);
    }

    /// <summary>
    /// 版號一共寫在三個地方（csproj、README 徽章、關於頁），發版時最容易漏掉其中一處；
    /// 既然已經有一份紀錄要對，就順手把三處一起釘住。
    /// </summary>
    [Fact]
    public void 三處版號與紀錄一致()
    {
        string v = ChangelogCatalog.Latest;
        Assert.Contains($"version-{v}-", ReadRepoFile("README.md"));
        Assert.Contains($"版本 {v} ・", ReadRepoFile(Path.Combine("Views", "AboutView.xaml")));
    }

    // ── 內容本身要站得住 ──────────────────────────────────────────────────

    [Fact]
    public void 每一筆都說得出版本標題與內容()
    {
        foreach (var e in ChangelogCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Version), "版本欄不得留空");
            Assert.False(string.IsNullOrWhiteSpace(e.Title), $"{e.Version}：缺少一句話總結");
            Assert.NotEmpty(e.Items);
            Assert.DoesNotContain(e.Items, i => string.IsNullOrWhiteSpace(i));
            // 版面上是一整段文字，每一項前面都要有項目符號才讀得出是幾件事
            Assert.Equal(e.Items.Count, e.ItemsText.Split('\n').Count(l => l.StartsWith('・')));
        }
    }

    /// <summary>
    /// 有編號的版本要有真實日期；倉庫建立前的階段沒有可靠日期，就必須留空並如實顯示，
    /// 不准填一個看起來合理的日期。
    /// </summary>
    [Fact]
    public void 日期要嘛是真的要嘛承認沒有()
    {
        foreach (var e in ChangelogCatalog.Entries)
        {
            if (e.BeforeRepo)
            {
                Assert.Equal("", e.Date);
                Assert.Equal("日期未留下紀錄", e.DateText);
            }
            else
            {
                Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", e.Date);
                Assert.Equal(e.Date, e.DateText);
            }
        }
    }

    [Fact]
    public void 版號不重複()
    {
        var all = ChangelogCatalog.Entries.Select(e => e.Version).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    /// <summary>有編號的版本一個都不能漏，階段名也要在。</summary>
    [Fact]
    public void 每個真的存在過的版本都有一筆()
    {
        var have = ChangelogCatalog.Entries.Select(e => e.Version).ToHashSet();
        foreach (string v in Versioned) Assert.Contains(v, have);
        foreach (string s in Stages) Assert.Contains(s, have);
    }

    // ── 讀原始碼樹 ────────────────────────────────────────────────────────

    /// <summary>
    /// 從 <c>XinSpect.csproj</c> 讀出 <c>&lt;Version&gt;</c>。刻意不讀組件版本：要驗的正是
    /// 「專案檔的版號改了，紀錄有沒有跟上」，讀組件版本就變成拿同一個來源自己對自己。
    /// </summary>
    private static string CsprojVersion()
    {
        var m = Regex.Match(ReadRepoFile("XinSpect.csproj"), @"<Version>([^<]+)</Version>");
        Assert.True(m.Success, "XinSpect.csproj 裡找不到 <Version>");
        return m.Groups[1].Value.Trim();
    }

    /// <summary>讀原始碼樹裡的檔案（路徑相對於倉庫根目錄）。從測試輸出目錄往上找 csproj。</summary>
    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
                return File.ReadAllText(Path.Combine(dir.FullName, relativePath));
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到原始碼樹（往上找不到 XinSpect.csproj）");
    }
}
