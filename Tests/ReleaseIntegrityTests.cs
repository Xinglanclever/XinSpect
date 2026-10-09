using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 發佈一致性守門（docs/PROGRAM-ULTIMATE-2026-10-10.md §5.9；P0-1／P0-2 的回歸網）。
/// 既有 <c>ChangelogTests</c> 守「文字與版號一致」；這一組守「位元組數宣稱與它引用的來源同場」
/// 與「發佈後驗證腳本不能被悄悄拆掉」。
/// </summary>
public class ReleaseIntegrityTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到倉庫根（XinSpect.csproj）。");
    }

    /// <summary>
    /// P0-2 的回歸：README 表列「位元組數為本版（vX）實際發佈」，那張表的下載連結也必須指向 vX。
    /// 先前出過的事故是「文字跟著版號跳、數字沒跳」——宣稱 vX 的表裡放著 v(X−1) 的大小。
    /// 這一條讓宣稱版本與連結版本綁成同一個欄位：只要有人把版號往上跳卻忘了補實測數字，
    /// 至少宣稱與連結還是一致的，真正落後的「數字」由 Tools/verify-release.ps1 在發佈後比對。
    /// </summary>
    [Fact]
    public void README的位元組數宣稱與下載連結同版本()
    {
        foreach (var file in new[] { "README.md", "README.zh-CN.md" })
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), file));
            var claim = Regex.Match(text, @"本版（v([0-9.]+)）");
            Assert.True(claim.Success, $"{file} 少了「本版（vX）實際發佈」宣稱行——位元組數表必須註明數字來源版本");
            var links = Regex.Matches(text, @"/releases/download/v([0-9.]+)/").Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.NotEmpty(links);
            foreach (var linkTag in links)
                Assert.Equal(claim.Groups[1].Value, linkTag);
        }
    }

    /// <summary>宣稱版本必須等於專案版號（與 <c>ChangelogTests</c> 的徽章檢查互為補充：那是徽章，這是數字表來源）。</summary>
    [Fact]
    public void 位元組數宣稱版本等於專案版號()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        var claim = Regex.Match(text, @"本版（v([0-9.]+)）");
        Assert.True(claim.Success);
        Assert.Equal(ChangelogCatalog.Latest, claim.Groups[1].Value);
    }

    /// <summary>
    /// 發佈後驗證腳本必須在倉庫裡、沒被掏空，而且帶 UTF-8 BOM——
    /// Windows PowerShell 5.1 把無 BOM 的 UTF-8 當 ANSI 讀，中文註解會直接炸（本專案踩過多次）。
    /// </summary>
    [Fact]
    public void 發佈驗證腳本存在且UTF8帶BOM有实质检查()
    {
        var path = Path.Combine(RepoRoot(), "Tools", "verify-release.ps1");
        Assert.True(File.Exists(path), "Tools/verify-release.ps1 不見了——發佈後驗證（§5.9）不能省略");
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "verify-release.ps1 少了 UTF-8 BOM——PS 5.1 讀無 BOM 的 UTF-8 會把中文當 ANSI 炸掉");
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("releases/tags", text, StringComparison.Ordinal);
        Assert.Contains("bytes", text, StringComparison.Ordinal);
    }
}
