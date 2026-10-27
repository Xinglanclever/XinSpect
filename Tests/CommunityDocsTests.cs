using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 社群與貢獻文件的「現在式數字」守門。
/// </summary>
/// <remarks>
/// <para>
/// 這批文件（SECURITY／CONTRIBUTING／SUPPORT／CODE_OF_CONDUCT、Issue 與 PR 範本、CI 工作流程）
/// 有一個共同的壞習慣：它們會寫「目前 2.9x」「全套 6,9xx 條」這種<b>現在式</b>的數字。
/// 這種數字沒有人守就一定會舊——本專案已經踩過兩次同樣的坑（README 徽章停在 3195、
/// 關於頁版號落後三十幾版），原因都是「寫進去的當下是對的，之後沒有東西在數它」。
/// </para>
/// <para>
/// 所以這裡不重複寫死數字，而是把每個數字<b>釘回它唯一的來源</b>：
/// 版號釘 <see cref="ChangelogCatalog.Latest"/>、測試數釘 <see cref="TestSuiteBaseline.ProjectTests"/>。
/// 兩者都有各自的守門在數，這裡只負責「文件不得落後」。
/// </para>
/// <para>
/// 另外守一件事：<c>ci.yml</c> 的過濾字串不能<b>靜默地什麼都沒跑到</b>。
/// 一個打錯字的 <c>FullyQualifiedName~</c> 會讓 CI 綠得很漂亮，而它一個測試都沒跑——
/// 那正是「通過」與「沒有檢查」長得一樣的那種缺陷。
/// </para>
/// </remarks>
public class CommunityDocsTests
{
    // ── 版號：文件裡寫的「目前版本」必須等於紀錄的最新一筆 ────────────────────

    [Fact]
    public void 安全性政策的最新發佈版要等於當前版本()
    {
        string text = ReadRepoFile("SECURITY.md");
        var m = Regex.Match(text, @"最新發佈版（目前 ([\d.]+)）");
        Assert.True(m.Success, "SECURITY.md 找不到「最新發佈版（目前 X.Y）」這一行——措辭改了就要一起改守門");
        Assert.Equal(ChangelogCatalog.Latest, m.Groups[1].Value);
    }

    [Fact]
    public void Issue範本的版號提示要等於當前版本()
    {
        foreach (string file in new[]
        {
            Path.Combine(".github", "ISSUE_TEMPLATE", "bug_report.yml"),
            Path.Combine(".github", "ISSUE_TEMPLATE", "hardware_report.yml"),
        })
        {
            string text = ReadRepoFile(file);
            var placeholders = Regex.Matches(text, @"placeholder: ""([\d.]+)""").Cast<Match>().ToList();
            Assert.True(placeholders.Count > 0, $"{file}：找不到版本 placeholder");
            foreach (var p in placeholders)
                Assert.True(p.Groups[1].Value == ChangelogCatalog.Latest,
                    $"{file}：placeholder 寫 {p.Groups[1].Value}，但當前版本是 {ChangelogCatalog.Latest}");
        }
    }

    [Fact]
    public void Bug回報範本的操作步驟版號要等於當前版本()
    {
        string text = ReadRepoFile(Path.Combine(".github", "ISSUE_TEMPLATE", "bug_report.yml"));
        var m = Regex.Match(text, @"以系統管理員啟動 ([\d.]+)");
        Assert.True(m.Success, "bug_report.yml 找不到「以系統管理員啟動 X.Y」步驟");
        Assert.Equal(ChangelogCatalog.Latest, m.Groups[1].Value);
    }

    // ── 測試數：文件裡寫的條數必須等於基線 ────────────────────────────────

    [Fact]
    public void 貢獻指南與PR範本的測試條數要等於基線()
    {
        string n = TestSuiteBaseline.ProjectTests.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

        string contributing = ReadRepoFile("CONTRIBUTING.md");
        // 允許「vX.YY 為 6,9xx 條」這種寫法：抓「為 <數字> 條」
        var cm = Regex.Match(contributing, @"為 ([\d,]+) 條");
        Assert.True(cm.Success, "CONTRIBUTING.md 找不到「為 X,XXX 條」的測試數");
        Assert.True(cm.Groups[1].Value == n,
            $"CONTRIBUTING.md 寫 {cm.Groups[1].Value} 條，但基線是 {n} 條（TestSuiteBaseline）");

        string pr = ReadRepoFile(Path.Combine(".github", "PULL_REQUEST_TEMPLATE.md"));
        var pm = Regex.Match(pr, @"本機實測：([\d,]+) 條");
        Assert.True(pm.Success, "PR 範本找不到「本機實測：X,XXX 條」");
        Assert.True(pm.Groups[1].Value == n,
            $"PR 範本寫 {pm.Groups[1].Value} 條，但基線是 {n} 條");
    }

    [Fact]
    public void CI工作流程的測試條數註解要等於基線()
    {
        string n = TestSuiteBaseline.ProjectTests.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        string text = ReadRepoFile(Path.Combine(".github", "workflows", "ci.yml"));
        var m = Regex.Match(text, @"全套 ([\d,]+) 條");
        Assert.True(m.Success, "ci.yml 找不到「全套 X,XXX 條」的註解");
        Assert.True(m.Groups[1].Value == n,
            $"ci.yml 寫 {m.Groups[1].Value} 條，但基線是 {n} 條");
    }

    // ── 真守門：CI 的過濾字串不得靜默地什麼都沒跑到 ─────────────────────────

    [Fact]
    public void CI守門過濾字串的每一個類名都要真的存在()
    {
        string text = ReadRepoFile(Path.Combine(".github", "workflows", "ci.yml"));
        var names = Regex.Matches(text, @"FullyQualifiedName~([A-Za-z0-9_]+)")
            .Cast<Match>().Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

        Assert.True(names.Count >= 3, $"ci.yml 只找到 {names.Count} 個過濾類名——過濾字串可能被改壞了");

        var types = typeof(CommunityDocsTests).Assembly.GetTypes()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = names.Where(n => !types.Contains(n)).ToList();
        Assert.True(missing.Count == 0,
            "ci.yml 的過濾字串指向不存在的測試類：" + string.Join("、", missing)
            + "——這會讓 CI 綠得漂亮，而它一個測試都沒跑（「通過」與「沒有檢查」長得一樣）");
    }

    /// <summary>
    /// 三份 README 是同一個產品對三種讀者的說法，但過去只改一份的情況真的發生過：
    /// AI 的送出前遮蔽與金鑰 DPAPI 加密、瀏覽器只開 http(s) 與下載位置，這些**安全界線**
    /// 最先只出現在正體版，另外兩份還停在舊說法（「金鑰存在本機」就是一句在加密實作之後
    /// 才變成不完整的宣稱）。這裡不驗句子怎麼寫，只驗同一組硬事實三份都在。
    /// </summary>
    [Fact]
    public void 三份README對AI與瀏覽器的界線要寫到同一組事實()
    {
        string[] facts = ["DPAPI", "javascript:", @"XinSpect\Downloads", "Retry-After"];
        var missing = new List<string>();

        foreach (string file in new[] { "README.md", "README.zh-CN.md", "README.en.md" })
        {
            string text = ReadRepoFile(file);
            foreach (string fact in facts)
                if (!text.Contains(fact, StringComparison.Ordinal))
                    missing.Add($"{file}：找不到「{fact}」——這一版的 AI／瀏覽器界線沒有寫進這一份");
        }

        Assert.True(missing.Count == 0,
            "三份 README 對同一組安全事實的說法不一致（改一份就要改三份）：\n" + string.Join("\n", missing));
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
        throw new InvalidOperationException("從測試輸出目錄往上找不到 XinSpect.csproj——讀不到倉庫根");
    }
}
