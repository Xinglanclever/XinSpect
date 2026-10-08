using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 知識表收錄率的申報。核心要釘住的是：這些表都是<b>手工子集</b>，
/// 「查不到名字」代表知識表沒收錄，<b>不代表裝置有問題</b>——這件事必須寫在畫面上。
/// 另外，申報的數字必須與表的實際內容一致：表改了而申報沒改會紅燈。
/// </summary>
public class KnowledgeCoverageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "XinSpect.Decoders")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    /// <summary>數出 switch 運算式裡有幾個 case（排除 default 分支）。</summary>
    private static int CountSwitchCases(string text, string startMarker, string endMarker)
    {
        int i = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(i >= 0, $"找不到 {startMarker}");
        int j = endMarker.Length > 0 ? text.IndexOf(endMarker, i, StringComparison.Ordinal) : text.Length;
        Assert.True(j > i, $"找不到 {endMarker}");
        string seg = text[i..j];
        // 每個 case 一個 =>；default（`_ =>`）不算條目
        return Regex.Matches(seg, @"=>").Count - Regex.Matches(seg, @"_ =>").Count;
    }

    // ── 申報數字必須與表的實際內容一致 ────────────────────────────────────

    [Fact]
    public void PCI基底類別碼的申報數要等於實際條目數()
    {
        string text = Read(Path.Combine("XinSpect.Decoders", "PciKnowledge.cs"));
        int actual = CountSwitchCases(text, "public static string? BaseClassName", "public static string? SubClassName");
        var declared = KnowledgeCoverage.Tables.Single(t => t.Name == "PCI 基底類別碼");
        Assert.Equal(actual, declared.Entries);
    }

    [Fact]
    public void PCI子類別碼的申報數要等於實際條目數()
    {
        string text = Read(Path.Combine("XinSpect.Decoders", "PciKnowledge.cs"));
        int actual = CountSwitchCases(text, "public static string? SubClassName", "public static string? VendorName");
        var declared = KnowledgeCoverage.Tables.Single(t => t.Name == "PCI 子類別碼");
        Assert.Equal(actual, declared.Entries);
    }

    [Fact]
    public void PCI廠商ID的申報數要等於實際條目數()
    {
        string text = Read(Path.Combine("XinSpect.Decoders", "PciKnowledge.cs"));
        int actual = CountSwitchCases(text, "public static string? VendorName", "public static string Describe");
        var declared = KnowledgeCoverage.Tables.Single(t => t.Name == "PCI 廠商 ID");
        Assert.Equal(actual, declared.Entries);
    }

    [Fact]
    public void MAC_OUI的申報數要等於實際條目數()
    {
        string text = Read(Path.Combine("XinSpect.Decoders", "OuiKnowledge.cs"));
        int actual = Regex.Matches(text, @"\[""\w\w-\w\w-\w\w""\]").Count;
        var declared = KnowledgeCoverage.Tables.Single(t => t.Name == "MAC OUI 廠商");
        Assert.Equal(actual, declared.Entries);
    }

    // ── 分母不可確認時不得編一個 ──────────────────────────────────────────

    [Fact]
    public void 分母不可確認時_不得編出比例()
    {
        // PCI-SIG 登錄檔與 IEEE OUI 的規模會變動且本程式無法離線確認——
        // 編一個分母出來會讓「收錄率 12%」這種數字看起來很確定，其實不是。
        foreach (var t in KnowledgeCoverage.Tables)
        {
            if (t.Denominator == 0)
                Assert.Null(t.Ratio);
            else
                Assert.NotNull(t.Ratio);
        }
    }

    [Fact]
    public void 分母為零的申報_要明說不編比例()
    {
        var table = KnowledgeCoverage.Tables.First(t => t.Denominator == 0);
        Assert.Contains("不編比例", table.Describe());
    }

    // ── 未收錄時的顯示方式必須寫明 ────────────────────────────────────────

    [Fact]
    public void 每張表都要寫明未收錄時的顯示方式()
    {
        foreach (var t in KnowledgeCoverage.Tables)
            Assert.False(string.IsNullOrWhiteSpace(t.Fallback), $"{t.Name} 沒寫未收錄時的顯示方式");
    }

    [Fact]
    public void 廠商ID表的未收錄說明要明說不是裝置有問題()
    {
        // 這是最容易被誤讀的一張表：使用者看到「Vendor 0x1234（未收錄）」
        // 會以為裝置有問題，其實只是知識表沒收錄。
        var table = KnowledgeCoverage.Tables.Single(t => t.Name == "PCI 廠商 ID");
        Assert.Contains("不是裝置有問題", table.Fallback);
    }

    [Fact]
    public void 摘要要明說查不到名字不代表裝置有問題()
    {
        string s = KnowledgeCoverage.Summary();
        Assert.Contains("手工子集", s);
        Assert.Contains("不代表裝置有問題", s);
        Assert.Contains("顯示的是原始代碼", s);
    }

    [Fact]
    public void 摘要要列出各表收錄數()
    {
        string s = KnowledgeCoverage.Summary();
        foreach (var t in KnowledgeCoverage.Tables)
            Assert.Contains($"{t.Name} {t.Entries} 條", s);
    }

    [Fact]
    public void 每張表都要有範圍說明()
    {
        foreach (var t in KnowledgeCoverage.Tables)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Scope), $"{t.Name} 沒寫收錄範圍");
            Assert.True(t.Entries > 0, $"{t.Name} 收錄數為 0——空表不該申報");
        }
    }
}
