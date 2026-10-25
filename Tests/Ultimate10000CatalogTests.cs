using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-10000 候選能力目錄（<c>docs/ULTIMATE-10000/</c>）的算術守門。
///
/// <para>
/// 形狀抄 <see cref="PlanLibraryTests"/>：讀 repo 根相對路徑、用固定形狀的正則抽列、
/// 斷言失敗時訊息要說人話（指出第一個斷點、給出實測數字，而不是丟一句「assert failed」）。
/// </para>
///
/// <para>
/// <b>以「ID 定義列」為準。</b>主目錄裡同一個 ID 會在「批次總帳」與示例文字中被引用多次
/// （例如「本批合計 822 項，自 U10-CP-0788 起」）——那些引用不是條目。
/// 條目只認表格中以 <c>| U10-… |</c> 開頭的<b>定義列</b>（見 <see cref="DefinitionRowRegex"/>）。
/// </para>
///
/// <para>
/// 閾值的依據（2026-10-25 對 1.1MB 主目錄實測）：定義列 10,022 條、去重後 10,022 個 ID、
/// 39 個域全部自 0001 起連續、無鍵列 17 條（0.17%，全部是 U10-NX「不做清單」——
/// 那個域的第三欄本來就寫例外條件而非候選鍵）。總數下限用 ≥ 10,000 而不寫死 10,022，
/// 是給日後增補留成長餘地；超過下限的具體數字會印在失敗訊息裡。
/// </para>
/// </summary>
public class Ultimate10000CatalogTests
{
    /// <summary>條目 ID 的形狀：<c>U10-兩碼大寫域碼-四碼數字</c>。刻意不用 <c>\d{4,}</c> 之類的寬鬆量詞。</summary>
    private const string IdPattern = @"U10-[A-Z]{2}-\d{4}";

    private static readonly Regex IdRegex = new(IdPattern, RegexOptions.Compiled);

    /// <summary>
    /// ID 定義列：表格中以 <c>| U10-… |</c> 開頭的列（開頭錨定＋兩側管線都要求）。
    /// 批次總帳與示例中的引用出現在散文裡、不在列首管線內，天然不會被這條抓到。
    /// </summary>
    private static readonly Regex DefinitionRowRegex =
        new(@"^\|\s*(?<id>" + IdPattern + @")\s*\|", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>無鍵列佔比的門檻：5%。2026-10-25 實測 0.17%（17/10,022），餘裕極大。</summary>
    private const double MaxKeylessRatio = 0.05;

    // ── 解析 ──────────────────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string MainCatalogPath() =>
        Path.Combine(RepoRoot(), "docs", "ULTIMATE-10000", "ULTIMATE-10000-EXPANSION-2026-10-24.md");

    private sealed record DefinitionRow(string Id, string Domain, int Number, string Line);

    /// <summary>逐列掃出全部 ID 定義列（保持文件內順序，供連續性檢查用）。</summary>
    private static List<DefinitionRow> DefinitionRows(string text)
    {
        var rows = new List<DefinitionRow>();
        foreach (string line in text.Split('\n'))
        {
            var m = DefinitionRowRegex.Match(line);
            if (!m.Success) continue;
            string id = m.Groups["id"].Value;
            rows.Add(new DefinitionRow(id, id[4..6], int.Parse(id[7..]), line.TrimEnd('\r')));
        }
        return rows;
    }

    /// <summary>
    /// 抓出列中反引號包起來、含點分的東西（候選鍵的最低共同形狀）。
    /// 刻意不做更細的字元集過濾：本目錄的鍵合法地含有大寫、<c>@</c>、<c>,</c>、<c>+</c> 等
    /// （例如 <c>dmt.640x350@85</c>、<c>ts.cpu.fp64.&#123;st,mt&#125;</c>、<c>dmt.1792+1856@60</c>）——
    /// 第一版照抄 PlanLibraryTests 的小寫點分形狀，把 566 條有鍵列誤判成無鍵（這條錯被自我檢查式的實測抓到）。
    /// 「反引號內含一個點」就是本目錄「有候選鍵」的實測定義：2026-10-25 實測下無鍵列恰為 17 條、全部在 U10-NX。
    /// </summary>
    private static List<string> ExtractKeys(string line)
    {
        var keys = new List<string>();
        int i = 0;
        while (true)
        {
            int open = line.IndexOf('`', i);
            if (open < 0) break;
            int close = line.IndexOf('`', open + 1);
            if (close < 0) break;
            string c = line[(open + 1)..close];
            if (c.Length > 1 && c.Contains('.')) keys.Add(c);
            i = close + 1;
        }
        return keys;
    }

    // ── 結構驗算 ──────────────────────────────────────────────────────────

    [Fact]
    public void 主目錄檔案存在且非空()
    {
        string path = MainCatalogPath();
        Assert.True(File.Exists(path), $"主目錄不在預期位置：{path}");
        var fi = new FileInfo(path);
        Assert.True(fi.Length > 0, $"主目錄是空檔案：{path}");
        Assert.True(fi.Length > 500_000, $"主目錄只有 {fi.Length} bytes——比 2026-10-25 納管時的 1,056,427 bytes 小太多，疑似被截斷");
    }

    [Fact]
    public void 條目總數去重後不少於一萬()
    {
        // 只數定義列；批次總帳與示例中的 ID 引用不算條目（同一 ID 會被引用多次）。
        // 2026-10-25 實測：定義列 10,022、去重後 10,022（全文件無重號）。下限 10,000 留成長餘地。
        string text = File.ReadAllText(MainCatalogPath());
        var rows = DefinitionRows(text);
        int distinct = rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count();

        Assert.True(distinct >= 10_000,
            $"主目錄去重後只有 {distinct} 個 ID（定義列 {rows.Count} 條），低於下限 10,000。" +
            "納管時的實測值是 10,022——如果不是刻意增補後回歸，就是目錄被截斷或解析壞了。");

        // 定義列自身不應有重號（重號的另一種病：同一列被複製貼上）。
        Assert.True(rows.Count == distinct,
            $"定義列 {rows.Count} 條但去重後只剩 {distinct} 個 ID——有定義列重號：" +
            string.Join("、", rows.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => $"{g.Key}×{g.Count()}").Take(10)));
    }

    [Fact]
    public void 每個域內編號從0001起連續無跳號無重號()
    {
        string text = File.ReadAllText(MainCatalogPath());
        var byDomain = DefinitionRows(text)
            .GroupBy(r => r.Domain, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        Assert.True(byDomain.Count == 39,
            $"域數是 {byDomain.Count}，不是 39——2026-10-25 納管時主目錄有 39 個域" +
            $"（實際域碼：{string.Join("、", byDomain.Select(g => g.Key))}）");

        var problems = new List<string>();
        foreach (var g in byDomain)
        {
            var numbers = g.Select(r => r.Number).OrderBy(n => n).ToList();

            if (numbers[0] != 1)
                problems.Add($"{g.Key}：第一號是 {numbers[0]:0000}，不從 0001 起");
            if (numbers.Count != numbers.Distinct().Count())
                problems.Add($"{g.Key}：有重號（{string.Join("、", numbers.GroupBy(n => n).Where(x => x.Count() > 1).Select(x => x.Key).Take(5))}）");

            // 只報第一個斷點：一個域跳十個號會生出十條訊息，第一條才是要修的。
            for (int i = 1; i < numbers.Count; i++)
            {
                if (numbers[i] != numbers[i - 1] + 1)
                {
                    problems.Add($"{g.Key}：編號在 {numbers[i - 1]:0000} → {numbers[i]:0000} 處斷開（第一個斷點；該域共 {numbers.Count} 條）");
                    break;
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void 定義列都有候選鍵或明確的例外()
    {
        // 每條定義列的同一列上要有一個反引號候選鍵；沒有的視為「明確例外」（如 U10-NX 不做清單，
        // 第三欄寫的是例外條件而非鍵）。無鍵列 > 5% 才失敗——2026-10-25 實測 17/10,022 ≈ 0.17%，
        // 且 17 條全部是 U10-NX，故一併釘死「無鍵列只允許出現在 NX 域」。
        string text = File.ReadAllText(MainCatalogPath());
        var rows = DefinitionRows(text);
        var keyless = rows.Where(r => ExtractKeys(r.Line).Count == 0).ToList();

        double ratio = rows.Count == 0 ? 1.0 : (double)keyless.Count / rows.Count;
        Assert.True(ratio <= MaxKeylessRatio,
            $"無鍵定義列 {keyless.Count}/{rows.Count} 條（{ratio:P1}），超過門檻 {MaxKeylessRatio:P0}。" +
            $"納管時實測 17/10,022（0.17%）。樣本：{string.Join("、", keyless.Take(5).Select(r => r.Id))}");

        var nonNx = keyless.Where(r => r.Domain != "NX").ToList();
        Assert.True(nonNx.Count == 0,
            "無鍵列只允許出現在 U10-NX（不做清單——第三欄寫的是例外條件，本來就沒有候選鍵）。" +
            "以下域出現了無鍵列，若不是刻意例外，就是那一列漏寫了候選鍵：\n" +
            string.Join("\n", nonNx.Select(r => $"{r.Id}：{r.Line[..Math.Min(120, r.Line.Length)]}")));
    }

    /// <summary>
    /// <b>驗算工具自己也要被驗</b>（PlanLibraryTests 的教訓：第一版解析把十個階段全讀成 0，
    /// 十條紅燈看起來像文件壞了，其實是解析壞了）。這一條用人造樣本釘死抽取正則的行為。
    /// </summary>
    [Fact]
    public void 解析器自我檢查()
    {
        const string sample = """
            # 人造樣本
            批次總帳引用不算定義列：本批自 U10-CP-0788 起，另參照 U10-MS-0001。
            | U10-CP-0001 | 最大基本葉值 | `cpuid.leaf.basic.max` |
            | U10-XX-0012 | 域碼兩碼也算 | `x.y` |
            | U10-CP-13 | 四碼不足——不是定義列 | `a.b` |
            | U10-CP-0002 | 相鄰定義列 | `cpu.brand.raw` |
            |U10-NV-0999|無空白的緊湊列也是定義列|`nvme.id.ctrl`|
            u10-cp-0003 小寫不算。U10-CPX-0001 域碼三碼不算。
            """;

        // ① 定義列抽取：散文引用、四碼不足、小寫、三碼域碼都不算；緊湊無空白的表格列要算。
        var ids = DefinitionRows(sample).Select(r => r.Id).ToList();
        Assert.Equal(new[] { "U10-CP-0001", "U10-XX-0012", "U10-CP-0002", "U10-NV-0999" }, ids);

        // ② 引用計數用 IdRegex：散文裡的兩個引用要被認出來（它們只是不進條目帳）。
        Assert.Equal(2, IdRegex.Matches("本批自 U10-CP-0788 起，另參照 U10-MS-0001。").Count);

        // ③ 形狀邊界：兩位數編號、三碼域碼、小寫都不是合法 ID。
        Assert.False(IdRegex.IsMatch("U10-CP-13"), "兩位數編號不該被當成四碼 ID");
        Assert.False(IdRegex.IsMatch("U10-CPX-0001"), "域碼恰兩碼，三碼不該命中");
        Assert.False(IdRegex.IsMatch("u10-cp-0001"), "ID 是大寫的");

        // ④ 鍵萃取：只吃反引號裡點分鍵；形狀不符的不算數。
        //    本目錄的鍵合法地含大寫與 @ 等（VC 域全是這種），第一版的小寫過濾曾把 566 條誤判成無鍵——釘死在這裡。
        Assert.Equal(["cpuid.leaf.basic.max"], ExtractKeys("| U10-CP-0001 | 最大基本葉值 | `cpuid.leaf.basic.max` |"));
        Assert.Equal(["dmt.640x350@85"], ExtractKeys("| U10-VC-0001 | DMT 640x350@85 | `dmt.640x350@85` |"));
        Assert.Empty(ExtractKeys("| U10-CP-0004 | 沒有鍵 | 使用者明示要求 |"));

        // ⑤ 編號切欄：Id 的第 7 位起是四碼數字。
        var row = DefinitionRows("| U10-SB-1234 | 名稱 | `s.t` |").Single();
        Assert.Equal("SB", row.Domain);
        Assert.Equal(1234, row.Number);
    }

    [Fact]
    public void 前導文件已歸檔在側()
    {
        string path = Path.Combine(RepoRoot(), "docs", "ULTIMATE-10000", "ULTIMATE-5000-RESERVOIR-2026-10-24.md");
        Assert.True(File.Exists(path), $"前導文件（已歸檔）不在預期位置：{path}");
    }
}
