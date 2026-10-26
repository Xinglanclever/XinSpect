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
        "1.9.2", "1.9.1", "1.9.0", "1.8.2", "1.8.1", "1.8.0", "1.7.7", "1.7.6", "1.7.5", "1.7.4", "1.7.3", "1.7.2", "1.7.1", "1.6.2", "1.6.1", "1.6.0",
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
    /// <remarks>
    /// README 那一處要先做 shields.io 的轉義：它拿 <c>-</c> 當欄位分隔符，所以版號裡的連字號
    /// （預覽版的 <c>1.9.1-B1</c>）必須寫成 <c>--</c>，否則徽章會把「B1」讀成顏色。
    /// 這個轉義只影響徽章網址，不是版號本身不一致。
    /// </remarks>
    [Fact]
    public void 三處版號與紀錄一致()
    {
        string v = ChangelogCatalog.Latest;
        Assert.Contains($"version-{v.Replace("-", "--")}-", ReadRepoFile("README.md"));
        Assert.Contains($"版本 {v} ・", ReadRepoFile(Path.Combine("Views", "AboutView.xaml")));
    }

    /// <summary>
    /// <b>&lt;FileVersion&gt; 是版號的第四處，而它真的落後過。</b>
    ///
    /// <para>
    /// v2.85 出貨的 exe，檔案內容裡顯示的「檔案版本」是 <c>2.84.0.0</c>——
    /// 因為當時只改了 <c>&lt;Version&gt;</c>，忘了改它。
    /// <c>FileVersion</c> 只吃數字，所以寫法固定是三段版號加第四段建置號；
    /// 前三段必須與 <c>&lt;Version&gt;</c> 相同。
    /// </para>
    ///
    /// <para>
    /// <b>為什麼這值得一條測試：</b>它不會讓程式壞掉、也不會讓測試紅，
    /// 只會讓使用者在檔案內容裡看到一個少一版的號碼，而<b>那個號碼是他唯一看得到的地方</b>。
    /// 先前的 <c>ChangelogTests</c> 只看紀錄、徽章與關於頁三者，正好從它旁邊走過去。
    /// </para>
    /// </summary>
    [Fact]
    public void FileVersion的前三段要與Version相同()
    {
        string csproj = ReadRepoFile("XinSpect.csproj");
        string version = Regex.Match(csproj, @"<Version>([^<]+)</Version>").Groups[1].Value;
        string fileVersion = Regex.Match(csproj, @"<FileVersion>([^<]+)</FileVersion>").Groups[1].Value;

        Assert.False(string.IsNullOrEmpty(version), "XinSpect.csproj 裡找不到 <Version>");
        Assert.False(string.IsNullOrEmpty(fileVersion), "XinSpect.csproj 裡找不到 <FileVersion>");
        Assert.StartsWith(version + ".", fileVersion);
    }

    /// <summary>
    /// 版號還寫在幾處<b>不受守門保護</b>的地方：三份 README 的下載連結與版本沿革。
    /// 這些地方先前真的落後過（v2.26 時期 README 還指著 v2.2.0），而守門測試只看徽章，
    /// 所以點下載的人會拿到舊版。這裡把守門範圍擴大到每一個指得出舊版號的地方。
    /// </summary>
    [Fact]
    public void 三份README的下載連結與版本沿革都指向當前版本()
    {
        string v = ChangelogCatalog.Latest;
        foreach (string file in new[] { "README.md", "README.zh-CN.md", "README.en.md" })
        {
            string text = ReadRepoFile(file);

            // 下載連結（Release 頁與三個資產）不得指向別的版號
            foreach (Match m in Regex.Matches(text, @"/releases/(?:tag|download)/(v\d+\.\d+)").Cast<Match>())
                Assert.True(m.Groups[1].Value == "v" + v,
                    $"{file}：下載連結指向 {m.Groups[1].Value}，但當前版本是 v{v}");

            // 徽章已由上一條測試釘住；版本沿革則要求當前版號出現
            Assert.Contains(v, text);
        }
    }

    /// <summary>
    /// 版本沿革裡的最新一筆必須與 <see cref="ChangelogCatalog"/> 的最新一筆對得上——
    /// 三份 README 各寫各的沿革時，最容易只更新其中一份。
    /// </summary>
    [Fact]
    public void 三份README的版本沿革最新一筆都對得上紀錄()
    {
        string v = ChangelogCatalog.Latest;
        string title = ChangelogCatalog.Entries[0].Title;
        foreach (string file in new[] { "README.md", "README.zh-CN.md" })
        {
            string text = ReadRepoFile(file);
            var first = Regex.Match(text, @"^- \*\*v(\d+\.\d+)[^*]*\*\*", RegexOptions.Multiline);
            Assert.True(first.Success, $"{file}：找不到版本沿革的最新一筆");
            Assert.True(first.Groups[1].Value == v,
                $"{file}：版本沿革最新一筆是 v{first.Groups[1].Value}，但當前版本是 v{v}（紀錄標題：{title}）");
        }
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

    /// <summary>
    /// README 的測試徽章是一句「現在式」的宣稱，但先前沒有任何守門——版號被釘住了，測試數沒有，
    /// 於是徽章停在 3195，而同一份文件的版本沿革已經寫到 3524。一張紙上兩個數字，讀者無從判斷。
    /// 徽章現在钉在 <see cref="TestSuiteBaseline.ProjectTests"/> 上，只有一個來源。
    /// </summary>
    [Fact]
    public void 三份README的測試徽章等於專案測試基線()
    {
        string n = TestSuiteBaseline.ProjectTests.ToString();
        foreach (string file in new[] { "README.md", "README.zh-CN.md", "README.en.md" })
        {
            var badge = Regex.Match(ReadRepoFile(file), @"badge/tests-(\d+)%20passed");
            Assert.True(badge.Success, $"{file}：找不到測試徽章");
            Assert.True(badge.Groups[1].Value == n,
                $"{file}：測試徽章寫 {badge.Groups[1].Value}，但專案測試基線是 {n}");
        }
    }

    /// <summary>
    /// 基線是手寫的，所以它自己也可能過期——上一版的缺陷就是「一個沒人守著的數字」。
    /// 這裡不（也不可能）數出「執行後共有幾條測試」，但可以數出測試組件裡有幾個[Fact]/[Theory]
    /// 方法：執行數一定不少於方法數，所以基線低於方法數就一定是忘了改。
    /// 它抓得住「加了一整批測試卻沒動基線」，抓不住差一兩條的疏忽——那個要靠人。
    /// </summary>
    [Fact]
    public void 專案測試基線不得低於測試組件裡的測試方法數()
    {
        int methods = typeof(ChangelogTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Count(m => m.GetCustomAttributes(inherit: false)
                .Any(a => a is FactAttribute || a is TheoryAttribute));

        Assert.True(methods > 0, "一個測試方法都沒數到——反射邏輯壤了");
        Assert.True(TestSuiteBaseline.ProjectTests >= methods,
            $"基線寫 {TestSuiteBaseline.ProjectTests}，但測試組件裡有 {methods} 個測試方法——" +
            "加了測試卻忘了改 TestSuiteBaseline.ProjectTests");
    }

    /// <summary>
    /// 下載表的註腳是一句現在式的說明（「本表的位元組數為本版（vX）實際發佈的檔案大小」），
    /// 它曾經停在 v2.31，而同一張表的連結已經指到 v2.37：連結被規矩釘住了，這句沒有。
    /// 連「說明這張表」的文字都落後，讀者就有理由懷疑整張表。
    /// </summary>
    [Fact]
    public void 下載表註腳的版號不得落後於當前版本()
    {
        string v = ChangelogCatalog.Latest;
        foreach (string file in new[] { "README.md", "README.zh-CN.md" })
        {
            var lines = ReadRepoFile(file)
                .Split('\n')
                .Where(l => l.Contains("實際發佈的檔案大小") || l.Contains("实际发布的文件大小"))
                .ToList();
            Assert.True(lines.Count == 1, $"{file}：下載表註腳應該只有一句，實際找到 {lines.Count} 句");

            var m = Regex.Match(lines[0], @"v\d+\.\d+");
            Assert.True(m.Success, $"{file}：下載表註腳裡沒有版號");
            Assert.True(m.Value == "v" + v, $"{file}：下載表註腳寫 {m.Value}，但當前版本是 v{v}");
        }
    }

    // ── 三份文件的沿革與紀錄逐筆對帳 ──────────────────────────────────────

    /// <summary>
    /// 同一個版號在沿革裡出現兩次，讀者看到的是兩個同名條目——而且兩條的內容會不一樣。
    /// 簡體版真的這樣過：v2.90／v2.89 各有一條短行加一條長行（短行就是長行的開頭被誤留了一份），
    /// <b>而沒有任何守門看得出來</b>，因為兩行都「格式正確」、版號也都在。
    /// </summary>
    [Fact]
    public void 三份README的版本沿革不得有重複版號()
    {
        var bad = new List<string>();

        foreach (string file in new[] { "README.md", "README.zh-CN.md" })
        {
            var seen = new Dictionary<string, int>();
            string[] lines = ReadRepoFile(file).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                // 抓整段粗體標題：v1.9.1-B1 與 v1.9.1 是兩筆，不能用「數字點數字」去截
                var m = Regex.Match(lines[i], @"^- \*\*v([^*]+)\*\*");
                if (!m.Success) continue;
                string key = m.Groups[1].Value.Trim();
                if (seen.TryGetValue(key, out int first))
                    bad.Add($"{file}：v{key} 出現兩次（第 {first + 1} 行與第 {i + 1} 行）");
                else seen[key] = i;
            }
        }

        // 英文版的逐版表是表格形狀（| 3.00 | 2026-10-26 | … |），換一套抓法但問同一件事
        var seenEn = new Dictionary<string, int>();
        string[] en = ReadRepoFile("README.en.md").Split('\n');
        for (int i = 0; i < en.Length; i++)
        {
            var m = Regex.Match(en[i], @"^\| (\d+\.\d+) \| \d{4}-\d{2}-\d{2} \|");
            if (!m.Success) continue;
            string key = m.Groups[1].Value;
            if (seenEn.TryGetValue(key, out int first))
                bad.Add($"README.en.md：{key} 出現兩次（第 {first + 1} 行與第 {i + 1} 行）");
            else seenEn[key] = i;
        }

        Assert.True(bad.Count == 0, "版本沿革有重複條目（同一版兩個條目，讀者無從判斷哪個對）：\n"
            + string.Join("\n", bad));
    }

    /// <summary>
    /// README 第十一部的兩層清單要跟紀錄一筆對一筆：11.2 是「一句話一版」，11.3 是「逐項翻開」。
    /// 兩層都沒有守門時，補了新紀錄只會出現在「關於」頁與資料檔裡——而 README 是這份文件唯一
    /// 對外的版本沿革，少了那一筆，讀者會以為那一版不存在。
    /// </summary>
    [Fact]
    public void README的兩層沿革都要跟紀錄一筆對一筆()
    {
        string text = ReadRepoFile("README.md");
        int entries = ChangelogCatalog.Entries.Count;

        int bullets = Regex.Matches(Slice(text, "### 11.2 逐版紀錄", "### 11.3 逐版逐項"), @"(?m)^- \*\*").Count;
        Assert.True(bullets == entries,
            $"README 第 11.2 節有 {bullets} 筆，紀錄有 {entries} 筆（新紀錄要補一行）");

        int sections = Regex.Matches(Slice(text, "### 11.3 逐版逐項", "## 第 12 部"), @"(?m)^#### v").Count;
        Assert.True(sections == entries,
            $"README 第 11.3 節有 {sections} 個「#### v…」小節，紀錄有 {entries} 筆（新紀錄要翻開一節）");
    }

    /// <summary>
    /// 最新一筆的逐項文字必須逐字出現在 11.3 那一節。
    /// 11.2 只寫一句話，真正給讀者的細節在 11.3；那一節先前完全沒有人管，抄錯或漏抄都不會紅。
    /// 只驗最新一筆是刻意的：它是「這一版做了什麼」唯一的正式說法，也是每次改動最先動到的那一筆。
    /// </summary>
    [Fact]
    public void 最新一筆的逐項文字要逐字出現在逐版逐項()
    {
        var latest = ChangelogCatalog.Entries[0];
        string text = ReadRepoFile("README.md");
        int start = text.IndexOf("### 11.3 逐版逐項", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到第 11.3 節");
        int head = text.IndexOf("#### v" + latest.Version, start, StringComparison.Ordinal);
        Assert.True(head >= 0, $"第 11.3 節找不到 v{latest.Version} 那一節");
        int next = text.IndexOf("\n#### v", head + 4, StringComparison.Ordinal);
        if (next < 0) next = text.Length;
        string section = text[head..next];

        var missing = latest.Items.Where(i => !section.Contains("- " + i, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0,
            $"v{latest.Version} 的逐項文字與紀錄不一致（第 11.3 節缺這幾條）：\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// 英文版的逐版表宣稱「列出 2.60 以後每一版」——宣稱與內容要一起成立。
    /// 這張表在 v3.00 之前漏了 v2.99（版本加了、表沒加），而沒有任何東西在數它。
    /// </summary>
    [Fact]
    public void 英文版的逐版表要涵蓋它宣稱的範圍()
    {
        string en = ReadRepoFile("README.en.md");
        Assert.True(en.Contains("every release from 2.60 onward", StringComparison.Ordinal),
            "英文版宣稱的那句話不見了——這條守門是照著它寫的，句改了就要一起改");

        var have = new HashSet<string>(
            Regex.Matches(en, @"(?m)^\| (\d+\.\d+) \| \d{4}-\d{2}-\d{2} \|").Select(m => m.Groups[1].Value));

        var missing = ChangelogCatalog.Entries
            .Select(e => e.Version)
            .Where(v => AtLeast(v, 2, 60) && !have.Contains(v))
            .ToList();

        Assert.True(missing.Count == 0,
            "英文版逐版表宣稱涵蓋 2.60 以後每一版，但少了：" + string.Join("、", missing));
    }

    /// <summary>版本字串「主.次」是否不低於 <paramref name="major"/>.<paramref name="minor"/>（只比數字，不比字串）。</summary>
    private static bool AtLeast(string version, int major, int minor)
    {
        var parts = version.Split('.');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b)) return false;
        return a * 100 + b >= major * 100 + minor;
    }

    private static string Slice(string text, string from, string to)
    {
        int i = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(i >= 0, $"找不到「{from}」");
        int j = text.IndexOf(to, i + from.Length, StringComparison.Ordinal);
        Assert.True(j > i, $"找不到「{to}」（在「{from}」之後）");
        return text[i..j];
    }

    // ── 讀原始碼樹 ────────────────────────────────────────────────────────

    /// <summary>
    /// 檢視的 XAML 屬性不得留下舊版號。
    ///
    /// <para>
    /// <c>MainWindow.xaml</c> 與 <c>AboutView.xaml</c> 各寫死過一次
    /// <c>"XinSpect v2.5 Olympus"</c>，而程式在執行期會用 <see cref="AppInfo"/> 覆寫它們——
    /// 所以使用者看不到舊字串，<b>也就沒有人會發現它舊了</b>：兩個字面值一路落後了三十幾個版本。
    /// 這是「現在式的宣稱」家族裡最安靜的一種（畫面上被蓋掉、編譯器不管、守門也沒數它）。
    /// </para>
    /// <para>
    /// 現在的規矩是「XAML 不寫版號」（版號只有一個來源），而這條守門允許一種例外：
    /// 寫了就必須等於當前版號——這樣「補一個展示用的字面值」不會被禁，但寫舊的一定紅。
    /// 只掃屬性值（<c>Title="…"</c>／<c>Text="…"</c>），不掃註解——註解裡的 <c>v2.60</c>
    /// 是「這個功能是哪一版做的」，那是歷史，不是現在式。
    /// </para>
    /// </summary>
    [Fact]
    public void 檢視的XAML不得留下舊版號()
    {
        string version = CsprojVersion();
        string[] files = ["MainWindow.xaml", "Views/AboutView.xaml"];
        var bad = new List<string>();
        foreach (string file in files)
        {
            string text = ReadRepoFile(file);
            // 逐行掃描：只取引號裡的屬性值，跳過註解行
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("<!--", StringComparison.Ordinal)) continue;
                foreach (Match m in Regex.Matches(line, "=\"([^\"]*)\""))
                {
                    var v = Regex.Match(m.Groups[1].Value, @"v(\d+\.\d+)");
                    if (v.Success && v.Groups[1].Value != version)
                        bad.Add($"{file}：屬性值「{m.Groups[1].Value}」帶著 v{v.Groups[1].Value}，但當前版號是 {version}");
                }
            }
        }
        Assert.True(bad.Count == 0,
            "XAML 的版號字面值落後了（程式會覆寫它，所以畫面上看不出來）：\n" + string.Join("\n", bad));
    }

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
