using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 計畫庫（規格庫）的算術守門。
///
/// <para>
/// <b>為什麼要有這一組：</b><c>docs/ULTIMATE-1000/README.md</c> §六自己規定「每 25 項跑一次
/// 稽核工具與守門測試」——但那個稽核工具（<c>work/UltimateAudit/</c>）住在<b>未入版控的暫存區</b>。
/// 代價在 2026-10-18 具體發生：那份文件後來長出第 11 份檔案（<c>STAGE-02-REMAINING-KEYS.md</c>），
/// 於是它的自我檢查（寫死「底下有 10 份階段檔」）與階段檔的 glob（<c>STAGE-02-*.md</c> 命中兩份、
/// <c>.Single()</c> 直接拋例外）雙雙失效——<b>規定的閘門連跑都跑不動，而且沒有人會知道</b>，
/// 因為它不在任何自動跑的地方。這一組把它搬進測試：閘門變成會被跑的東西，壞掉會紅。
/// </para>
///
/// <para>
/// <b>解析一律用字面比對，不用「字元類別＋量詞」。</b>這條紀律來自一個被誤診成環境問題的真實缺陷：
/// 舊稽核用 <c>[A-Z]+\d?</c> 認域碼，於是 <c>F10</c> 認不出來（階段一一整列消失、宣告變成 95），
/// 而 <c>F1</c>–<c>F9</c> 都認得。當時的結論一度寫成「本機的 Python 與 .NET 兩個正則引擎都壞了」
/// ——<b>那是誤診</b>：<c>\d?</c> 只允許<b>一位</b>數字，<c>F10</c> 有兩位，回 false 是正確答案。
/// 兩邊引擎的答案其實一致且正確（見 §附錄的實測表）。真正的病是那個樣式寫錯了域碼的形狀。
/// 錯的診斷比錯的樣式更貴——它會讓後人不敢再用能動的工具。
/// </para>
///
/// <para>
/// 這一組<b>不</b>取代 <see cref="FirmwareDocReconcileTests"/>：那一組驗的是「階段二的實作進度
/// 申報常數與矩陣一致」（會隨實作推進而變動的數字），這一組驗的是「計畫庫自己的算術與慣例」
/// （不隨實作變動的結構）。兩者的紅燈訊息要能各自指出不同的病，所以刻意不共用解析器。
/// </para>
/// </summary>
public class PlanLibraryTests
{
    private const int StageCount = 10;
    private const int ItemsPerStage = 105;

    // ── 解析 ──────────────────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string PlanDir() => Path.Combine(RepoRoot(), "docs", "ULTIMATE-1000");

    /// <summary>階段檔：<c>STAGE-nn-*.md</c> 且不含 <c>REMAINING</c>（那是待辦清單，不是階段本體）。</summary>
    private static string StageFile(int stage)
    {
        var hits = Directory.GetFiles(PlanDir(), $"STAGE-{stage:00}-*.md")
            .Where(f => !Path.GetFileName(f).Contains("REMAINING", StringComparison.Ordinal))
            .ToArray();
        Assert.True(hits.Length == 1, $"階段 {stage} 的檔案不是恰好一份：{string.Join("、", hits.Select(Path.GetFileName))}");
        return hits[0];
    }

    /// <summary>把管線列切成欄位；不是管線列回空清單。</summary>
    private static List<string> Cells(string line)
    {
        string t = line.Trim();
        if (!t.StartsWith('|')) return [];
        return t.Trim('|').Split('|').Select(x => x.Trim()).ToList();
    }

    /// <summary>是不是項目 ID（<c>P&lt;階段&gt;-&lt;三位數&gt;</c>）——逐字元判斷，不用正則。</summary>
    private static bool IsItemId(string s, out int stage, out int number)
    {
        stage = number = 0;
        if (s.Length < 6 || s[0] != 'P') return false;
        int dash = s.IndexOf('-');
        if (dash < 2 || dash > 3) return false;
        string a = s[1..dash], b = s[(dash + 1)..];
        if (!a.All(char.IsAsciiDigit) || b.Length != 3 || !b.All(char.IsAsciiDigit)) return false;
        stage = int.Parse(a);
        number = int.Parse(b);
        return true;
    }

    /// <summary>
    /// 抓出欄位裡以反引號包起來的點分小寫鍵（形狀固定，逐字元掃描即可）。
    /// 刻意不吃 <c>（測試）</c> 這類佔位字——那是刻意的「這一項的產出是測試而不是事實鍵」。
    /// </summary>
    private static List<string> ExtractKeys(string cell)
    {
        var keys = new List<string>();
        int i = 0;
        while (true)
        {
            int open = cell.IndexOf('`', i);
            if (open < 0) break;
            int close = cell.IndexOf('`', open + 1);
            if (close < 0) break;
            string c = cell[(open + 1)..close];
            bool shaped = c.Length > 0 && char.IsAsciiLetterLower(c[0]) && c.Contains('.')
                && c.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '.' or '_' or '{' or '}' or '*');
            if (shaped) keys.Add(c);
            i = close + 1;
        }
        return keys;
    }

    private sealed record Domain(string Code, int Count, IReadOnlyList<string> Prefixes);

    private sealed record Row(string Id, int Number, string KeyCell, string Dep, IReadOnlyList<string> Keys);

    private sealed record Stage(int Number, string File, int ClaimedItems, IReadOnlyList<Domain> Domains, IReadOnlyList<Row> Rows);

    /// <summary>
    /// 檔頭宣稱的項數。字面形狀：<c>&gt; **本階段 105 項（P1-001–P1-105）**，域碼 10 個。</c>
    /// 抽成獨立函式是為了讓它自己被自我檢查驗到——第一版沒容許「105 項」之間的空格，
    /// 十個階段全部讀成 0，而<b>那十條紅燈看起來像文件壞了</b>。
    /// </summary>
    private static int ClaimedItems(string[] lines)
    {
        foreach (string line in lines.Take(40))
        {
            int at = line.IndexOf("本階段", StringComparison.Ordinal);
            if (at < 0) continue;
            int j = at + 3;
            while (j < line.Length && !char.IsAsciiDigit(line[j])) j++;
            int k = j;
            while (k < line.Length && char.IsAsciiDigit(line[k])) k++;
            int m = k;
            while (m < line.Length && line[m] == ' ') m++;
            if (k > j && m < line.Length && line[m] == '項') return int.Parse(line[j..k]);
        }
        return 0;
    }

    private static Stage Parse(int stage)
    {
        string path = StageFile(stage);
        string[] lines = File.ReadAllLines(path);
        int claimed = ClaimedItems(lines);

        // ── 域碼表（§三）：欄位數 < 5 的列不是域；域碼必須是 ASCII 英數
        //（這一條讓「合計」那一列不會被當成一個域——CJK 的 char.IsLetter 為 true，用 IsAsciiLetterOrDigit 才對）
        var domains = new List<Domain>();
        int start = Array.FindIndex(lines, l => l.StartsWith("## 三、域碼與項數", StringComparison.Ordinal));
        int end = start < 0 ? -1 : Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        for (int i = start + 1; start >= 0 && i < end; i++)
        {
            var c = Cells(lines[i]);
            if (c.Count < 5) continue;
            string code = c[0].Replace("**", "").Trim();
            if (code.Length is 0 or > 4 || !code.All(char.IsAsciiLetterOrDigit)) continue;
            string count = c[2].Replace("**", "").Trim();
            if (!count.All(char.IsAsciiDigit)) continue;
            var prefixes = c[3].Split(['／', '/', '、', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().Trim('`').TrimEnd('*'))
                .Where(p => p.Length > 0)
                .Select(p => p.EndsWith('.') ? p : p + ".")   // 階段四／九的域碼表寫裸前綴
                .ToList();
            domains.Add(new Domain(code, int.Parse(count), prefixes));
        }

        // ── 矩陣列（§五）：★ 錨記夾在管線與 ID 之間（`| ★P2-034 | …`），先去掉再判 ID
        var rows = new List<Row>();
        foreach (string line in lines)
        {
            if (line.Contains("---", StringComparison.Ordinal)) continue;
            var c = Cells(line);
            if (c.Count < 7) continue;
            string first = c[0].Trim().TrimStart('★');
            if (!IsItemId(first, out int st, out int no) || st != stage) continue;
            rows.Add(new Row(first, no, c[3], c[6], ExtractKeys(c[3])));
        }

        return new Stage(stage, Path.GetFileName(path), claimed, domains, rows);
    }

    // ── 稽核工具自我檢查 ──────────────────────────────────────────────────

    /// <summary>
    /// <b>稽核工具本身必須先被稽核。</b>每一項都寫成「答案錯了會被這條抓到」的形式——
    /// 這正是計畫裡「每個守門須有一次該紅會紅的證明」的自我套用。
    /// </summary>
    [Fact]
    public void 稽核工具自我檢查()
    {
        // ① 「★ 錨記」必須被認出來——這就是讓階段二被數成 102（而非 105）的那個坑
        Assert.Equal("P2-034", Cells("| ★P2-034 | 名稱 | 目標 | `k.1` | L0 | M | P2-001 |")[0].TrimStart('★'));
        Assert.True(IsItemId("★P10-001".TrimStart('★'), out int st10, out int n1) && st10 == 10 && n1 == 1,
            "P10-001（7 字元）必須被判為項目 ID——寫死長度 6 的版本會讓第十階段 105 列全部落空");
        Assert.False(IsItemId("F10", out _, out _), "域碼不是項目 ID");

        // ② 表頭／分隔列與資料列的欄數不同（尾端 | 的有無），門檻不能寫成 10
        Assert.True(Cells("| ID | 名稱 | 目標 | 鍵 | 敏感度 | 成本 | 依賴 | 實作鍵 |").Count >= 7);
        Assert.True(Cells("| P1-001 | 名稱 | 目標 | `k.1` | L0 | S | — |ok:k.1").Count >= 7);

        // ③ 鍵萃取只吃反引號裡的點分小寫鍵；佔位字不是鍵
        Assert.Equal(["ac.tables.summary"], ExtractKeys("列出全部表 | `ac.tables.summary` |"));
        Assert.Empty(ExtractKeys("解碼正確性以真機樣本證明 | （測試） |"));

        // ④ 本機的正則引擎沒有壞——`^[A-Z]+\d?$` 對 "F10" 回 false 是**正確**答案。
        //    這一條把 2026-10-16 那個誤診釘死在測試裡：錯的診斷比錯的樣式更貴。
        //    （F10 有兩位數字，而 \d? 只允許一位；會壞的是那個樣式，不是引擎。）
        Assert.DoesNotMatch("^[A-Z]+\\d?$", "F10");
        Assert.Matches("^[A-Z]+\\d*$", "F10");

        // ⑤ 檔頭宣稱的解析（含「105 項」之間的空格）；這條自己紅過一次，所以它必須在自我檢查裡
        Assert.Equal(105, ClaimedItems(["> **本階段 105 項（P1-001–P1-105）**，域碼 10 個。前置：無。"]));
        Assert.Equal(0, ClaimedItems(["> 沒有宣稱的一行"]));

        // ⑥ 輸入可達：恰好十份階段檔（第十一份是待辦清單，不算階段本體）
        int stages = Directory.GetFiles(PlanDir(), "STAGE-*.md")
            .Count(f => !Path.GetFileName(f).Contains("REMAINING", StringComparison.Ordinal));
        Assert.Equal(StageCount, stages);
    }

    // ── 結構算術 ──────────────────────────────────────────────────────────

    [Fact]
    public void 十階段的矩陣合計一千零五十列且_ID_互異()
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0;
        for (int s = 1; s <= StageCount; s++)
        {
            foreach (var row in Parse(s).Rows)
            {
                total++;
                Assert.False(seen.TryGetValue(row.Id, out int owner),
                    $"{row.Id} 同時出現在階段 {owner} 與階段 {s}——ID 是全域唯一的");
                seen[row.Id] = s;
            }
        }
        Assert.Equal(StageCount * ItemsPerStage, total);
        Assert.Equal(StageCount * ItemsPerStage, seen.Count);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void 每個階段的矩陣是_105_列且編號連續(int stage)
    {
        var rows = Parse(stage).Rows;
        Assert.Equal(ItemsPerStage, rows.Count);
        Assert.Equal(Enumerable.Range(1, ItemsPerStage), rows.Select(r => r.Number).OrderBy(n => n));
    }

    /// <summary>
    /// 檔頭寫「本階段 105 項」而矩陣少幾列，是最容易被忽略的漂移：**兩邊都是文件，沒有編譯器**。
    /// </summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void 檔頭宣稱的項數等於矩陣列數(int stage)
    {
        var s = Parse(stage);
        Assert.Equal(s.Rows.Count, s.ClaimedItems);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void 域碼表宣告的項數合計等於矩陣列數(int stage)
    {
        var s = Parse(stage);
        Assert.Equal(s.Rows.Count, s.Domains.Sum(d => d.Count));
    }

    /// <summary>
    /// 域碼表的項數只驗合計是不夠的：兩邊各錯一點會互相抵銷。
    /// 矩陣依域分塊、順序與域碼表一致，所以逐塊比對「這一塊的輸出鍵前綴是否落在該域宣告的前綴裡」——
    /// 這一條抓的是「某一項被放到隔壁域那個位置」的錯位（域項數的合計此時仍然成立）。
    /// </summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void 每個域的區塊與它宣告的輸出前綴對得上(int stage)
    {
        var s = Parse(stage);
        var bad = new List<string>();
        int cursor = 0;
        foreach (var d in s.Domains)
        {
            foreach (var row in s.Rows.Skip(cursor).Take(d.Count))
                foreach (string key in row.Keys)
                {
                    string root = key.Split('.')[0] + ".";
                    bool declared = d.Prefixes.Any(p => root.StartsWith(p, StringComparison.Ordinal)
                        || p.StartsWith(root, StringComparison.Ordinal));
                    if (!declared) bad.Add($"{d.Code}：{row.Id} 的 {key}（域宣告 {string.Join("、", d.Prefixes)}）");
                }
            cursor += d.Count;
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
        Assert.Equal(s.Rows.Count, cursor);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void 輸出鍵前綴必須在本階段的域碼表宣告內(int stage)
    {
        var s = Parse(stage);
        var declared = s.Domains.SelectMany(d => d.Prefixes).ToList();
        var bad = new List<string>();
        foreach (var row in s.Rows)
            foreach (string key in row.Keys)
            {
                string root = key.Split('.')[0] + ".";
                if (!declared.Any(p => root.StartsWith(p, StringComparison.Ordinal) || p.StartsWith(root, StringComparison.Ordinal)))
                    bad.Add($"{row.Id}：{key}");
            }
        Assert.True(bad.Count == 0,
            "輸出鍵的前綴不在任何域宣告內，讀者就無法用前綴定位域（補進域碼表，或改鍵名）：\n" + string.Join("\n", bad));
    }

    // ── 依賴欄 ────────────────────────────────────────────────────────────

    /// <summary>依賴欄裡的 ID 必須真的存在——指到不存在的項，是把讀者送去一個沒有東西的地方。</summary>
    [Fact]
    public void 依賴欄指到的項都要存在()
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        for (int s = 1; s <= StageCount; s++)
            foreach (var r in Parse(s).Rows) all.Add(r.Id);

        var bad = new List<string>();
        for (int s = 1; s <= StageCount; s++)
            foreach (var r in Parse(s).Rows)
                foreach (string id in IdsIn(r.Dep))
                    if (!all.Contains(id)) bad.Add($"{r.Id} -> {id}");
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    /// <summary>
    /// 依賴欄的合法語法是「ID（可多個，以、分隔）」，可選「參照：」前綴，
    /// 以及緊接在 ID 後的括號說明（例如 <c>P1-048（L3 禁項掃描守門）</c>）。
    /// 這一條把「依賴欄被當成自由文字寫」擋住——那種欄位會在對帳時安靜地消失。
    /// </summary>
    [Fact]
    public void 依賴欄只能寫_ID_或允許的佔位字()
    {
        var bad = new List<string>();
        for (int s = 1; s <= StageCount; s++)
            foreach (var r in Parse(s).Rows)
            {
                string d = r.Dep.Trim();
                if (d.Length == 0 || d is "—" or "-" or "無") continue;
                string rest = StripIds(d);       // 去掉 ID 與其後的括號說明
                rest = rest.Replace("參照：", "").Replace("參照", "")
                           .Replace("、", "").Replace(",", "").Replace("；", "").Trim();
                if (rest.Length > 0) bad.Add($"{r.Id} 依賴欄：{d}");
            }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    /// <summary>
    /// 依賴欄寫的是「建置前置」。跨階段的引用是另一件事，必須明示為「參照：Px-xxx」——
    /// 否則讀者會以為某一項要等五個階段之後的東西才能開工。
    /// </summary>
    [Fact]
    public void 跨階段的引用必須明示為參照()
    {
        var bad = new List<string>();
        int refs = 0;
        for (int s = 1; s <= StageCount; s++)
            foreach (var r in Parse(s).Rows)
                foreach (string id in IdsIn(r.Dep))
                {
                    IsItemId(id, out int other, out _);
                    if (other <= s) continue;
                    if (r.Dep.Contains("參照", StringComparison.Ordinal)) { refs++; continue; }
                    bad.Add($"{r.Id}（階段 {s}）-> {id}（階段 {other}）");
                }
        Assert.True(bad.Count == 0,
            "跨階段引用必須寫成「參照：Px-xxx」（依賴欄講的是建置前置）：\n" + string.Join("\n", bad));
        Assert.True(refs > 0, "一個跨階段參照都沒有——解析器的「參照」判讀可能壞了（它應該找到 35 條）");
    }

    // ── 私有工具 ──────────────────────────────────────────────────────────

    /// <summary>掃出字串裡所有 <c>P&lt;d&gt;-&lt;nnn&gt;</c>（逐字元，不用正則）。</summary>
    private static List<string> IdsIn(string text)
    {
        var ids = new List<string>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != 'P') continue;
            int j = i + 1;
            while (j < text.Length && char.IsAsciiDigit(text[j])) j++;
            if (j == i + 1 || j >= text.Length || text[j] != '-') continue;
            int k = j + 1;
            while (k < text.Length && char.IsAsciiDigit(text[k])) k++;
            if (k - (j + 1) != 3) continue;
            ids.Add(text[i..k]);
            i = k - 1;
        }
        return ids;
    }

    /// <summary>把 ID 以及緊接其後的括號說明去掉，剩下的才是「不是 ID 的東西」。</summary>
    private static string StripIds(string text)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == 'P')
            {
                var ids = IdsIn(text[i..]);
                if (ids.Count > 0 && text[(i + ids[0].Length)..].StartsWith('P') == false)
                {
                    i += ids[0].Length;
                    if (i < text.Length && (text[i] == '（' || text[i] == '('))
                    {
                        int close = text.IndexOfAny(['）', ')'], i);
                        if (close > 0) i = close + 1;
                    }
                    continue;
                }
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }
}
