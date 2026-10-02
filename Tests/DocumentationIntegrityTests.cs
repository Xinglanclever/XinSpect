using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 文件完整性契約（V7 §19）：量化宣稱必須可推導——封面寫的數字（55 能力、10 機制、5 測試方法、
/// 5 維度、51 工作包、20 章）一律由文件內容算出來對帳，手寫漂移就紅燈。這是「用第 4 章的方法學
/// 對付自己的文件」的機器落實。
/// </summary>
public class DocumentationIntegrityTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到 repo 根（XinSpect.csproj）");
    }

    private static string[] V7Lines() =>
        File.ReadAllLines(Path.Combine(FindRepoRoot(), "PROGRAM-EVEREST-V7-2026-10-02.md"));

    private static IReadOnlyDictionary<string, int> Derived(string[] lines)
    {
        var wp = new HashSet<int>();
        foreach (var l in lines)
        {
            var m = Regex.Match(l, @"^\|\s*\*\*WP(\d+)\*\*");
            if (m.Success) wp.Add(int.Parse(m.Groups[1].Value));
        }
        var d = new Dictionary<string, int>
        {
            ["能力"] = lines.Count(l => Regex.IsMatch(l, @"^\|\s*\*\*A\d+\*\*")),
            ["機制"] = lines.Count(l => Regex.IsMatch(l, @"^\|\s*\*\*M\d+\*\*")),
            ["測試方法"] = lines.Count(l => Regex.IsMatch(l, @"^## 5\.\d+ T\d")),
            ["維度"] = lines.Count(l => Regex.IsMatch(l, @"^\|\s*\*\*D\d+\*\*")),
            ["個工作包"] = wp.Count,
            ["章"] = lines.Count(l => l.StartsWith("# 第", StringComparison.Ordinal)),
        };
        return d;
    }

    [Fact]
    public void 封面數字必須與內容推導一致()
    {
        var lines = V7Lines();
        var derived = Derived(lines);

        // 封面行（第 3 行）逐一抽出宣稱值與推導值對帳。
        var cover = lines[2];
        foreach (var (label, pattern) in new[]
        {
            (label: "能力", pattern: @"(\d+) 能力"),
            (label: "機制", pattern: @"(\d+) 機制"),
            (label: "測試方法", pattern: @"(\d+) 測試方法"),
            (label: "維度", pattern: @"(\d+) 維度"),
            (label: "個工作包", pattern: @"(\d+) 個工作包"),
            (label: "章", pattern: @"(\d+) 章"),
        })
        {
            var m = Regex.Match(cover, pattern);
            Assert.True(m.Success, $"封面行找不到「{label}」的宣稱數字");
            Assert.True(int.Parse(m.Groups[1].Value) == derived[label],
                $"封面宣稱 {m.Groups[1].Value} {label}，但內容推導為 {derived[label]}——文件數字漂移，改文件不是改測試");
        }
    }

    [Fact]
    public void 工作包編號連續無缺無重()
    {
        var lines = V7Lines();
        var wp = new HashSet<int>();
        foreach (var l in lines)
        {
            var m = Regex.Match(l, @"^\|\s*\*\*WP(\d+)\*\*");
            if (m.Success) wp.Add(int.Parse(m.Groups[1].Value));
        }
        Assert.Equal(Enumerable.Range(1, 51), wp.OrderBy(x => x));
    }

    [Fact]
    public void 里程碑用G不與機制撞號()
    {
        // V7 §0.1 第 15 項的教訓：同一缺陷會在修正它的同一份文件裡復發——機器盯著。
        var lines = V7Lines();
        Assert.DoesNotContain(lines, l => Regex.IsMatch(l, @"^\|\s*\*\*M\d+\*\*.*里程碑"));
        Assert.Equal(6, lines.Count(l => Regex.IsMatch(l, @"^\|\s*\*\*G\d\*\*")));
    }
}
