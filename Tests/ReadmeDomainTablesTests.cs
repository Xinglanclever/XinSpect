using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// README 第 6.3／6.5 節與附錄 F／F-2 是<b>整份鍵目錄的展開</b>（域數、鍵數、家族數全部由目錄決定，不寫在這裡），
/// 由產生器一次寫成。這一支把它的<b>結構</b>釘住：域的編號連續、每一域的鍵數與目錄相符、
/// 展開的鍵集合與目錄逐鍵相等、附錄的首鍵／末鍵真的是字典序首末。
///
/// <para>
/// <b>為什麼不逐列比對內容：</b>那等於把 README 變成第二份目錄——兩份一樣的資料互相驗證，
/// 只證明它們一樣，不證明它們對。這一支只問「這份展開有沒有漏掉或多出任何一把鍵」，
/// 那才是會出錯的地方（產生器換了、目錄改了而文件沒重跑）。
/// </para>
///
/// <para>
/// <b>紅燈訊息指名道姓：</b>少了哪一把、多了哪一把、哪一域的計數不符、哪一域的編號跳號。
/// 一份三萬多行的展開如果只回一句「集合不相等」，沒有人能修它。
/// </para>
/// </summary>
public class ReadmeDomainTablesTests
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

    private static string Slice(string start, string end)
    {
        string text = Readme();
        int i = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, $"README 找不到區段「{start}」");
        int j = text.IndexOf(end, i, StringComparison.Ordinal);
        Assert.True(j > i, $"README 找不到「{start}」之後的結束標記「{end}」");
        return text[i..j];
    }

    /// <summary>目錄裡每個域有幾把鍵（字典序）。</summary>
    private static SortedDictionary<string, List<string>> CatalogByDomain()
    {
        var map = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string key in FactKeyCatalog.Keys)
        {
            string domain = key.Split('.')[0];
            if (!map.TryGetValue(domain, out var list)) map[domain] = list = [];
            list.Add(key);
        }
        return map;
    }

    // ── 第 6.3 節：逐域鍵表 ───────────────────────────────────────────────

    [Fact]
    public void 第六點三節展開的鍵集合要與目錄逐鍵相等()
    {
        string section = Slice("### 6.3 逐域鍵表", "### 6.4 跨域通則");
        var rows = Regex.Matches(section, @"^\| `([^`]+)` \|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(rows.Count, rows.Distinct(StringComparer.Ordinal).Count());

        var documented = rows.ToHashSet(StringComparer.Ordinal);
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);

        var missing = catalog.Except(documented).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extra = documented.Except(catalog).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0 && extra.Count == 0,
            "README 第 6.3 節的逐域鍵表與事實鍵目錄不一致（重跑產生器，或手動補上）：\n"
            + $"目錄有而文件沒有（{missing.Count}）：{string.Join("、", missing.Take(15))}\n"
            + $"文件有而目錄沒有（{extra.Count}）：{string.Join("、", extra.Take(15))}");
    }

    /// <summary>
    /// 6.3 節的排序規則是「鍵數由多到少，同數再按域名字典序」——那是讀者想先看到大域的順序，
    /// 不是字典序。守門照這條規則驗，否則會把正確的排列判成錯的。
    /// </summary>
    private static List<(int Number, string Domain, int Count)> DomainHeaders(string section, string marker)
    {
        var list = new List<(int, string, int)>();
        foreach (Match m in Regex.Matches(section, "^" + marker + @"(\d+) 域 `([^`]+)`(.*)$",
                     RegexOptions.Multiline))
        {
            int number = int.Parse(m.Groups[1].Value);
            string domain = m.Groups[2].Value;
            var c = Regex.Match(m.Groups[3].Value, @"（(\d+) 把）");
            Assert.True(c.Success, $"域 `{domain}` 的標題沒有「（N 把）」計數");
            list.Add((number, domain, int.Parse(c.Groups[1].Value)));
        }
        return list;
    }

    [Fact]
    public void 第六點三節的域編號要連續且每個標題的鍵數要等於該域的實際列數()
    {
        string section = Slice("### 6.3 逐域鍵表", "### 6.4 跨域通則");
        var headers = DomainHeaders(section, @"### 6\.3\.");

        var byDomain = CatalogByDomain();
        var problems = new List<string>();

        if (headers.Count != byDomain.Count)
            problems.Add($"標題 {headers.Count} 個，目錄有 {byDomain.Count} 個域");

        for (int i = 0; i < headers.Count; i++)
        {
            if (headers[i].Number != i + 1)
            {
                problems.Add($"第 {i + 1} 個標題編號是 {headers[i].Number}（應為 {i + 1}）");
                break;
            }
        }

        // 排序規則：鍵數由多到少，同數再按域名字典序。
        var actualOrder = headers.Select(h => (h.Count, h.Domain)).ToList();
        var expectedOrder = actualOrder.OrderByDescending(x => x.Count)
            .ThenBy(x => x.Domain, StringComparer.Ordinal).ToList();
        if (!actualOrder.SequenceEqual(expectedOrder))
        {
            int at = Enumerable.Range(0, actualOrder.Count)
                .First(i => !actualOrder[i].Equals(expectedOrder[i]));
            problems.Add($"域標題的排序不是「鍵數遞減、同數按字典序」（第 {at + 1} 個開始不同："
                + $"實際 {actualOrder[at].Domain}／{actualOrder[at].Count}，"
                + $"應為 {expectedOrder[at].Domain}／{expectedOrder[at].Count}）");
        }

        foreach (var (_, domain, count) in headers)
        {
            int actual = Regex.Matches(section, @"^\| `" + Regex.Escape(domain) + @"\.", RegexOptions.Multiline).Count;
            if (actual != count)
                problems.Add($"域 `{domain}` 標題寫 {count} 把，實際列了 {actual} 列");
            if (byDomain.TryGetValue(domain, out var keys) && keys.Count != count)
                problems.Add($"域 `{domain}` 標題寫 {count} 把，目錄裡是 {keys.Count} 把");
        }

        Assert.True(problems.Count == 0,
            "README 第 6.3 節的域結構有問題：" + Environment.NewLine
            + string.Join(Environment.NewLine, problems.Take(20)));
    }

    [Fact]
    public void 第六點三節與第六點五節與附錄F的域清單要一致()
    {
        var inSection = DomainHeaders(Slice("### 6.3 逐域鍵表", "### 6.4 跨域通則"), @"### 6\.3\.")
            .Select(h => h.Domain).ToList();
        var inSource = DomainHeaders(Slice("### 6.5 逐鍵出處", "## 第 7 部"), @"#### 6\.5\.")
            .Select(h => h.Domain).ToList();
        var inAppendix = Regex.Matches(Slice("### 附錄 F　", "### 附錄 F-2"),
                @"^\| `([^`]+)` \| \d+ \|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(inSection, inSource);
        Assert.Equal(inSection, inAppendix);
        Assert.Equal(CatalogByDomain().Keys.OrderBy(k => k, StringComparer.Ordinal),
            inSection.OrderBy(k => k, StringComparer.Ordinal));
    }

    // ── 第 6.5 節：逐鍵出處 ──────────────────────────────────────────────

    [Fact]
    public void 第六點五節逐鍵出處的鍵集合要與目錄逐鍵相等()
    {
        string section = Slice("### 6.5 逐鍵出處", "## 第 7 部");
        var bullets = Regex.Matches(section, @"^- \*\*`([^`]+)`\*\*", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(bullets.Count, bullets.Distinct(StringComparer.Ordinal).Count());

        var documented = bullets.ToHashSet(StringComparer.Ordinal);
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = catalog.Except(documented).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extra = documented.Except(catalog).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0 && extra.Count == 0,
            "README 第 6.5 節的逐鍵出處與事實鍵目錄不一致：\n"
            + $"目錄有而文件沒有（{missing.Count}）：{string.Join("、", missing.Take(15))}\n"
            + $"文件有而目錄沒有（{extra.Count}）：{string.Join("、", extra.Take(15))}");
    }

    [Fact]
    public void 第六點五節的覆蓋率申報要與實際抽得到的把數相符()
    {
        // 「N 把之中有 M 把在 Services/ 的字面中找得到」——N 是目錄鍵數，
        // M 由掃描決定。這一句是唯一會被「目錄改了但沒重跑產生器」弄壞的現在式宣稱。
        string text = Readme();
        var m = Regex.Match(text, @"(\d[\d,]*) 把鍵之中有 (\d[\d,]*) 把在本版 `Services/` 的字面中找得到位置");
        Assert.True(m.Success, "README 第 6.5 節找不到覆蓋率申報那一句");

        int claimedTotal = int.Parse(m.Groups[1].Value.Replace(",", ""));
        int claimedFound = int.Parse(m.Groups[2].Value.Replace(",", ""));

        string corpus = string.Concat(Directory
            .GetFiles(Path.Combine(RepoRoot(), "Services"), "*.cs", SearchOption.AllDirectories)
            .Select(f => File.ReadAllText(f)));
        int found = FactKeyCatalog.Keys.Count(k => corpus.Contains("\"" + k, StringComparison.Ordinal));

        Assert.True(claimedTotal == FactKeyCatalog.Keys.Count,
            $"申報的總數 {claimedTotal} 不等於目錄的 {FactKeyCatalog.Keys.Count} 把");
        Assert.True(claimedFound == found,
            $"申報「找得到 {claimedFound} 把」，實際掃到 {found} 把");
    }

    // ── 附錄 F／F-2 ──────────────────────────────────────────────────────

    [Fact]
    public void 附錄F每一列的鍵數與首末鍵要與目錄相符()
    {
        string appendix = Slice("### 附錄 F　", "### 附錄 F-2");
        var rows = Regex.Matches(appendix,
                @"^\| `([^`]+)` \| (\d+) \| `([^`]+)` \| `([^`]+)` \|", RegexOptions.Multiline)
            .Select(m => (Domain: m.Groups[1].Value, Count: int.Parse(m.Groups[2].Value),
                          First: m.Groups[3].Value, Last: m.Groups[4].Value))
            .ToList();

        var byDomain = CatalogByDomain();
        var problems = new List<string>();

        if (rows.Count != byDomain.Count)
            problems.Add($"附錄 F 列了 {rows.Count} 個域，目錄有 {byDomain.Count} 個");

        foreach (var (domain, count, first, last) in rows)
        {
            if (!byDomain.TryGetValue(domain, out var keys))
            {
                problems.Add($"附錄 F 的 `{domain}` 不在目錄裡");
                continue;
            }
            // 目錄本身已按字典序排好，所以某域的首末鍵就是該域的第一把與最後一把。
            var ordered = keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (count != keys.Count) problems.Add($"`{domain}` 寫 {count} 把，目錄 {keys.Count} 把");
            if (first != ordered[0]) problems.Add($"`{domain}` 首鍵寫 {first}，實際 {ordered[0]}");
            if (last != ordered[^1]) problems.Add($"`{domain}` 末鍵寫 {last}，實際 {ordered[^1]}");
        }

        var documented = rows.Select(r => r.Domain).ToHashSet(StringComparer.Ordinal);
        var missing = byDomain.Keys.Where(d => !documented.Contains(d)).ToList();
        if (missing.Count > 0) problems.Add($"附錄 F 少了這些域：{string.Join("、", missing)}");

        Assert.True(problems.Count == 0,
            "README 附錄 F 與事實鍵目錄不一致：\n" + string.Join("\n", problems.Take(20)));
    }

    [Fact]
    public void 附錄F2的家族清單要與動態家族登記逐條相等()
    {
        string appendix = Slice("### 附錄 F-2", "### 附錄 G");
        var rows = Regex.Matches(appendix, @"^\| `([^`]+)` \|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(rows.Count, rows.Distinct(StringComparer.Ordinal).Count());

        var documented = rows.ToHashSet(StringComparer.Ordinal);
        var declared = FactKeyDynamicCatalog.Families.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = declared.Except(documented).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extra = documented.Except(declared).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0 && extra.Count == 0,
            "README 附錄 F-2 與動態家族登記不一致：\n"
            + $"登記有而文件沒有（{missing.Count}）：{string.Join("、", missing)}\n"
            + $"文件有而登記沒有（{extra.Count}）：{string.Join("、", extra)}");
    }

    [Fact]
    public void 附錄F2的理由欄要與登記的原文相同()
    {
        // 兩份文字內容相同的話，這裡只是重複；但「文件把理由改寫過」是另一種失真——
        // 讀者會以為那是原始申報，其實是摘要。抽樣三條比對原文。
        string appendix = Slice("### 附錄 F-2", "### 附錄 G");
        var sampled = FactKeyDynamicCatalog.Families.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(3);
        foreach (string prefix in sampled)
        {
            string reason = FactKeyDynamicCatalog.Families[prefix];
            Assert.Contains(prefix, appendix);
            // 只比對前 20 個字：README 的表格會把長理由整段照抄，抽樣確認沒有被改寫。
            string head = reason.Length > 20 ? reason[..20] : reason;
            Assert.Contains(head, appendix);
        }
    }

    // ── 第 6.3 節的後設資料欄：第 288 輪補的守門 ───────────────────────

    /// <summary>§6.3 的一列：鍵 → 三格（名稱／來源／可信度）。</summary>
    private static Dictionary<string, string[]> MetadataRows()
    {
        string section = Slice("### 6.3 逐域鍵表", "### 6.4 跨域通則");
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(section,
                     @"^\| `([^`]+)` \| ([^|]*) \| ([^|]*) \| ([^|]*) \|", RegexOptions.Multiline))
        {
            map[m.Groups[1].Value] = [m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim(), m.Groups[4].Value.Trim()];
        }
        return map;
    }

    [Fact]
    public void 原始碼把名字寫在建構子裡的鍵_第六點三節就要填得出來()
    {
        // 這一欄的定義是「原始碼字面」。第 288 輪之前產生器只認「鍵字面直接塞進建構子」，
        // 而 1,131 把鍵裡有 839 把是「先宣告常數、再 new HardwareFact(常數, …)」——
        // 於是那一欄幾乎全是「—」，一張 1,131 列的表變成一片破折號。
        var rows = MetadataRows();
        // 只認「鍵與名稱都是純字面」的呼叫：名稱若是 `$"來源裁決：{subject}"` 這種插值，
        // 抽不到是正確的（留「—」比填半個值誠實），這裡就不該要求它。
        var literal = new Regex(
            @"new\s+HardwareFact\(\s*""([a-z][a-z0-9_.]+)""\s*,\s*[^,""
]+,\s*""([^""
]*)""",
            RegexOptions.Compiled);

        string corpus = string.Concat(Directory
            .GetFiles(Path.Combine(RepoRoot(), "Services"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = literal.Matches(corpus).Select(m => m.Groups[1].Value)
            .Where(k => catalog.Contains(k))
            .Distinct(StringComparer.Ordinal)
            // 只要求**名稱**：來源常常是 `$"{Source}｜狀態≠可達"` 這種組合字串，
            // 抽不到是對的（留「—」比填一個只對一半的值誠實）。
            .Where(k => !rows.TryGetValue(k, out var cells) || cells[0] == "—")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "以下鍵在原始碼裡有「名字與來源都寫死」的建構子呼叫，文件卻還是「—」："
            + string.Join("、", missing.Take(20))
            + "（修正方式：python Tools/gen-readme-domains.py）");
    }

    [Fact]
    public void 第六點三節的每一列都要是單行()
    {
        // 一個儲存格裡有換行，那一列就破了表格——而**既有的守門看不出來**：
        // 比對用的 `[^|]*` 會跨過換行，所以列被拆成兩行之後仍然「匹配成功」，一路綠。
        // 第 288 輪真的發生過一次：來源字串在原始碼裡是兩段 `"…" + "…"` 串接，
        // 產生器把換行與加號原封不動寫進格子（`sb.onboard` 那一列）。
        string section = Slice("### 6.3 逐域鍵表", "### 6.4 跨域通則");

        var broken = section.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("| `", StringComparison.Ordinal) && !l.EndsWith("|", StringComparison.Ordinal))
            .ToList();

        Assert.True(broken.Count == 0,
            "以下列沒有在自己的那一行收尾（表格破了）：" + Environment.NewLine
            + string.Join(Environment.NewLine, broken.Take(5)));
    }

    [Fact]
    public void 第六點三節的後設資料填寫率不得退步()
    {
        // 下限不是目標，是**退化警報**：第 288 輪把「三格全填」的列從 112 推到 683、
        // 「名稱」欄從 169 推到 964。低於下限代表抽取退化了（常數解析壞掉、
        // 或有人把產生器改回只認字面），而文件看起來仍然一切正常。
        //
        // 為什麼名稱是主要指標：「來源」常常是 `$"{Source}｜狀態≠可達"` 這種組合字串，
        // 抽不到是正確的；而**同一把鍵有兩條建構路徑時（有值／讀不到），兩條的可信度
        // 可以不同**——那一格留「—」，因為任選一個都是在替另一條路徑說話。
        var rows = MetadataRows();
        int named = rows.Values.Count(c => c[0] != "—");
        int full = rows.Values.Count(c => c.All(x => x != "—"));

        Assert.True(named >= 900, $"§6.3 只有 {named} 列填了名稱（下限 900，第 288 輪實測 964）");
        Assert.True(full >= 650, $"§6.3 只有 {full} 列三格全填（下限 650，第 288 輪實測 683）");
    }

    // ── 目錄（TOC）：第 283 輪補的守門 ──────────────────────────────────    // ── 目錄（TOC）：第 283 輪補的守門 ──────────────────────────────────

    /// <summary>GitHub 標題錨點的規則：去反引號與標點、空白轉連字號、保留中文。</summary>
    private static string Slug(string heading)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in heading.Replace("`", "").ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);
            else if (ch is ' ' or ' ' or '　') sb.Append('-');
        }
        return sb.ToString();
    }

    [Fact]
    public void 目錄的6點3條目要與章節逐條對應()
    {
        // 第 283 輪之前沒有人管這段目錄：v2.99 的目錄寫「6.3.57 域 kp」，
        // 而章節已經是「6.3.58」——沒有任何測試看得出來，因為守門只讀章節。
        // 目錄是三萬行文件的導覽，編號與錨點錯了，等於導覽指到別的地方。
        string text = Readme();
        var toc = Regex.Matches(text, @"^  - \[6\.3\.(\d+) 域 `([^`]+)`([^\]]*)\]\(#([^)]+)\)",
                RegexOptions.Multiline)
            .Select(m => (Number: int.Parse(m.Groups[1].Value), Domain: m.Groups[2].Value,
                          Suffix: m.Groups[3].Value.Trim(), Anchor: m.Groups[4].Value))
            .ToList();
        var heads = Regex.Matches(text, @"^### 6\.3\.(\d+) 域 `([^`]+)`([^
]*)",
                RegexOptions.Multiline)
            // Heading 保留原文（含前後空白）——錨點是對「整行標題」算的，
            // 先 Trim 再拼回去會少一個連字號（`ac — ACPI` 與 `ac— ACPI` 是兩個不同的錨點）。
            .Select(m => (Heading: m.Value[4..], Number: int.Parse(m.Groups[1].Value),
                          Domain: m.Groups[2].Value, Suffix: m.Groups[3].Value))
            .ToList();

        var problems = new List<string>();
        if (toc.Count != heads.Count)
            problems.Add($"目錄列了 {toc.Count} 條，章節有 {heads.Count} 個");

        for (int i = 0; i < Math.Min(toc.Count, heads.Count); i++)
        {
            var (at, ah) = (toc[i], heads[i]);
            if (at.Number != ah.Number)
                problems.Add($"第 {i + 1} 條：目錄寫 6.3.{at.Number}，章節是 6.3.{ah.Number}（域 {ah.Domain}）");
            if (at.Domain != ah.Domain)
                problems.Add($"第 {i + 1} 條：目錄寫域 {at.Domain}，章節是 {ah.Domain}");
            if (at.Suffix.Trim() != ah.Suffix.Trim())
                problems.Add($"第 {i + 1} 條（{ah.Domain}）：目錄寫「{at.Suffix.Trim()}」，章節是「{ah.Suffix.Trim()}」");

            string want = Slug(ah.Heading);
            if (at.Anchor != want)
                problems.Add($"第 {i + 1} 條（{ah.Domain}）：錨點 #{at.Anchor} 指不到這個標題（應為 #{want}）");
        }

        Assert.True(problems.Count == 0,
            "README 目錄的 6.3 條目與章節對不上（修正方式：python Tools/gen-readme-domains.py）：\n"
            + string.Join("\n", problems.Take(20)));
    }

    [Fact]
    public void 目錄與章節標題的域數鍵數要與目錄相符()
    {
        string text = Readme();
        int domains = CatalogByDomain().Count;
        int keys = FactKeyCatalog.Keys.Count;
        int families = FactKeyDynamicCatalog.Families.Count;

        // 兩處正文標題＋兩處目錄條目：四個數字同源，任何一處落後都是同一種說謊。
        Assert.Contains($"## 第 6 部 數據辭典：{domains} 個域、{keys:N0} 把鍵", text);
        Assert.Contains($"### 6.3 逐域鍵表（{domains} 個域、{keys:N0} 把鍵）", text);
        Assert.Contains($"  - [6.3 逐域鍵表（{domains} 個域、{keys:N0} 把鍵）]"
                        + $"(#63-逐域鍵表{domains}-個域{keys}-把鍵)", text);
        Assert.Contains($"  - [附錄 F　{domains} 個域的前綴表]", text);
        Assert.Contains($"  - [附錄 F-2　{families} 個動態家族", text);
    }
}
