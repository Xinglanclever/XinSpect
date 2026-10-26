using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// README 那兩張「可複算」的表，真的可複算：每一列的數字都由這棵樹當場數出來比對。
///
/// <para>
/// <b>為什麼要這一支：</b>README 自己寫著「數法是內容的一部分」，但那些數字一直是<b>手算後寫死</b>的
/// ——第 277 輪對帳時發現 zh-CN 與 en 兩份的統計表停在 v2.87 時代（事實鍵 1,044、家族 28、
/// 測試 6,580），整整三十幾輪沒人動。同一份文件裡，鍵數與測試數有守門、其他六個數字沒有，
/// 於是守門綠燈與文件說謊可以同時成立。<b>一份自己宣稱可複算、卻沒有機器複算的文件，
/// 會在三個版本之後變成願望清單。</b>
/// </para>
///
/// <para>
/// <b>數法必須與文件寫的一致，否則守門只是換一種說謊。</b>所以這一支不發明新數法：
/// 鍵數與家族數讀目錄的成員、頁數讀 <see cref="PageRegistry"/>、說明條目讀
/// <see cref="HelpCatalog.All"/>、版本筆數讀 <see cref="ChangelogCatalog.Entries"/>、
/// 測試基線讀 <see cref="TestSuiteBaseline.ProjectTests"/>；檔案數則照文件第三欄寫的走訪規則
/// （排除 <c>obj/</c>、<c>bin/</c>、<c>publish-*/</c>、<c>.git/</c>）。
/// </para>
///
/// <para>
/// <b>紅燈訊息會直接給出應該填的數字</b>——這一支的用途不是「證明我記得對」，是
/// 「改完程式碼之後告訴我文件要改成什麼」。
/// </para>
/// </summary>
public class ReadmeStatsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Services")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Readme() => File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));

    /// <summary>把表格區塊拆成（列名, 數值）——數值去掉粗體與千分位。</summary>
    private static Dictionary<string, string> Rows(string section, string endMarker)
    {
        string text = Readme();
        int start = text.IndexOf(section, StringComparison.Ordinal);
        Assert.True(start >= 0, $"README 找不到區段「{section}」");
        int end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"README 找不到「{section}」的結束標記「{endMarker}」");

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text[start..end], @"^\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|", RegexOptions.Multiline))
        {
            string label = m.Groups[1].Value.Trim();
            string value = m.Groups[2].Value.Trim().Replace("*", "").Replace(",", "");
            if (label is "類別" or "項目" || label.StartsWith("---", StringComparison.Ordinal)) continue;
            rows[label] = value;
        }
        return rows;
    }

    /// <summary>走訪所有 <c>*.cs</c>，規則與 README 第三欄寫的一致。</summary>
    private static List<string> AllCsFiles()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "obj", ".git", "node_modules", "TestResults" };
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(RepoRoot());
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            string name = Path.GetFileName(dir);
            if (excluded.Contains(name) || name.StartsWith("publish-", StringComparison.Ordinal)) continue;
            try
            {
                foreach (string sub in Directory.GetDirectories(dir)) pending.Push(sub);
                files.AddRange(Directory.GetFiles(dir, "*.cs"));
            }
            catch (UnauthorizedAccessException) { }
        }
        return files;
    }

    private static int TestFileCount()
        => Directory.GetFiles(Path.Combine(RepoRoot(), "Tests"), "*.cs", SearchOption.TopDirectoryOnly).Length;

    /// <summary>測試方法數（反射）：<c>[Fact]</c> 與 <c>[Theory]</c> 分開數——後者繼承自前者，不能混。</summary>
    private static (int Facts, int Theories) TestMethodCounts()
    {
        var methods = typeof(ReadmeStatsTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        int facts = 0, theories = 0;
        foreach (var m in methods)
        {
            var attrs = m.GetCustomAttributes<FactAttribute>(inherit: true).ToList();
            if (attrs.Count == 0) continue;
            if (attrs.Any(a => a.GetType() == typeof(TheoryAttribute))) theories++;
            else facts++;
        }
        return (facts, theories);
    }

    /// <summary>由事實鍵目錄算出的域統計（鍵數、域數、一鍵域／≤3 鍵域／≥20 鍵域）。</summary>
    private static (int Keys, int Domains, int OneKey, int UpToThree, int AtLeastTwenty) DomainStats()
    {
        var byDomain = FactKeyCatalog.Keys
            .GroupBy(k => k.Split('.')[0], StringComparer.Ordinal)
            .Select(g => g.Count())
            .ToList();
        return (FactKeyCatalog.Keys.Count, byDomain.Count,
            byDomain.Count(c => c == 1), byDomain.Count(c => c <= 3), byDomain.Count(c => c >= 20));
    }

    // ── 主表 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 規模表的每一個數字都要與這棵樹相符()
    {
        var rows = Rows("## 這棵樹的事實表（可複算）", "\n---");
        var d = DomainStats();
        var (facts, theories) = TestMethodCounts();

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["編譯期事實鍵"] = d.Keys.ToString(),
            ["事實域（鍵的第一段前綴）"] = d.Domains.ToString(),
            ["動態鍵家族"] = FactKeyDynamicCatalog.Families.Count.ToString(),
            ["一鍵域／≤3 鍵域／≥20 鍵域"] = $"{d.OneKey}／{d.UpToThree}／{d.AtLeastTwenty}",
            // 可到達畫面＝主頁面＋「實用工具」子頁兩份註冊表（README 的數法是「Key = 條目數」）。
            ["可到達畫面（`PageRegistry`）"] = (PageRegistry.Pages.Count + PageRegistry.Utilities.Count).ToString(),
            ["XAML 檔（含對話框與控制項）"] =
                Directory.GetFiles(Path.Combine(RepoRoot(), "Views"), "*.xaml").Length.ToString(),
            ["全站說明條目（圓圈問號）"] = HelpCatalog.All.Count.ToString(),
            ["服務檔"] = Directory.GetFiles(Path.Combine(RepoRoot(), "Services"), "*.cs",
                SearchOption.AllDirectories).Length.ToString(),
            ["全域 C# 檔"] = AllCsFiles().Count.ToString(),
            ["版本紀錄筆數"] = ChangelogCatalog.Entries.Count.ToString(),
            ["專案測試基線"] = TestSuiteBaseline.ProjectTests.ToString(),
        };

        var problems = new List<string>();
        foreach (var (label, want) in expected)
        {
            if (!rows.TryGetValue(label, out string? got))
            {
                problems.Add($"主表少了「{label}」這一列");
                continue;
            }
            if (got != want)
                problems.Add($"「{label}」寫 {got}，這棵樹現在是 {want}");
        }

        Assert.True(problems.Count == 0,
            "README 規模表與這棵樹不符（改完程式碼請一併更新文件；數字就是右邊那個）：\n"
            + string.Join("\n", problems));
    }

    // ── 第 10.1 節：測試的規模 ─────────────────────────────────────────────

    [Fact]
    public void 測試規模表的數字要與測試組件相符()
    {
        var rows = Rows("### 10.1 測試的規模（可複算）", "\n**兩個比例值得讀");
        var (facts, theories) = TestMethodCounts();

        var problems = new List<string>();
        void Check(string label, int want)
        {
            if (!rows.TryGetValue(label, out string? got)) { problems.Add($"少了「{label}」這一列"); return; }
            if (got != want.ToString()) problems.Add($"「{label}」寫 {got}，實際 {want}");
        }

        Check("測試檔", TestFileCount());
        Check("`[Fact]` 方法", facts);
        Check("`[Theory]` 方法", theories);
        Check("執行期測試案例", TestSuiteBaseline.ProjectTests);

        Assert.True(problems.Count == 0,
            "README 第 10.1 節與測試組件不符：\n" + string.Join("\n", problems));
    }

    [Fact]
    public void 敘述句裡的方法數要等於Fact加Theory()
    {
        // 這一條抓的是「表格改了、下面那句話沒改」——同一節裡兩個數字必須同源。
        var (facts, theories) = TestMethodCounts();
        string text = Readme();
        var m = Regex.Match(text, @"大於方法數（([\d,]+)）");
        Assert.True(m.Success, "README 第 10.1 節找不到「大於方法數（N）」這句話");
        int claimed = int.Parse(m.Groups[1].Value.Replace(",", ""));
        Assert.True(claimed == facts + theories,
            $"敘述句寫方法數 {claimed}，實際 [Fact] {facts}＋[Theory] {theories}＝{facts + theories}");
    }

    // ── 兩份譯本的統計表（第 277 輪補過，這裡釘住不再漂） ──────────────────

    [Fact]
    public void 簡體版的統計表四個單一來源數字要與目錄一致()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "README.zh-CN.md"));
        var d = DomainStats();

        Assert.Contains($"| 事实键目录 | {d.Keys:N0} 键／{d.Domains} 个前缀 |", text);
        Assert.Contains($"| 动态键家族 | {FactKeyDynamicCatalog.Families.Count} 个 |", text);
        Assert.Contains($"| 运行期测试案例 | {TestSuiteBaseline.ProjectTests:N0}（", text);
    }

    [Fact]
    public void 英文版的敘述句要用本版鍵數()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "README.en.md"));
        int keys = FactKeyCatalog.Keys.Count;

        // 這三句是「現在式」宣稱：目錄有幾把鍵、覆蓋面是幾把。
        Assert.Contains($"Of {keys:N0} catalogue keys", text);
        Assert.Contains($"catalogue ({keys:N0} keys)", text);
        Assert.Contains($"All {keys:N0} keys", text);
    }
}
