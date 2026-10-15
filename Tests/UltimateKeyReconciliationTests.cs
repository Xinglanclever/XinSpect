using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 的輸出鍵對帳：<b>規格矩陣宣告的鍵必須真的有人生產。</b>
///
/// <para>
/// <b>為什麼要有這一條守門（真實缺陷）：</b>2026-10-16 推進階段一時發現
/// <c>state.lattice</c>（P1-014）在矩陣裡宣告、實作（<c>FactStateLattice</c>）也早就存在
/// 且有 20 條測試，但<b>全 repo 沒有任何地方生產這把鍵</b>——同一批還有
/// <c>state.count</c>、<c>fact.readerror.reason</c>、<c>fact.why_unreadable</c>。
/// </para>
///
/// <para>
/// 既有的 <c>FactKeyCatalogTests</c> 抓不到這件事：它比對的是
/// 「原始碼掃到的鍵 ↔ 目錄」，<b>掃不到就不會報</b>。規格宣告了一把鍵、實作也做了、
/// 只是沒接線——這種缺陷在「目錄 ≡ 原始碼」這個視角下完全隱形。
/// </para>
///
/// <para>
/// <b>等級宣告</b>：擋得住「矩陣宣告了鍵但沒人生產」；
/// 擋不住「生產了但使用者看不到」（那需要呈現層的另一組對帳，屬階段九）。
/// </para>
/// </summary>
public class UltimateKeyReconciliationTests
{
    /// <summary>
    /// 矩陣宣告了、但**這一輪還沒開工**的鍵。鍵是鍵名，值是理由與預計實作的項目。
    /// <b>每一條都要指名是哪一項還沒做</b>，不能只寫「待辦」。
    ///
    /// <para>
    /// 這裡刻意<b>不</b>逐鍵列出那 971 把未開工的鍵——那會變成一份和矩陣一樣長的
    /// 手抄清單，而手抄清單一定會漂。改以<b>階段–項目編號範圍</b>表達：
    /// 「哪些項目已經開工、哪些還沒」。這樣清單短、可稽核，而且新增項目時
    /// 只要把範圍往前推一格。
    /// </para>
    /// </summary>
    private static readonly List<(string Range, string Reason)> NotYetImplementedRanges =
    [
        ("P1-033", "知識包框架與匿名化——階段一後半段，尚未開工"),
    ];

    /// <summary>已開工的項目（其宣告的鍵必須有生產者）。範圍含頭含尾，以項目編號認定。</summary>
    private static readonly List<(string From, string To)> InProgressRanges =
    [
        ("P1-001", "P1-032"),
    ];

    /// <summary>
    /// 永遠不會有生產者的鍵：性質上就該如此（例如純測試級的項）。
    /// 鍵是鍵名，值是理由。
    /// </summary>
    private static readonly Dictionary<string, string> NeverProduced = new(StringComparer.Ordinal)
    {
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>從 ULTIMATE 矩陣的「輸出鍵」欄抓出鍵（反引號內的點分小寫字串）。</summary>
    private static List<(string Id, string Key)> MatrixKeys(string root)
    {
        var result = new List<(string, string)>();
        string dir = Path.Combine(root, "docs", "ULTIMATE-1000");
        Assert.True(Directory.Exists(dir), $"找不到 {dir}");

        foreach (string file in Directory.GetFiles(dir, "STAGE-*.md"))
        {
            string stage = Regex.Match(Path.GetFileName(file), @"STAGE-(\d+)").Groups[1].Value;
            string prefix = $"P{int.Parse(stage)}-";
            foreach (string line in File.ReadAllLines(file))
            {
                string t = line.Trim();
                if (!t.StartsWith('|')) continue;
                var c = t.Trim('|').Split('|').Select(x => x.Trim()).ToList();
                if (c.Count < 7 || t.Contains("---")) continue;
                string id = c[0].TrimStart('★');
                if (!id.StartsWith(prefix, StringComparison.Ordinal) || id.Length != prefix.Length + 3) continue;
                foreach (Match m in Regex.Matches(c[3], @"`([a-z][a-z0-9_.{}*]*)`"))
                    result.Add((id, m.Groups[1].Value));
            }
        }
        return result;
    }

    [Fact]
    public void 矩陣宣告的每一把輸出鍵都必須有人生產()
    {
        string root = RepoRoot();
        var declared = MatrixKeys(root);
        Assert.True(declared.Count > 0, "矩陣裡一把鍵都抓不到——解析或路徑怪了，這條守門就失效了");

        // 「有人生產」的認定：鍵（或其動態家族樣式）出現在 Services/ 的原始碼字串裡。
        // 用既有的掃描器，確保與 FactKeyCatalogTests 同一口徑。
        var produced = CoverageService.ScanFactKeysFromSource(Path.Combine(root, "Services"))
            .ToHashSet(StringComparer.Ordinal);

        // 只檢查「已開工」的項目。未開工的項目其鍵當然沒有生產者——
        // 把它們一起要求會讓這條守門從第一天起就是 971 條紅燈，那樣沒有人會看它。
        var inScope = declared.Where(d => InProgress(d)).ToList();
        Assert.True(inScope.Count > 0, "沒有任何項目落在已開工範圍內——範圍設定壞了，這條守門就失效了");

        var missing = inScope
            .Where(d => !IsProduced(d.Key, produced))
            .Select(d => $"{d.Id} 的 `{d.Key}`")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            $"ULTIMATE 矩陣在已開工範圍內宣告了 {missing.Count} 把沒有生產者的輸出鍵——"
            + "這是「規格有、實作有、沒接線」，目錄守門看不到它：\n"
            + string.Join("\n", missing.Select(m => "  • " + m))
            + $"\n二選一：(a) 補上產生器；(b) 把該項目移出已開工範圍 {DescribeRanges()}。");
    }

    private static string DescribeRanges() =>
        string.Join("、", InProgressRanges.Select(r => $"{r.From}–{r.To}"));

    private static bool InProgress((string Id, string Key) item) =>
        InProgressRanges.Any(r =>
            string.CompareOrdinal(item.Id, r.From) >= 0 && string.CompareOrdinal(item.Id, r.To) <= 0);

    /// <summary>
    /// 矩陣的鍵是不是有生產者。
    /// 動態家族（<c>spd.{n}.vendor</c>）以「前綴在原始碼出現過」認定——
    /// 逐成員列舉是不可能的，因為成員由機器事實決定。
    /// </summary>
    private static bool IsProduced(string key, HashSet<string> produced)
    {
        if (produced.Contains(key)) return true;

        // 動態鍵：把 {..} 與 * 之前的部分當前綴，看有沒有任何掃到的鍵落在它下面。
        int brace = key.IndexOfAny(['{', '*']);
        string stem = brace >= 0 ? key[..brace] : key;
        if (brace >= 0)
            return produced.Any(p => p.StartsWith(stem, StringComparison.Ordinal));

        // 靜態鍵：允許以相同前綴的家族成員代替（例如 `spd.0.vendor` 由 `spd.{n}.vendor` 家族覆蓋）
        return false;
    }

    [Fact]
    public void 未開工範圍的每一條都要寫出理由()
    {
        foreach (var (range, reason) in NotYetImplementedRanges)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{range}：必須寫出理由");
            Assert.Matches(@"P\d{1,2}-\d{3}", range);
            Assert.True(reason.Length >= 10, $"{range}：理由太短");
        }
    }

    [Fact]
    public void 已開工範圍要合法且不與未開工範圍重疊()
    {
        foreach (var (from, to) in InProgressRanges)
        {
            Assert.Matches(@"P\d{1,2}-\d{3}", from);
            Assert.Matches(@"P\d{1,2}-\d{3}", to);
            Assert.True(string.CompareOrdinal(from, to) <= 0, $"{from}–{to}：範圍顛倒");
        }
        // 兩個範圍清單若重疊，就會出現「同一項既算已開工又算未開工」的矛盾。
        foreach (var (from, to) in InProgressRanges)
        foreach (var (nyi, reason) in NotYetImplementedRanges)
            Assert.False(string.CompareOrdinal(from, nyi) <= 0 && string.CompareOrdinal(nyi, to) <= 0,
                $"{nyi} 同時落在已開工範圍 {from}–{to} 與未開工清單（{reason}）——兩者只能擇一");
    }

    [Fact]
    public void 永不生產清單的每一條都要寫出理由()
    {
        foreach (var (key, reason) in NeverProduced)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{key}：必須寫出為什麼它永遠不會有生產者");
            Assert.True(reason.Length >= 10, $"{key}：理由太短");
        }
    }
}
