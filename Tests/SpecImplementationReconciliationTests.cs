using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 規格矩陣與實作目錄的對帳守門（ULTIMATE-1000 階段二起）。
///
/// <para>
/// <b>這一組存在的理由，是四輪手工盤點的教訓：</b>
/// 我在階段二開工後連續四輪用「grep 鍵名」的方式查哪些項目做過了，
/// 每一輪都發現「規格宣告的鍵名與實作實際用的鍵名不同」——
/// 例如規格寫 <c>ac.table.{sig}</c> 而實作叫 <c>acpi.table.{slug}</c>。
/// 那不是四個獨立的錯，是同一個錯的四個實例：
/// <b>規格是先寫的，實作是後做的，而沒有人在實作時回頭對齊規格。</b>
/// </para>
///
/// <para>
/// 所以矩陣多了一個「實作鍵」欄，語意是機器可讀的：
/// <list type="bullet">
///   <item><c>ok:&lt;key&gt;</c>——規格鍵與實作鍵相同</item>
///   <item><c>alias:&lt;key&gt;</c>——語意相同、名字不同（實作真實用的鍵名）</item>
///   <item><c>todo:</c>——尚未實作</item>
/// </list>
/// 這一組測的就是「這一欄說的與目錄裡實際有的，是不是同一件事」。
/// </para>
/// </summary>
public class SpecImplementationReconciliationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// 矩陣列的辨識：<c>|</c> 之後允許可有可無的 <c>★</c> 再接 <c>P2-nnn</c>。
    ///
    /// <para>
    /// <b>為什麼不能寫成 <c>StartsWith("| P2-")</c>：</b>
    /// 矩陣裡有三列帶 <c>★</c> 錨記（P2-034／P2-036／P2-064），
    /// 星號夾在 <c>|</c> 與 <c>P2-</c> 之間，樸素的字串比對會<b>安靜地漏掉它們</b>——
    /// 數出 102 而不是 105，而且不會有任何錯誤訊息。
    /// </para>
    ///
    /// <para>
    /// <b>這件事真的發生過兩次：</b>一次是先前那份稽核報告（它因此宣稱
    /// 「1,050 是錯的」），一次是我自己（我因此一度以為 README 的結論要更正）。
    /// 兩次都是<b>工具壞掉被讀成文件壞掉</b>。
    /// </para>
    /// </summary>
    private static readonly Regex MatrixRow =
        new(@"^\|\s*★?\s*(P2-\d{3})\s*\|", RegexOptions.Compiled);

    /// <summary>
    /// 讀一份階段矩陣的「輸出鍵」與「實作鍵」兩欄。
    ///
    /// <para>
    /// <b>門檻是 9 不是 10。</b>資料列寫成 <c>| a | b | … | h |</c>——尾端沒有多一個
    /// <c>|</c>，所以 <c>Split('|')</c> 得到 9 段（第 0 段是空字串）。
    /// 表頭與分隔列有尾端 <c>|</c>，得到 10 段。
    /// 我第一版寫 <c>&lt; 10</c>，於是<b>全部 105 列都被濾掉</b>，數出 0——
    /// 而同一個錯誤在幾輪前讓我數出 102（那時門檻還沒加，是 <c>★</c> 那個缺陷）。
    /// </para>
    /// </summary>
    private const int MatrixColumnCount = 9;

    private static List<(string Id, string Declared, string Impl)> Rows(string stageFile)
    {
        string path = Path.Combine(RepoRoot(), "docs", "ULTIMATE-1000", stageFile);
        var rows = new List<(string, string, string)>();
        foreach (string line in File.ReadAllLines(path))
        {
            if (!MatrixRow.IsMatch(line)) continue;
            var cells = line.Split('|');
            // 形狀不對的列不靜默跳過——那是「工具壞掉被讀成文件壞掉」的起點。
            // 這一條由 每一列的欄位數都要與表頭一致 這個測試把關。
            if (cells.Length != MatrixColumnCount) continue;
            rows.Add((cells[1].Trim(), cells[4].Trim(), cells[8].Trim()));
        }
        return rows;
    }

    private static HashSet<string> CatalogKeys()
    {
        string path = Path.Combine(RepoRoot(), "Services", "FactKeyCatalog.cs");
        return Regex.Matches(File.ReadAllText(path), @"^\s*""([a-z0-9_.]+)"",\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> DynamicPrefixes()
    {
        string path = Path.Combine(RepoRoot(), "Services", "FactKeyDynamicCatalog.cs");
        return Regex.Matches(File.ReadAllText(path), @"\[""([a-z0-9_.]+)""\]\s*=")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>某個鍵樣式在目錄或動態家族裡找不找得到（<c>*</c> 與 <c>{x}</c> 視為家族）。</summary>
    private static bool Exists(string pattern, HashSet<string> catalog, HashSet<string> dynamic)
    {
        pattern = pattern.Trim().Trim('`');
        if (pattern.Length == 0 || pattern == "—" || pattern == "todo:") return false;

        if (pattern.Contains('*') || pattern.Contains('{'))
        {
            string family = pattern.Split('*')[0].Split('{')[0].TrimEnd('.');
            return catalog.Any(k => k.StartsWith(family + ".", StringComparison.Ordinal))
                || dynamic.Contains(family + ".")
                || dynamic.Contains(family);
        }
        return catalog.Contains(pattern);
    }

    // ── 主守門 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 「實作鍵」欄的一格——<b>唯一的解讀處</b>。
    ///
    /// <para>
    /// 這一欄的語法是 <c>ok:&lt;鍵&gt;</c>／<c>alias:&lt;鍵&gt;@&lt;出處&gt;</c>／<c>todo:</c>。
    /// 原本每一條測試各自 <c>Split</c> 這一格，於是當 alias 加上出處之後，
    /// <b>只有一半的消費者跟著改</b>——另一條測試把
    /// <c>cxl.cfmws.count@Services/CxlFactsService.cs:34</c> 當成鍵名去查目錄。
    /// </para>
    ///
    /// <para>
    /// 那正是「規格與實作漂移」同一種病，只是發生在程式碼內部：
    /// <b>兩處各自解讀同一份資料，就是兩個可以漂移的地方。</b>
    /// 所以拆解只留這一個地方。
    /// </para>
    /// </summary>
    private static (string Kind, string Key, string? Provenance) ParseEntry(string entry)
    {
        string e = entry.Trim();
        if (e.StartsWith("todo:", StringComparison.Ordinal)) return ("todo", "", null);

        (string kind, string rest) = e.StartsWith("ok:", StringComparison.Ordinal) ? ("ok", e[3..])
            : e.StartsWith("alias:", StringComparison.Ordinal) ? ("alias", e[6..])
            : ("unknown", e);

        int at = rest.IndexOf('@');
        return at < 0
            ? (kind, rest.Trim(), null)
            : (kind, rest[..at].Trim(), rest[(at + 1)..].Trim());
    }

    [Fact]
    public void 矩陣每一列的實作鍵都要在目錄裡找得到()
    {
        var catalog = CatalogKeys();
        var dynamic = DynamicPrefixes();
        var missing = new List<string>();

        foreach (var (id, declared, impl) in Rows("STAGE-02-FIRMWARE.md"))
        {
            foreach (string entry in impl.Split('／', StringSplitOptions.RemoveEmptyEntries))
            {
                var (kind, key, _) = ParseEntry(entry);
                if (kind == "todo") continue;
                if (!Exists(key, catalog, dynamic))
                    missing.Add($"{id}：矩陣說實作鍵是 {key}，但目錄與動態家族裡都找不到");
            }
        }

        Assert.True(missing.Count == 0,
            "規格矩陣的『實作鍵』欄與目錄不符——"
            + "這一欄是宣稱『這個鍵真的存在』，而它不存在：\n"
            + string.Join("\n", missing.Select(m => "  • " + m)));
    }

    /// <summary>
    /// 把一列的「規格鍵」與「實作鍵」<b>逐鍵配對</b>起來。
    ///
    /// <para>
    /// <b>為什麼不能只看「這一列有沒有 todo」：</b>一列可以有多把鍵，
    /// 而它們的進度可以不同——P2-029 是 <c>sb.tpmdev</c>（已完成）／
    /// <c>sb.proc_add</c>（未開工），實作鍵欄寫成 <c>ok:sb.tpmdev／todo:</c>。
    /// 舊版守門看到任一個 <c>todo:</c> 就拿**整列宣告的鍵**去查，
    /// 於是查到 <c>sb.tpmdev</c> 存在就誤報。
    /// </para>
    ///
    /// <para>
    /// 正確的判定是配對：第 i 個實作鍵對應第 i 個規格鍵。
    /// <b>「一列」不是原子單位，「一把鍵」才是。</b>
    /// </para>
    /// </summary>
    private static List<(string Id, string DeclaredKey, string Kind, string ImplKey)> Paired(string stageFile)
    {
        var pairs = new List<(string, string, string, string)>();
        foreach (var (id, declared, impl) in Rows(stageFile))
        {
            var declaredKeys = declared.Split('／', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim().Trim('`')).ToList();
            var entries = impl.Split('／', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => ParseEntry(e)).ToList();

            // 規格鍵帶範圍寫法（`sb.type0.*`–`sb.type3.*`）時兩邊數量會不等，
            // 那就退回「多對多」——但多對多的判定要用「有沒有任何一把存在」，
            // 而不是「每一把都不存在」。
            if (declaredKeys.Count == entries.Count)
            {
                for (int i = 0; i < entries.Count; i++)
                    pairs.Add((id, declaredKeys[i], entries[i].Kind, entries[i].Key));
            }
            else
            {
                foreach (var (kind, key, _) in entries)
                    pairs.Add((id, string.Join("／", declaredKeys), kind, key));
            }
        }
        return pairs;
    }

    [Fact]
    public void 標為todo的鍵必須真的不存在()
    {
        // 反向檢查：如果一個項目已經做完了卻還標 todo，
        // 那矩陣就在說謊——而說謊的方向是「少報了自己的完成度」。
        var catalog = CatalogKeys();
        var dynamic = DynamicPrefixes();
        var wrong = new List<string>();

        foreach (var (id, declaredKey, kind, _) in Paired("STAGE-02-FIRMWARE.md"))
        {
            if (kind != "todo") continue;
            if (declaredKey.Length == 0 || declaredKey == "—") continue;

            if (Exists(declaredKey, catalog, dynamic))
                wrong.Add($"{id}：矩陣把 {declaredKey} 標為 todo，但它已經在目錄裡了");
        }

        Assert.True(wrong.Count == 0,
            "矩陣標為未實作、但實作其實已經有了（說謊的方向是少報完成度）：\n"
            + string.Join("\n", wrong.Select(w => "  • " + w)));
    }

    /// <summary>
    /// <b>逐鍵配對的正對照。</b>
    ///
    /// <para>
    /// 這一條餵一個「第一把鍵存在、第二把不存在」的假列給配對邏輯，
    /// 要求它<b>不會把已完成的那一把報成 todo</b>。
    /// 沒有這一條，上面那條守門可以被一個「只要看到 todo 就整列報錯」的
    /// 寬鬆判定騙過去——而那個寬鬆判定正是它原本的樣子。
    /// </para>
    /// </summary>
    [Fact]
    public void 逐鍵配對_正對照_一半完成一半未做時不得誤報()
    {
        var catalog = new HashSet<string>(StringComparer.Ordinal) { "sb.done" };
        var dynamic = new HashSet<string>(StringComparer.Ordinal);

        // 模擬 P2-029：第一把完成、第二把未做。
        var declared = "`sb.done`／`sb.notdone`";
        var impl = "ok:sb.done／todo:";
        var declaredKeys = declared.Split('／').Select(d => d.Trim().Trim('`')).ToList();
        var entries = impl.Split('／').Select(e => ParseEntry(e.Trim())).ToList();

        Assert.Equal(declaredKeys.Count, entries.Count);

        var wrong = new List<string>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Kind != "todo") continue;
            if (Exists(declaredKeys[i], catalog, dynamic))
                wrong.Add(declaredKeys[i]);
        }

        Assert.Empty(wrong);   // 舊版會在這裡誤報 sb.done
        Assert.Equal("todo", entries[1].Kind);
        Assert.Equal("ok", entries[0].Kind);
    }

    [Fact]
    public void 實作鍵欄的每一格都要是合法語意()
    {
        var bad = new List<string>();
        foreach (var (id, declared, impl) in Rows("STAGE-02-FIRMWARE.md"))
        {
            if (impl.Length == 0) { bad.Add($"{id}：實作鍵欄是空的"); continue; }
            foreach (string entry in impl.Split('／', StringSplitOptions.RemoveEmptyEntries))
            {
                string e = entry.Trim();
                bool ok = e.StartsWith("ok:", StringComparison.Ordinal)
                       || e.StartsWith("alias:", StringComparison.Ordinal)
                       || e.StartsWith("todo:", StringComparison.Ordinal);
                if (!ok) bad.Add($"{id}：'{e}' 不是 ok:／alias:／todo: 之一");
            }
        }

        Assert.True(bad.Count == 0,
            "實作鍵欄有無法解讀的內容：\n" + string.Join("\n", bad.Select(b => "  • " + b)));
    }

    [Fact]
    public void alias的實作鍵必須與規格鍵不同_否則就該寫ok()
    {
        // alias 的意思是「名字不同」。若兩者相同還寫 alias，
        // 那這一欄就在暗示有一個翻譯層，而實際上沒有。
        var bad = new List<string>();
        foreach (var (id, declared, impl) in Rows("STAGE-02-FIRMWARE.md"))
        {
            foreach (string entry in impl.Split('／', StringSplitOptions.RemoveEmptyEntries))
            {
                var (kind, implKey, _) = ParseEntry(entry);
                if (kind != "alias") continue;
                foreach (string d in declared.Split('／', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (d.Trim().Trim('`') == implKey)
                        bad.Add($"{id}：alias 指向 {implKey}，但規格鍵也是它——應該寫 ok:");
                }
            }
        }

        Assert.True(bad.Count == 0, string.Join("\n", bad.Select(b => "  • " + b)));
    }

    /// <summary>
    /// <b>每一個 <c>alias:</c> 都必須附出處，而且那個出處必須真的存在。</b>
    ///
    /// <para>
    /// 這一組的分工是「<b>機器查證 todo、人工判斷 alias</b>」。機器那一半由其他測試釘住；
    /// 人工那一半——「這兩個鍵語意相同」——<b>不是機器能猜的判斷</b>，
    /// 所以它的唯一依據就是出處。出處若可以隨手寫，那個判斷就變成「我說了算」。
    /// </para>
    ///
    /// <para>
    /// 出處的格式是 <c>alias:&lt;實作鍵&gt;@&lt;相對路徑&gt;:&lt;行號&gt;</c>；
    /// 這裡驗三件事：格式對、檔案存在、行號在該檔案的範圍內。
    /// </para>
    /// </summary>
    [Fact]
    public void 每一個alias都要附出處_而且出處必須真的存在()
    {
        string root = RepoRoot();
        var bad = new List<string>();
        int aliases = 0;

        foreach (var (id, declared, impl) in Rows("STAGE-02-FIRMWARE.md"))
        {
            foreach (string entry in impl.Split('／', StringSplitOptions.RemoveEmptyEntries))
            {
                var (kind, key, provenance) = ParseEntry(entry);
                if (kind != "alias") continue;
                aliases++;

                if (provenance is null || provenance.Length == 0)
                {
                    bad.Add($"{id}：alias 沒有附出處（格式 alias:<鍵>@<路徑>:<行號>）");
                    continue;
                }

                int colon = provenance.LastIndexOf(':');
                if (colon < 0)
                {
                    bad.Add($"{id}：出處 '{provenance}' 缺少行號");
                    continue;
                }

                string rel = provenance[..colon];
                if (!int.TryParse(provenance[(colon + 1)..], out int lineNo) || lineNo <= 0)
                {
                    bad.Add($"{id}：出處的行號 '{provenance[(colon + 1)..]}' 不是正整數");
                    continue;
                }

                string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                {
                    bad.Add($"{id}：出處檔案不存在——{rel}");
                    continue;
                }

                int total = File.ReadAllLines(full).Length;
                if (lineNo > total)
                    bad.Add($"{id}：出處 {rel}:{lineNo} 超出檔案行數（該檔只有 {total} 行）");
            }
        }

        Assert.True(aliases > 0, "一個 alias 都沒有——這條測試失去意義，請確認矩陣是否還在用 alias");
        Assert.True(bad.Count == 0,
            "alias 的出處不成立——「語意相同」是人工判斷，而出處是它唯一的依據：\n"
            + string.Join("\n", bad.Select(b => "  • " + b)));
    }

    [Fact]
    public void 矩陣要有一百零五列()
    {
        // 階段二的文件宣告是 105 列（READER §一 的階段表與檔案開頭都寫 105）。
        var rows = Rows("STAGE-02-FIRMWARE.md");
        Assert.Equal(105, rows.Count);
        Assert.Equal(105, rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// <b>列數檢查的正對照。</b>
    ///
    /// <para>
    /// 沒有這一條，上面那個 <c>Assert.Equal(105, ...)</c> 可以被一個「什麼都認不出來」
    /// 的解析器騙過去——它會數出 0 然後紅燈，看起來像守門有在做事，
    /// 但真正的問題（解析器壞了）與症狀（列數不對）會被混為一談。
    /// </para>
    ///
    /// <para>
    /// 這一條直接餵三種寫法給解析器，要求它<b>三種都認得</b>：
    /// 樸素列、帶 <c>★</c> 的列、以及 <c>|</c> 後有空白的列。
    /// </para>
    /// </summary>
    [Fact]
    public void 列解析器_正對照_三種寫法都要認得()
    {
        Assert.Matches(MatrixRow, "| P2-001 | ACPI 表頭校驗和全查 | … |");
        Assert.Matches(MatrixRow, "| ★P2-034 | UEFI 映像 FV／FFS／區段樹 | … |");
        Assert.Matches(MatrixRow, "|  ★ P2-064 | TPM 開機鏈重放 | … |");

        // 反例：不該被當成矩陣列的
        Assert.DoesNotMatch(MatrixRow, "> **本階段 105 項（P2-001–P2-105）**");
        Assert.DoesNotMatch(MatrixRow, "| 項次 | 名稱 |");
        Assert.DoesNotMatch(MatrixRow, "### ★ P2-034｜UEFI 映像 FV／FFS／區段樹分解");
    }

    [Fact]
    public void 矩陣裡真的有三列帶星號錨記()
    {
        // 這一條把「★ 列存在」本身變成事實。
        // 如果哪天 ★ 被移除，這個測試會紅——而那時該做的是確認
        // 「是有人刻意改格式，還是有人不小心把錨記刪了」。
        string path = Path.Combine(RepoRoot(), "docs", "ULTIMATE-1000", "STAGE-02-FIRMWARE.md");
        int starred = File.ReadAllLines(path).Count(l => l.StartsWith("| ★P2-", StringComparison.Ordinal));

        Assert.True(starred == 3,
            $"階段二矩陣預期有 3 列帶 ★ 錨記（P2-034／P2-036／P2-064），實際 {starred} 列。"
            + "★ 是刻意標記（見 README §三 的錨記約定）；數目變了要確認是刻意改格式還是誤刪。");
    }

    /// <summary>
    /// <b>每一列的欄位數都要與表頭一致。</b>
    ///
    /// <para>
    /// 這一條比「列數等於 105」更根本，理由是這一輪學到的教訓：
    /// <b>總數可以因為兩個不同的錯誤互相抵消而看起來正常。</b>
    /// 我原本以為「102 列 ＋ 3 列缺欄位」，真相是「105 列全部 9 段，而我的門檻寫成 10」——
    /// 總數檢查對這種事一無所知，欄位數分布卻會立刻說出來。
    /// </para>
    ///
    /// <para>
    /// 它同時釘住「資料列 9 段、表頭 10 段」這個事實——
    /// 那是我連續踩三次坑的那個細節（報告一次、我兩次）。
    /// </para>
    /// </summary>
    [Fact]
    public void 每一列的欄位數都要與表頭一致()
    {
        string path = Path.Combine(RepoRoot(), "docs", "ULTIMATE-1000", "STAGE-02-FIRMWARE.md");
        var lines = File.ReadAllLines(path);

        int headerCells = lines.First(l => l.StartsWith("| ID |", StringComparison.Ordinal)).Split('|').Length;
        Assert.Equal(10, headerCells);   // 表頭有尾端 |，所以是 10 段

        var odd = new List<string>();
        int dataRows = 0;
        foreach (string line in lines)
        {
            if (!MatrixRow.IsMatch(line)) continue;
            dataRows++;
            int n = line.Split('|').Length;
            if (n != MatrixColumnCount)
                odd.Add($"{line.Split('|')[1].Trim()}：{n} 段（應為 {MatrixColumnCount}）");
        }

        Assert.Equal(105, dataRows);
        Assert.True(odd.Count == 0,
            "這些矩陣列的欄位數與其他列不同——"
            + "欄位數不一致時，任何以欄位位置取值的檢查都會取到錯的東西：\n"
            + string.Join("\n", odd.Select(o => "  • " + o)));
    }
}
