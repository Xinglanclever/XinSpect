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

    /// <summary>由事實鍵目錄算出的域統計（鍵數、域數、一鍵域／≤3 鍵域／≥20 鍵域）與服務檔數。</summary>
    private static (int Keys, int Domains, int OneKey, int UpToThree, int AtLeastTwenty, int Services) DomainStats()
    {
        var byDomain = FactKeyCatalog.Keys
            .GroupBy(k => k.Split('.')[0], StringComparer.Ordinal)
            .Select(g => g.Count())
            .ToList();
        return (FactKeyCatalog.Keys.Count, byDomain.Count,
            byDomain.Count(c => c == 1), byDomain.Count(c => c <= 3), byDomain.Count(c => c >= 20),
            Directory.GetFiles(Path.Combine(RepoRoot(), "Services"), "*.cs", SearchOption.AllDirectories).Length);
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

    // ── 散落在正文裡的現在式數字：第 318 輪補 ─────────────────────────────
    //
    // 為什麼要補這一組：第 318 輪對帳時，規模表（第一節那張）全綠，而正文裡**同一批數字**
    // 停在三十幾輪前——域數寫 150（實況 151）、動態家族寫 49（實況 59）、服務檔寫 618（實況 639）、
    // 測試寫 7,040（實況 7,467）、說明條目寫 286（實況 285，還比實況多）、畫面寫 64（實況 63）。
    // 守門只看表格，於是「表格是真的、正文是舊的」可以同時成立——一份文件裡兩個答案，
    // 讀者無從判斷哪一個算數。這一組把正文那幾處也接上同一個來源。

    /// <summary>可到達畫面數（主頁面＋實用工具兩份註冊表）。</summary>
    private static int ReachablePages() => PageRegistry.Pages.Count + PageRegistry.Utilities.Count;

    [Fact]
    public void 零點八節的十條複算算式要用本版數字()
    {
        string text = Readme();
        var d = DomainStats();
        var problems = new List<string>();

        // 只抓「數字＋單位」那一段，不整行比對：算式那幾句的說明文字會改，
        // 但括號裡的「本版 N」是現在式宣稱，必須與這棵樹同源。
        void Check(string fragment)
        {
            if (!text.Contains(fragment, StringComparison.Ordinal))
                problems.Add($"找不到：「{fragment}」");
        }

        Check($"去重（本版 {d.Domains}）。");
        Check($"的成員（本版 {FactKeyDynamicCatalog.Families.Count}）。");
        Check($"`Key =` 條目（本版 {ReachablePages()}）。");
        Check($"`Entries` 成員（本版 {HelpCatalog.All.Count}）。");
        Check($"wc -l`（本版 {d.Services}）。");
        Check($"`Tests/TestSuiteBaseline.cs`（本版 {TestSuiteBaseline.ProjectTests:N0}）。");

        Assert.True(problems.Count == 0,
            "README 第 0.8 節的複算算式與這棵樹不符（這些數字與規模表同源，必須一起改）：\n"
            + string.Join("\n", problems));
    }

    [Fact]
    public void 服務層章節的三處服務檔數要等於實況()
    {
        // 第 2.2 節同一個數字出現三次（「底下有 N 個」「為什麼是 N 而不是 60」「N 個服務檔意味著」）。
        // 只改一處是最常見的漏法：句子讀起來還通，數字卻自相矛盾。
        string text = Readme();
        int services = Directory.GetFiles(Path.Combine(RepoRoot(), "Services"), "*.cs",
            SearchOption.AllDirectories).Length;

        Assert.Contains($"`Services/` 底下有 **{services}** 個 `.cs` 檔", text);
        Assert.Contains($"**為什麼服務數量是 {services} 而不是 60。**", text);
        Assert.Contains($"**服務數量本身就是一種風險。** {services} 個服務檔意味著", text);
        Assert.Contains($"2. **服務數量本身就是一種風險。** {services} 個服務檔意味著", text);
    }

    [Fact]
    public void 二點五節的域統計要與目錄相符()
    {
        // 這一句把四個數字串在一起（域數、一鍵域、≤3 鍵域、≥20 鍵域），還把 12 個大域逐一列出。
        // 其中任何一個變了而句子沒改，讀者看到的是一份「看起來很精確」的舊帳。
        string text = Readme();
        var d = DomainStats();

        var byDomain = FactKeyCatalog.Keys
            .GroupBy(k => k.Split('.')[0], StringComparer.Ordinal)
            .Select(g => (Domain: g.Key, Count: g.Count()))
            .Where(x => x.Count >= 20)
            .OrderByDescending(x => x.Count).ThenBy(x => x.Domain, StringComparer.Ordinal)
            .ToList();
        string listed = string.Join("、", byDomain.Select(x => $"`{x.Domain}` {x.Count}"));

        Assert.Contains($"共 **{d.Domains}** 個域；其中 {d.OneKey} 個域只有一把鍵、"
                        + $"{d.UpToThree} 個域不超過三把、{d.AtLeastTwenty} 個域有 20 把以上（{listed}）", text);
    }

    [Fact]
    public void 動態家族章節的標題與數字要等於登記數()
    {
        string text = Readme();
        int families = FactKeyDynamicCatalog.Families.Count;

        Assert.Contains($"### 2.6 動態家族：{families} 個「我列不完」的申報", text);
        Assert.Contains($"登記 **{families}** 個動態前綴", text);
        Assert.Contains($"{FactKeyCatalog.Keys.Count:N0} 個鍵加上 {families} 個動態家族", text);
    }

    [Fact]
    public void 第六點二節的域清單要等於鍵數二十把以上的域()
    {
        // 「最大的 N 個域」不是編輯挑的，是**算出來的**：鍵數 ≥20。
        // 第 318 輪之前這一段停在「十一個」——`os` 域在第 309 輪長到 23 把之後就該進來，
        // 但沒有任何東西會叫。這一條從目錄重算門檻、清單與合計，三者一起釘住。
        string text = Readme();
        var big = FactKeyCatalog.Keys
            .GroupBy(k => k.Split('.')[0], StringComparer.Ordinal)
            .Select(g => (Domain: g.Key, Count: g.Count()))
            .Where(x => x.Count >= 20)
            .OrderByDescending(x => x.Count).ThenBy(x => x.Domain, StringComparer.Ordinal)
            .ToList();
        int total = big.Sum(x => x.Count);

        Assert.Contains($"### 6.2 最大的{ToChinese(big.Count)}個域：逐域判讀要點", text);
        Assert.Contains($"  - [6.2 最大的{ToChinese(big.Count)}個域：逐域判讀要點]", text);
        Assert.Contains($"它們合計 {total} 把鍵", text);

        // 每一域的標題：`#### 6.2.N \`域\` — 說明（N 把）`，編號連續、計數與目錄相符。
        var heads = System.Text.RegularExpressions.Regex.Matches(text,
                @"^#### 6\.2\.(\d+) `([^`]+)` — [^（]*（(\d+) 把）$", RegexOptions.Multiline)
            .Select(m => (Number: int.Parse(m.Groups[1].Value), Domain: m.Groups[2].Value,
                          Count: int.Parse(m.Groups[3].Value)))
            .ToList();

        Assert.Equal(big.Count, heads.Count);
        for (int i = 0; i < heads.Count; i++)
        {
            Assert.Equal(i + 1, heads[i].Number);
            Assert.Equal(big[i].Domain, heads[i].Domain);
            Assert.Equal(big[i].Count, heads[i].Count);
        }
    }

    /// <summary>12 → 「十二」（README 的標題用中文數字，最多到二十）。</summary>
    private static string ToChinese(int n) => n switch
    {
        < 10 => "一二三四五六七八九"[n - 1].ToString(),
        10 => "十",
        < 20 => "十" + "一二三四五六七八九"[n - 11],
        20 => "二十",
        _ => n.ToString(),
    };

    [Fact]
    public void 附錄H的列集合與標題計數要等於Services的實況()
    {
        // 附錄 H 是「找到負責某件事的檔案」的地圖。第 318 輪之前它是一次性腳本寫的：
        // 標題寫 618、表上 615 列、磁碟上 639 個檔，其中還有 1 列指向已刪除的檔案。
        // 三個數字互相矛盾，而沒有守門看著——地圖缺一頁，比沒有地圖更難查。
        string text = Readme();
        int i = text.IndexOf("### 附錄 H", StringComparison.Ordinal);
        int j = text.IndexOf("### 附錄 I", i, StringComparison.Ordinal);
        Assert.True(i > 0 && j > i, "README 找不到附錄 H 區段");
        string section = text[i..j];

        var files = Directory.GetFiles(Path.Combine(RepoRoot(), "Services"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.Contains($"### 附錄 H　服務檔地圖（{files.Count} 個檔案）", section);
        Assert.Contains($"  - [附錄 H　服務檔地圖（{files.Count} 個檔案）]"
                        + $"(#附錄-h-服務檔地圖{files.Count}-個檔案)", text);

        var rows = Regex.Matches(section, @"^\| `([^`]+)` \| \d+ \| \d+ \|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(rows.Count, rows.Distinct(StringComparer.Ordinal).Count());

        var missing = files.Except(rows, StringComparer.Ordinal).ToList();
        var extra = rows.Except(files, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            "附錄 H 與 Services/ 不一致（修正方式：python Tools/gen-readme-domains.py）：\n"
            + $"磁碟有而表上沒有（{missing.Count}）：{string.Join("、", missing.Take(10))}\n"
            + $"表上有而磁碟沒有（{extra.Count}）：{string.Join("、", extra.Take(10))}");
    }

    [Fact]
    public void 附錄H的每個數字都要與檔案當場相符()
    {
        // 列存在不代表數字對：行數與類別數是**當場數出來的**，不是抄的。
        // 這一條用與產生器相同的兩個數法重算，抽驗全表。
        string text = Readme();
        int i = text.IndexOf("### 附錄 H", StringComparison.Ordinal);
        int j = text.IndexOf("### 附錄 I", i, StringComparison.Ordinal);
        string section = text[i..j];

        var rows = Regex.Matches(section, @"^\| `([^`]+)` \| (\d+) \| (\d+) \|", RegexOptions.Multiline)
            .Select(m => (Path: m.Groups[1].Value, Lines: int.Parse(m.Groups[2].Value),
                          Classes: int.Parse(m.Groups[3].Value)))
            .ToList();
        Assert.NotEmpty(rows);

        var problems = new List<string>();
        foreach (var (rel, lines, classes) in rows)
        {
            string full = Path.Combine(RepoRoot(), rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) { problems.Add($"{rel}：檔案不存在"); continue; }

            string raw = File.ReadAllText(full);
            int actualLines = raw.Split('\n').Length;
            int actualClasses = raw.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Count(l => Regex.IsMatch(l,
                    @"^\s*(?:(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|unsafe|new)\s+)*class\s+[A-Za-z_]"));

            if (actualLines != lines) problems.Add($"{rel}：寫 {lines} 行，實際 {actualLines}");
            if (actualClasses != classes) problems.Add($"{rel}：寫 {classes} 個類別，實際 {actualClasses}");
        }

        Assert.True(problems.Count == 0,
            "附錄 H 的行數／類別數與檔案不符（修正方式：python Tools/gen-readme-domains.py）：\n"
            + string.Join("\n", problems.Take(20)));
    }

    [Fact]
    public void 附錄J與第三部的說明條目數要等於HelpCatalog()
    {
        string text = Readme();
        int help = HelpCatalog.All.Count;

        Assert.Contains($"另有 **{help}** 條圓圈問號說明條目", text);
        Assert.Contains($"**二、圓圈問號（{help} 條說明）。**", text);
        Assert.Contains($"全站 {help} 條說明條目中", text);
        Assert.Contains($"16. 說明條目 {help} 條 —— 附錄 G 是它的全表。", text);
        Assert.Contains($"| 說明條目 | {help} 條 | 每頁圓圈問號的內容 |", text);
        Assert.Contains($"  - [附錄 G　全站說明條目逐條（{help} 條）]", text);
    }

    [Fact]
    public void 附錄C門檻速查表的結構數字要用本版數字()
    {
        // 附錄 C 自稱「所有成文的門檻都在這裡」，其中六列是**可複算的結構數字**。
        // 第 318 輪對帳時它們停在 381／150／48／64／284／7,040——比正文又舊一輪。
        string text = Readme();
        var d = DomainStats();

        Assert.Contains($"| 測試基線 | {TestSuiteBaseline.ProjectTests:N0}（`TestSuiteBaseline.ProjectTests`） |", text);
        Assert.Contains($"| 覆蓋基線 | {FactCoverageReport.ReviewedUncoveredBaseline} |", text);
        Assert.Contains($"| 事實鍵目錄 | {d.Keys:N0} 把／{d.Domains} 域 |", text);
        Assert.Contains($"| 動態家族 | {FactKeyDynamicCatalog.Families.Count} 個前綴 |", text);
        Assert.Contains($"| 可到達畫面 | {ReachablePages()} |", text);
        Assert.Contains($"| 說明條目 | {HelpCatalog.All.Count} 條 |", text);
    }

    [Fact]
    public void 附錄J的結構與分母條目要用本版數字()
    {
        string text = Readme();
        var d = DomainStats();

        Assert.Contains($"2. 測試 {TestSuiteBaseline.ProjectTests:N0} 綠 —— 跑守門五組不依賴硬體", text);
        Assert.Contains($"13. 事實鍵目錄是 {d.Keys:N0} 把、{d.Domains} 域", text);
        Assert.Contains($"14. 動態家族 {FactKeyDynamicCatalog.Families.Count} 個", text);
        Assert.Contains($"15. 可到達畫面 {ReachablePages()} 個", text);
        Assert.Contains($"17. 服務檔 {d.Services} 個", text);
        Assert.Contains($"| `Services/` | 事實的生產端（{d.Services} 個檔案） |", text);
    }
}
