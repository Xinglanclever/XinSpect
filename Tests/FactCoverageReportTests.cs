using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 事實鍵對帳覆蓋的申報（規則覆蓋測試）。這一組不要求「全覆蓋」——那會逼出湊數的規則。
/// 它要保證的是三件事：①每個鍵都被分類到三種狀態之一；②豁免都寫得出理由；
/// ③未覆蓋的數字是<b>已知的</b>，而且會隨事實增加而變動時被看見。
/// </summary>
public class FactCoverageReportTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Rules")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>從程式碼掃出事實鍵（只抓「事實鍵的位置」，不是所有看起來像路徑的字串）。</summary>
    private static HashSet<string> ScanFactKeys()
    {
        var patterns = new[]
        {
            new Regex(@"new HardwareFact\(\s*\$?""([a-z][a-z0-9_.\[\]-]*)"""),
            new Regex(@"new\(\s*\$?""([a-z][a-z0-9_.\[\]-]*)""\s*,\s*(?:Category|\$?_?cat)"),
            new Regex(@"(?:const|static readonly)\s+string\s+\w*[Kk]ey\w*\s*=\s*""([a-z][a-z0-9_.\[\]-]*)"""),
            new Regex(@"FactKey\s*=\s*""([a-z][a-z0-9_.\[\]-]*)"""),
        };
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string services = Path.Combine(RepoRoot(), "Services");
        foreach (string file in Directory.EnumerateFiles(services, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string text = File.ReadAllText(file);
            foreach (var p in patterns)
                foreach (Match m in p.Matches(text))
                {
                    string key = m.Groups[1].Value;
                    // 以 . 結尾的是插值鍵的前綴（例如 "asset.field."）——保留，它代表一整族
                    keys.Add(key.TrimEnd('.'));
                }
        }
        return keys;
    }

    private static List<(string Id, IReadOnlyList<string> Inputs)> LoadRules()
    {
        string json = File.ReadAllText(Path.Combine(RepoRoot(), "Rules", "builtin.json"));
        using var doc = JsonDocument.Parse(json);
        var list = new List<(string, IReadOnlyList<string>)>();
        foreach (var r in doc.RootElement.GetProperty("rules").EnumerateArray())
        {
            string id = r.GetProperty("id").GetString() ?? "";
            var inputs = r.GetProperty("inputs").EnumerateArray()
                .Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            list.Add((id, inputs));
        }
        return list;
    }

    // ── 分類的完整性 ──────────────────────────────────────────────────────

    [Fact]
    public void 掃到的事實鍵要有意義的數量()
    {
        // 掃描本身若壞掉（正則失效、路徑錯），會得到 0 或極少數——
        // 那會讓下面每一條測試都「通過」，所以先把掃描釘住。
        var keys = ScanFactKeys();
        Assert.True(keys.Count >= 100, $"只掃到 {keys.Count} 個事實鍵——掃描邏輯可能失效了");
    }

    [Fact]
    public void 每個鍵都被分類到三種狀態之一()
    {
        var keys = ScanFactKeys();
        var rules = LoadRules();
        var ruleInputs = rules.SelectMany(r => r.Inputs).ToHashSet(StringComparer.Ordinal);

        foreach (string key in keys)
        {
            var c = FactCoverageReport.Classify(key, ruleInputs);
            Assert.True(Enum.IsDefined(c), $"{key} 沒有被分類");
        }
    }

    [Fact]
    public void 申報的加總要等於總數()
    {
        var s = FactCoverageReport.Report(ScanFactKeys(), LoadRules());
        Assert.Equal(s.Total, s.Covered + s.Exempt + s.Uncovered);
        Assert.Equal(s.Uncovered, s.UncoveredKeys.Count);
    }

    [Fact]
    public void 未覆蓋的鍵要排序且不重複()
    {
        var s = FactCoverageReport.Report(ScanFactKeys(), LoadRules());
        Assert.Equal(s.UncoveredKeys.OrderBy(x => x, StringComparer.Ordinal), s.UncoveredKeys);
        Assert.Equal(s.UncoveredKeys.Count, s.UncoveredKeys.Distinct().Count());
    }

    // ── 豁免的品質 ────────────────────────────────────────────────────────

    [Fact]
    public void 每一條豁免都要寫得出理由()
    {
        var bad = FactCoverageReport.ExemptionsWithoutReasons();
        Assert.True(bad.Count == 0,
            "以下豁免沒有寫出理由（沒有理由的豁免等於沒豁免）：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void 豁免不得是空字串前綴_那會豁免掉全部()
    {
        Assert.DoesNotContain("", FactCoverageReport.Exemptions.Keys);
    }

    [Fact]
    public void 豁免的前綴要真的用得到()
    {
        // 沒用到的豁免會讓申報看起來比實際乾淨——那是另一種形式的說謊
        var keys = ScanFactKeys();
        foreach (string prefix in FactCoverageReport.Exemptions.Keys)
            Assert.Contains(keys, k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    // ── 未覆蓋的數字必須被看見 ────────────────────────────────────────────

    [Fact]
    public void 申報措辭不得說成通過或失敗()
    {
        var s = FactCoverageReport.Report(ScanFactKeys(), LoadRules());
        Assert.Contains("尚未被任何規則碰到", s.Headline);
        Assert.DoesNotContain("通過", s.Headline);
        Assert.DoesNotContain("失敗", s.Headline);
    }

    [Fact]
    public void 未覆蓋數超過已知基線時_列出前幾個供人核對()
    {
        // 這條不是禁止未覆蓋（那會逼出湊數的規則），而是讓它「有上限地增長」：
        // 事實從 300 多個長到某個程度時要有人看一眼，而不是靜默累積。
        var s = FactCoverageReport.Report(ScanFactKeys(), LoadRules());
        Assert.True(s.Uncovered <= 220,
            $"未覆蓋的事實鍵增至 {s.Uncovered} 個（基線 220）。新增事實時請順手評估要不要納入對帳，"
            + $"或加進 FactCoverageReport.Exemptions 並寫明理由。前 20 個未覆蓋：\n"
            + string.Join("\n", s.UncoveredKeys.Take(20)));
    }

    [Fact]
    public void 已覆蓋數不得低於規則引用的相異鍵數()
    {
        // 規則引用的鍵一定要被算成已覆蓋——否則分類邏輯有問題
        var rules = LoadRules();
        var s = FactCoverageReport.Report(ScanFactKeys(), rules);
        Assert.True(s.Covered >= s.RuleInputCount - s.Uncovered,
            $"已覆蓋 {s.Covered} 少於規則引用的 {s.RuleInputCount}——分類可能沒把規則輸入算進去");
    }

    [Fact]
    public void 規則檔本身要讀得到且條數合理()
    {
        var rules = LoadRules();
        Assert.True(rules.Count >= 20, $"只讀到 {rules.Count} 條規則——規則檔可能壞了");
        Assert.All(rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Id));
            Assert.NotEmpty(r.Inputs);
        });
    }
}
