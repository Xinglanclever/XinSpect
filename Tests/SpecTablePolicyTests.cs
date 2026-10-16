using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// <b>「成組的規格常數表要有收錄政策」的守門。</b>
///
/// <para>
/// 本專案有一條規則，它在五個地方各自被驗證過一次：
/// <b>如果我需要寫一段話去解釋「為什麼這個數字可能是錯的」，那它就不該被產出。</b>
/// </para>
///
/// <para>
/// 而它的可檢查形式<b>不是「每個常數都要有 SpecRef」</b>——
/// 那會要求 55 個解碼器各自加一段宣告，而其中多數的常數是<b>欄位偏移</b>，
/// 那些不是「一張規格表的一部分」，它們就是那個結構本身，
/// 而 <c>[SpecRef]</c> 已經指了結構。
/// </para>
///
/// <para>
/// <b>真正需要申報的是「成組的規格常數表」</b>：
/// 同一個前綴、多個數值——<b>因為那一組的成員可以增減，而界線在哪看不出來。</b>
/// 例如 <c>Tpm2</c> 的 <c>Pt*</c>（屬性編號）：列出來的四個讀起來像「全部」，
/// 而其餘的（<c>TPM_PT_PERMANENT</c> 那一族）為什麼不在，看不出來。
/// </para>
///
/// <para>
/// <b>所以判準是「同前綴 ≥ 3 個十六進位常數」</b>——
/// 那會抓到常數表，而不會去煩那些只有欄位偏移的檔案。
/// 而組本身必須有一段政策宣告（可搜 <c>spec-ref-policy</c>）。
/// </para>
/// </summary>
public class SpecTablePolicyTests
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
    /// 整條 <c>const</c> 宣告（可能橫跨多行、可能一行多個變數）。
    ///
    /// <para>
    /// <b>第一版只抓「<c>public const \w+ (\w+) = 0x…</c>」的第一個變數，
    /// 所以 <c>Tpm2.cs</c> 的那一行</b>
    /// <code>
    /// public const uint PtVendor1 = 0x00000104, PtVendor2 = 0x00000105,
    ///                   PtVendor3 = 0x00000106, PtVendor4 = 0x00000107;
    /// </code>
    /// <b>只貢獻了 <c>PtVendor1</c>——於是掃描回報「0 組」，
    /// 而那是一個假零：守門什麼都沒檢查到，卻顯示綠燈。</b>
    /// </para>
    /// </summary>
    private static readonly Regex ConstDecl =
        new(@"public const \w+ ([^;]+);", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>宣告內容裡的「名字 = 0x值」。</summary>
    private static readonly Regex ConstAssign =
        new(@"(\w+)\s*=\s*(0x[0-9A-Fa-f]+)", RegexOptions.Compiled);

    /// <summary>同前綴（去掉尾端數字）的常數群。</summary>
    internal static Dictionary<string, List<string>> Groups(string text)
    {
        var byPrefix = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Match d in ConstDecl.Matches(text))
            foreach (Match m in ConstAssign.Matches(d.Groups[1].Value))
            {
                string name = m.Groups[1].Value;
                string prefix = Regex.Replace(name, @"\d+$", "");
                if (prefix.Length == 0) continue;
                if (!byPrefix.TryGetValue(prefix, out var list)) byPrefix[prefix] = list = [];
                list.Add(name);
            }
        return byPrefix;
    }

    /// <summary>
    /// <b>這一條守著「守門真的抓得到東西」。</b>
    /// 用 <c>Tpm2.cs</c> 當已知有常數表的樣本——
    /// 如果它回空，那整個守門的綠燈都是假的。
    /// </summary>
    [Fact]
    public void 掃描要抓得到已知的常數表()
    {
        string path = Path.Combine(RepoRoot(), "XinSpect.Decoders", "Tpm2.cs");
        var tables = TablesIn(File.ReadAllText(path));

        Assert.Contains(tables, t => t.StartsWith("PtVendor*", StringComparison.Ordinal));
        Assert.Contains(tables, t => t.StartsWith("AlgSha*", StringComparison.Ordinal));
    }

    /// <summary>
    /// 一個檔案裡「成組的規格常數表」——同前綴 ≥ 3 個。
    /// 只有單一或兩個的（例如 <c>TagNoSessions</c>、<c>CcGetCapability</c>）不算一組。
    /// </summary>
    internal static List<string> TablesIn(string text)
        => Groups(text).Where(kv => kv.Value.Count >= 3)
            .Select(kv => $"{kv.Key}*（{kv.Value.Count} 個）")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

    private static bool HasPolicy(string text)
        => Regex.IsMatch(text, @"spec-ref-policy|收錄政策");

    internal static List<string> CheckFile(string path)
    {
        string text = File.ReadAllText(path);
        var tables = TablesIn(text);
        if (tables.Count == 0) return [];
        if (HasPolicy(text)) return [];

        return
        [
            $"{Path.GetFileName(path)}：有 {tables.Count} 組規格常數表"
            + $"（{string.Join("、", tables)}）但沒有收錄政策宣告——"
            + "**那一組的成員可以增減，而界線在哪看不出來。**"
            + "請加一段說出「只收錄對準過出處的編號」與「其餘不寫」的註解"
            + "（可搜 'spec-ref-policy'）",
        ];
    }

    [Fact]
    public void 成組的規格常數表要有收錄政策()
    {
        string root = RepoRoot();
        var problems = new List<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(root, "XinSpect.Decoders"), "*.cs"))
            problems.AddRange(CheckFile(file));

        Assert.True(problems.Count == 0,
            "**這一條守著「沒有出處的常數不寫」——而它已經在四個地方各自被記得一次。**\n"
            + string.Join("\n", problems.Select(p => "  • " + p)));
    }

    /// <summary>正對照：一組常數而沒有政策，必須被抓到。</summary>
    [Fact]
    public void 常數表政策_正對照()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"stp_{Guid.NewGuid():N}.cs");
        // **raw string literal 的收尾 """ 縮排會決定內容的實際縮排**——
        // 我第一版把 """ 與內容同縮排，所以內容被裁到最左，
        // `public const` 開頭的宣告因此對不上正規表示式。
        //
        // 而那個失敗的形式是「collection was empty」——**看起來像「沒有違規」**，
        // 那正是這一輪我在守門本身上抓到的同一個假零。
        // 所以這裡改用普通字串串接，讓縮排不可能出錯。
        File.WriteAllText(temp,
            "public static class Probe\n" +
            "{\n" +
            "    public const uint PtAlpha = 0x00000100;\n" +
            "    public const uint PtBeta = 0x00000101;\n" +
            "    public const uint PtGamma = 0x00000102;\n" +
            "}\n");

        try
        {
            var problems = CheckFile(temp);
            Assert.Single(problems);
            Assert.Contains("Pt*（3 個）", problems[0]);
            Assert.Contains("沒有收錄政策宣告", problems[0]);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>負對照：宣告了政策就放行。</summary>
    [Fact]
    public void 常數表政策_負對照_有宣告就放行()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"stp_{Guid.NewGuid():N}.cs");
        // 同正對照：用普通字串串接，避開 raw string literal 的縮排陷阱。
        File.WriteAllText(temp,
            "public static class Probe\n" +
            "{\n" +
            "    // spec-ref-policy：只寫對準過出處的編號。\n" +
            "    public const uint PtAlpha = 0x00000100;\n" +
            "    public const uint PtBeta = 0x00000101;\n" +
            "    public const uint PtGamma = 0x00000102;\n" +
            "}\n");

        try
        {
            Assert.Empty(CheckFile(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>
    /// <b>這一條守著「不要把守門擴大」。</b>
    /// 只有一兩個常數（欄位偏移那種）不該被當成一組——
    /// 否則 55 個解碼器都要各加一段宣告，而那與「加 56 個標記讓紅燈消失」同型。
    /// </summary>
    [Fact]
    public void 零散常數不該被當成一組()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"stp_{Guid.NewGuid():N}.cs");
        // HeaderOffset 與 TailOffset 的尾端都不是數字（去掉尾數後前綴不同），
        // 所以它們各自只有一個成員——**而這一條要驗的正是「那不構成一組」**。
        File.WriteAllText(temp,
            "public static class Probe\n" +
            "{\n" +
            "    public const uint HeaderOffset = 0x00000010;\n" +
            "    public const uint TailOffset = 0x00000020;\n" +
            "}\n");

        try
        {
            Assert.Empty(TablesIn(File.ReadAllText(temp)));
            Assert.Empty(CheckFile(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
