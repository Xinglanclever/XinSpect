using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 環境假設守門（docs/PROGRAM-ULTIMATE-2026-10-10.md §3.2／§5.5）：
/// <b>生產碼不得寫死「只有這台開發機才有」的路徑。</b>
/// </summary>
/// <remarks>
/// <para>
/// 已經發生過兩次同類缺陷：WinRing0 的 NuGet 快取路徑寫死使用者名稱（v2.43 修——發佈出去的
/// 單一執行檔在別台機器等於找不到驅動，MSR／PCI／I/O／MMIO 全數讀不到）；
/// PaddleOCR 與 CPU-Z 的搜尋路徑各寫死一條 <c>C:\Users\Administrator\…</c>（本版修）。
/// 共同點：功能在開發機上好好的，到使用者手上靜默失靈——失敗長得像「沒有這個功能」，
/// 不像「路徑假設錯了」，使用者與我們都查不到。
/// </para>
/// <para>
/// <b>為什麼抓「具體使用者名稱」而不是整個 <c>C:\Users\</c>：</b>
/// <c>WindowsBuiltInRole.Administrator</c>、<c>BUILTIN\Administrators</c>（ACL）這類
/// 角色／群組名是合法词汇，不是環境假設。真正的炸彈形態是「驱动器＋Users＋某人名」
/// 或「驱动器＋Desktop＋子目錄」——所以正則要 <c>Users\\&lt;名稱&gt;</c> 或 <c>Desktop\\&lt;名稱&gt;</c>
/// 開頭跟著非 <c>%</c>（百分號是環境變數佔位，合法）。
/// </para>
/// <para>
/// 正對照（<see cref="掃描器抓得到寫死路徑_正對照"/>）釘住掃描器真有牙；
/// 反面例子釘住它不會誤報角色名與環境推導寫法。
/// </para>
/// </remarks>
public class EnvironmentGuardTests
{
    /// <summary>
    /// 登記為「有理由的例外」：鍵＝倉庫相對路徑，值＝<b>為什麼不算環境假設</b>（不得為空）。
    /// 上限見 <see cref="白名單不得無限長大"/>；每一筆都該在下一次重構時被消掉。
    /// </summary>
    private static readonly Dictionary<string, string> Whitelist = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    private const int WhitelistCap = 5;

    /// <summary>本專案掃生產碼的目錄（涵蓋發佈路徑；測試與建置產物不在列）。</summary>
    private static readonly string[] ProductionDirs =
    [
        "Services", "Views", "Controls", "ViewModels", "Models", "Nav",
        "XinSpect.Decoders", "Bridge", "BlueSquadron", "Installer",
    ];

    private static readonly Regex HardcodedUserPath =
        new(@"[A-Za-z]:\\+Users\\+(?!%)[A-Za-z0-9._-]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HardcodedDesktopPath =
        new(@"[A-Za-z]:\\+Desktop\\+(?!%)[A-Za-z0-9._-]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>回傳命中行（1 起）的行號清單；源碼文字直接掃，不做字串/註解級解析（啟發式，偏保守）。</summary>
    internal static List<int> ScanLines(string source)
    {
        var hits = new List<int>();
        var lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            // 跳過註解行——文件裡「描述這條規則」的文字不該讓守門打自己。
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)
             || line.TrimStart().StartsWith("///", StringComparison.Ordinal)) continue;
            if (HardcodedUserPath.IsMatch(line) || HardcodedDesktopPath.IsMatch(line))
                hits.Add(i + 1);
        }
        return hits;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到倉庫根（XinSpect.csproj）。");
    }

    [Fact]
    public void 生產碼不得寫死使用者目錄與桌面路徑()
    {
        string root = RepoRoot();
        var offenders = new List<string>();
        foreach (var top in ProductionDirs)
        {
            var dir = Path.Combine(root, top);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                         .Where(f => (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                   || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                                  && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                  && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                  && !f.Contains($"{Path.DirectorySeparatorChar}publish-{Path.DirectorySeparatorChar}")
                                  && !f.Contains("\\publish-"))
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(root, file);
                if (Whitelist.ContainsKey(rel)) continue;
                foreach (int lineNo in ScanLines(File.ReadAllText(file)))
                    offenders.Add($"{rel}:{lineNo}");
            }
        }

        Assert.True(offenders.Count == 0,
            "生產碼寫死了開發機路徑——功能到其他使用者手上會靜默失靈（失敗長得像「沒有這個功能」）。" +
            "改用環境推導（SpecialFolder／%USERPROFILE%）＋找不到時如實標示；" +
            "確屬必要的例外請進白名單並寫出理由：" + string.Join("、", offenders));
    }

    [Fact]
    public void 白名單不得無限長大()
    {
        Assert.True(Whitelist.Count <= WhitelistCap,
            $"環境假設白名單已達 {Whitelist.Count} 筆（上限 {WhitelistCap}）——每多一筆都要重新想規則是否寫歪了。");
        foreach (var (file, reason) in Whitelist)
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{file} 的白名單理由為空");
    }

    [Fact]
    public void 掃描器抓得到寫死路徑_正對照()
    {
        const string bomb = """
            var a = @"C:\Users\Administrator\PaddleOCR-VL\run.py";
            var b = "D:\\Desktop\\tools\\cpuz.exe";
            """;
        Assert.Equal([1, 2], ScanLines(bomb));

        const string clean = """
            var ok1 = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var ok2 = Path.Combine(desktop, "图吧工具箱", "cpuz_x64.exe");
            var ok3 = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            var ok4 = new NTAccount("BUILTIN", "Administrators");
            var ok5 = "%USERPROFILE%\\PaddleOCR-VL";
            // 註解裡提到 C:\Users\Anyone\x 不該打紅守門自己
            """;
        Assert.Empty(ScanLines(clean));
    }
}
