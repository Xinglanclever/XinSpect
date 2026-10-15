using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 唯讀保證掃描守門（ULTIMATE-1000 階段一 P1-008）。
///
/// <para><b>守門模式</b>：介面契約。防範的真實風險是——
/// 一個「掃描器」在被讀取的路徑上順手改了硬體狀態。這在本專案不是假想：
/// MSR 受控寫入、SuperIO 設定視窗、PCI 設定位址切換都是寫入，
/// 而它們出現在讀取程式碼裡的理由通常很正當（「不先設定就讀不到」）。
/// 正因為正當，它不會被 review 擋下來，只會被守門擋下來。
/// </para>
///
/// <para><b>等級宣告</b>：擋得住「掃描器類別直接呼叫寫入 API」；
/// 擋不住「掃描器呼叫另一個會寫入的方法」（那是呼叫圖分析，不是原始碼掃描）。
/// 這一條限制寫在這裡，不假裝它不存在。
/// </para>
///
/// <para><b>正對照</b>：<see cref="正對照_寫入API的假掃描器必須紅燈"/> 以一個真的含寫入
/// 呼叫的假掃描器執行同一段檢查邏輯，證明「該紅會紅」。
/// 沒有這一條，這個守門可能因為樣式寫錯而永遠是綠的——而永遠是綠的守門等於沒有守門。
/// </para>
/// </summary>
public class ScanSkeletonGuardTests
{
    /// <summary>
    /// 寫入 API 樣式。<b>每一條都要說得出為什麼它算寫入</b>，不是「看起來像」。
    /// </summary>
    private static readonly (string Pattern, string Why)[] WriteApis =
    [
        (@"\bFile\.(WriteAll\w+|AppendAll\w+|Create|Delete|Move|Copy)\s*\(", "檔案系統寫入"),
        (@"\bDirectory\.(CreateDirectory|Delete|Move)\s*\(", "目錄寫入"),
        (@"\bRegistry\.\w+\.SetValue\s*\(", "登錄寫入"),
        (@"\bRegistryKey\.\w+\.SetValue\s*\(", "登錄寫入"),
        (@"\bnew\s+StreamWriter\s*\(", "檔案串流寫入"),
        (@"\bnew\s+FileStream\s*\([^)]*FileMode\.(Create|Append|OpenOrCreate|Truncate)", "檔案串流寫入"),
        (@"\bMarshal\.(WriteByte|WriteInt16|WriteInt32|WriteInt64)\s*\(", "記憶體對映寫入（暫態）"),
        (@"\bunsafe\b|\bfixed\s*\(", "指標寫入的入口（本專案掃描器不應需要）"),
        (@"\bSERVICE_CHANGE_CONFIG\b|\bSC_MANAGER_CREATE_SERVICE\b", "服務設定寫入"),
        (@"\bWriteProcessMemory\s*\(", "行程記憶體寫入"),
        (@"\bProcess\.Start\s*\(", "啟動外部程式（掃描器不該執行外部動作）"),
    ];

    /// <summary>本專案的掃描器介面與其家族型別。</summary>
    private static readonly Regex ScannerInterface =
        new(@"\binterface\s+(IReadOnlyScanner|ISourceAvailability)\b", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Services")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> ProductionFiles(string root)
    {
        string[] skipDirs = ["obj", "bin", "bin2", "Tests", "Nav", "StrykerOutput",
                             "lang-shots", "verify-shots", "verify-shots-210", "verify-shots-omni",
                             "_verify_probe", ".orphaned-tests", "work"];
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var parts = path[(root.Length + 1)..].Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => skipDirs.Contains(p) || p.StartsWith("publish-", StringComparison.Ordinal)))
                continue;
            yield return path;
        }
    }

    /// <summary>
    /// 找出實作掃描骨架介面的類別所在的檔案。
    /// 以「<c>: IReadOnlyScanner</c>」這種實作子句認定，不以檔名認定。
    /// </summary>
    private static List<(string File, string Text)> ScannerFiles(string root)
    {
        var result = new List<(string, string)>();
        foreach (string path in ProductionFiles(root))
        {
            string text = File.ReadAllText(path);
            if (ScannerInterface.IsMatch(text))
            {
                result.Add((path, text));   // 介面檔自己也要乾淨
                continue;
            }
            if (Regex.IsMatch(text, @":\s*[^{]*\bIReadOnlyScanner\b"))
                result.Add((path, text));
        }
        return result;
    }

    /// <summary>對一段原始碼執行寫入 API 掃描，回傳命中的說明。</summary>
    private static List<string> FindWriteCalls(string text)
    {
        var hits = new List<string>();
        foreach (var (pattern, why) in WriteApis)
        {
            foreach (Match m in Regex.Matches(text, pattern))
            {
                int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                hits.Add($"第 {line} 行：{why}（{m.Value.Trim()}）");
            }
        }
        return hits;
    }

    [Fact]
    public void 掃描骨架的實作不得出現寫入API()
    {
        string root = RepoRoot();
        var files = ScannerFiles(root);
        Assert.True(files.Count > 0, "找不到任何實作 IReadOnlyScanner 的檔案——命名或實作子句改了？這條守門就失效了");

        var violations = new List<string>();
        foreach (var (file, text) in files)
        {
            foreach (string hit in FindWriteCalls(text))
                violations.Add($"{Path.GetFileName(file)}：{hit}");
        }

        Assert.True(violations.Count == 0,
            "掃描骨架的實作出現寫入 API——讀取路徑不得改動任何狀態：\n"
            + string.Join("\n", violations.Select(v => "  • " + v))
            + "\n若某一項讀取真的需要先寫（例如 MSR 設定），該寫入必須經 P1-067 的閘門，"
            + "而不是藏在掃描器裡。");
    }

    [Fact]
    public void 正對照_寫入API的假掃描器必須紅燈()
    {
        // 這個字串就是「一個實作了 IReadOnlyScanner 但會寫檔的掃描器」。
        // 檢查邏輯若正確，它必須被抓到；抓不到就代表樣式失效。
        const string fakeScanner = """
            public sealed class 假掃描器 : IReadOnlyScanner
            {
                public string BackendName => "假";
                public ScanOutcome Scan(ScanContext c)
                {
                    File.WriteAllText(@"C:\temp\偷寫.txt", "不該發生");
                    return new([], []);
                }
            }
            """;

        var hits = FindWriteCalls(fakeScanner);
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Contains("檔案系統寫入"));
    }

    [Fact]
    public void 正對照_乾淨的掃描器不得誤報()
    {
        const string cleanScanner = """
            public sealed class 乾淨掃描器 : IReadOnlyScanner
            {
                public string BackendName => "乾淨";
                public ScanOutcome Scan(ScanContext c)
                {
                    var reader = new StreamReader(@"C:\temp\讀就好.txt");
                    string? line = reader.ReadLine();
                    return new([], []);
                }
            }
            """;

        Assert.Empty(FindWriteCalls(cleanScanner));
    }

    [Fact]
    public void 寫入API清單每一條都要有理由()
    {
        foreach (var (pattern, why) in WriteApis)
        {
            Assert.False(string.IsNullOrWhiteSpace(why), $"樣式 {pattern} 沒有寫出為什麼它算寫入");
            Assert.True(why.Length >= 4, $"樣式 {pattern} 的理由太短：{why}");
        }
    }

    [Fact]
    public void 掃描器檔案至少要找到骨架本身()
    {
        // 這一條防的是「掃描範圍不小心排除了骨架」而讓上面那條守門變成空集合。
        var files = ScannerFiles(RepoRoot());
        Assert.Contains(files, f => Path.GetFileName(f.File) == "ScanSkeleton.cs");
    }
}
