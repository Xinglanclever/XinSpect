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
                string e = entry.Trim();
                if (e.StartsWith("todo:", StringComparison.Ordinal)) continue;
                string key = e.StartsWith("ok:", StringComparison.Ordinal) ? e[3..]
                           : e.StartsWith("alias:", StringComparison.Ordinal) ? e[6..]
                           : e;
                if (!Exists(key, catalog, dynamic))
                    missing.Add($"{id}：矩陣說實作鍵是 {key}，但目錄與動態家族裡都找不到");
            }
        }

        Assert.True(missing.Count == 0,
            "規格矩陣的『實作鍵』欄與目錄不符——"
            + "這一欄是宣稱『這個鍵真的存在』，而它不存在：\n"
            + string.Join("\n", missing.Select(m => "  • " + m)));
    }

    [Fact]
    public void 標為todo的鍵必須真的不存在()
    {
        // 反向檢查：如果一個項目已經做完了卻還標 todo，
        // 那矩陣就在說謊——而說謊的方向是「少報了自己的完成度」。
        var catalog = CatalogKeys();
        var dynamic = DynamicPrefixes();
        var wrong = new List<string>();

        foreach (var (id, declared, impl) in Rows("STAGE-02-FIRMWARE.md"))
        {
            foreach (string entry in impl.Split('／', StringSplitOptions.RemoveEmptyEntries))
            {
                string e = entry.Trim();
                if (!e.StartsWith("todo:", StringComparison.Ordinal)) continue;

                // todo 的鍵取自規格宣告那一欄
                foreach (string d in declared.Split('／', StringSplitOptions.RemoveEmptyEntries))
                {
                    string key = d.Trim().Trim('`');
                    if (key.Length == 0 || key == "—") continue;
                    if (Exists(key, catalog, dynamic))
                        wrong.Add($"{id}：矩陣標 todo，但 {key} 已經在目錄裡了");
                }
            }
        }

        Assert.True(wrong.Count == 0,
            "矩陣標為未實作、但實作其實已經有了（說謊的方向是少報完成度）：\n"
            + string.Join("\n", wrong.Select(w => "  • " + w)));
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
                string e = entry.Trim();
                if (!e.StartsWith("alias:", StringComparison.Ordinal)) continue;
                string implKey = e[6..];
                foreach (string d in declared.Split('／', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (d.Trim().Trim('`') == implKey)
                        bad.Add($"{id}：alias 指向 {implKey}，但規格鍵也是它——應該寫 ok:");
                }
            }
        }

        Assert.True(bad.Count == 0, string.Join("\n", bad.Select(b => "  • " + b)));
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
