using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// U10-OS-0025／0033／0038 三把合併的守門（<c>os.wmi.fields</c>＋家族）：
/// 盤點表（<see cref="WmiFieldInventoryService.Inventory"/>）必須與原始碼實況一致——
/// ①各類別的 SELECT 欄位集合等於表的 SelectedFields（有人改查詢面而沒改表就紅燈）；
/// ②「未用」欄位確實全庫零 SELECT、零讀取（防止表腐化成謊言）；
/// ③事實輸出的鍵與形狀（彙總＋逐類家族前綴）。
/// </summary>
public class WmiFieldInventoryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("找不到倉庫根");
    }

    private static string ServicesRoot => Path.Combine(RepoRoot(), "Services");

    private static List<(string File, string Class, string Body)> ScanQueries()
    {
        var result = new List<(string, string, string)>();
        var re = new Regex("""SELECT\s+(?<body>(?:(?!FROM)[\s\S])*?)FROM\s+Win32_(?<cls>Processor|DiskDrive|VideoController)""",
            RegexOptions.Compiled);
        foreach (string file in Directory.EnumerateFiles(ServicesRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.EndsWith("WmiFieldInventoryService.cs", StringComparison.Ordinal)) continue;
            string text = File.ReadAllText(file);
            foreach (Match m in re.Matches(text))
                result.Add((Path.GetFileName(file), m.Groups["cls"].Value, m.Groups["body"].Value));
        }
        return result;
    }

    /// <summary>SELECT 本體跨多個字串字面（"…" + "…"），清掉引號、加號、註解與空白後逐逗號切欄。</summary>
    private static List<string> ParseFields(string body)
    {
        string cleaned = Regex.Replace(body, @"//[^\n]*", "");
        cleaned = cleaned.Replace("\"", "").Replace("+", " ");
        return [.. cleaned.Split(',')
            .Select(f => f.Trim())
            .Where(f => f.Length > 0 && f.All(char.IsLetterOrDigit))];
    }

    [Fact]
    public void 盤點表的查詢欄位集合與原始碼SELECT實況一致()
    {
        var queries = ScanQueries();
        Assert.NotEmpty(queries);
        foreach (var inv in WmiFieldInventoryService.Inventory)
        {
            var found = queries.Where(q => "Win32_" + q.Class == inv.WmiClassName)
                .SelectMany(q => ParseFields(q.Body))
                .ToHashSet(StringComparer.Ordinal);
            var table = inv.SelectedFields.ToHashSet(StringComparer.Ordinal);
            Assert.True(found.SetEquals(table),
                $"{inv.WmiClassName} 的 SELECT 欄位與盤點表不一致。原始碼有而表沒有：[{string.Join(", ", found.Except(table))}]；" +
                $"表有而原始碼沒有：[{string.Join(", ", table.Except(found))}]。改了查詢面就要同步更新盤點表。");
        }
    }

    [Fact]
    public void 未用欄位確實全庫零查詢零讀取()
    {
        var sources = Directory.EnumerateFiles(ServicesRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("WmiFieldInventoryService.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText).ToList();
        foreach (var inv in WmiFieldInventoryService.Inventory)
        {
            foreach (string field in inv.KnownUnusedFields)
            {
                string literal = "\"" + field + "\"";
                Assert.True(sources.All(s => !s.Contains(literal, StringComparison.Ordinal)),
                    $"{inv.WmiClassName}.{field} 被標為「未用」，但 Services/ 出現了字面 {literal}——盤點表已過期，請更新");
            }
        }
    }

    [Fact]
    public void 事實輸出_彙總一把加逐類家族_前綴常數齊()
    {
        var facts = WmiFieldInventoryService.Collect(At);
        Assert.Equal(WmiFieldInventoryService.Inventory.Count + 1, facts.Count);
        Assert.Equal("os.wmi.fields", facts[0].Key);
        Assert.Equal("os.wmi.fields", WmiFieldInventoryService.OsWmiFieldsKey);
        Assert.Equal("os.wmi.fields.", WmiFieldInventoryService.OsWmiFieldsPrefix);
        for (int i = 0; i < WmiFieldInventoryService.Inventory.Count; i++)
        {
            var inv = WmiFieldInventoryService.Inventory[i];
            var fact = facts[i + 1];
            Assert.Equal("os.wmi.fields." + inv.FactSuffix, fact.Key);
            Assert.Equal(FactAvailability.Present, fact.Availability);
            Assert.Equal(inv.KnownUnusedFields.Count, fact.NumericValue);
            // 成文誠實：未用清單要真的列在值裡，讓「沒拿什麼」有處可查。
            foreach (string field in inv.KnownUnusedFields)
                Assert.Contains(field, fact.Value);
        }
    }

    [Fact]
    public void 三把合併的對象類別不縮水()
    {
        var names = WmiFieldInventoryService.Inventory.Select(x => x.WmiClassName).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
            { "Win32_Processor", "Win32_DiskDrive", "Win32_VideoController" }, names);
    }
}
