using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// <b>測試 helper 的陣列邊界靜態檢查。</b>
///
/// <para>
/// <b>為什麼需要這一條：</b>我在同一個缺陷上錯了<b>四次</b>——
/// Type 0 的 <c>Length &gt; 0x14</c> 去讀 <c>DwordAt(0x14)</c>（需 0x18）、
/// Type 32 的 <c>&gt; 0x0A</c> 去讀到 <c>0x0D</c>（需 0x0E）、
/// Type 19 helper 的 <c>new byte[0x0F]</c> 卻在 <c>0x0C</c> 寫 4 bytes、
/// Type 4 helper 的 <c>new byte[0x1A]</c> 卻往 <c>0x25</c> 寫。
/// </para>
///
/// <para>
/// <b>四次都是「起點 &lt; 長度」而不是「起點 ＋ 寬度 ≤ 長度」。</b>
/// 而四次都是人工看出來的——那代表它應該被機械化地檢查，
/// 而不是靠我的注意力。這一條測的就是這個。
/// </para>
///
/// <para>
/// 它掃的是<b>測試檔</b>（helper 在那裡），規則三條：
/// <list type="number">
///   <item><c>new byte[N]</c> 之後的 <c>GetBytes(...).CopyTo(d, M)</c>：
///     M ＋ GetBytes 的寬度 ≤ N</item>
///   <item><c>d[M] = ...</c>：M ＋ 1 ≤ N</item>
///   <item><c>BitConverter.GetBytes(...).CopyTo(d, M)</c> 的寬度由常數型別推定</item>
/// </list>
/// </para>
/// </summary>
public class TestArrayBoundsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>把 `new byte[N]` 的宣告與同一個方法內的寫入配對起來檢查。</summary>
    private static List<string> CheckFile(string path)
    {
        var problems = new List<string>();
        string text = File.ReadAllText(path);
        var lines = text.Split('\n');

        // 對每個方法（以 `private static ... Xxx(` 開頭到 `\n    }` 結束）個別檢查。
        var methodStart = new Regex(@"^\s+(?:private|public|internal)\s+static\s+[\w<>,\[\]\?\. ]+\s+(\w+)\s*\(",
            RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            if (!methodStart.IsMatch(lines[i])) continue;

            string methodName = methodStart.Match(lines[i]).Groups[1].Value;
            int depth = 0, end = i;
            for (int j = i; j < lines.Length; j++)
            {
                depth += lines[j].Count(c => c == '{') - lines[j].Count(c => c == '}');
                if (j > i && depth <= 0) { end = j; break; }
            }

            var body = string.Join("\n", lines.Skip(i).Take(end - i + 1));

            // 這個方法裡宣告的每個 byte 陣列。
            foreach (Match decl in Regex.Matches(body, @"var\s+(\w+)\s*=\s*new\s+byte\[(0x[0-9A-Fa-f]+|\d+)\]"))
            {
                string name = decl.Groups[1].Value;
                int size = decl.Groups[2].Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToInt32(decl.Groups[2].Value[2..], 16)
                    : int.Parse(decl.Groups[2].Value);

                // GetBytes(...).CopyTo(name, M)
                foreach (Match cp in Regex.Matches(body,
                    $@"GetBytes\([^)]*\)\.CopyTo\(\s*{Regex.Escape(name)}\s*,\s*(0x[0-9A-Fa-f]+|\d+)\s*\)"))
                {
                    int offset = cp.Groups[1].Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? Convert.ToInt32(cp.Groups[1].Value[2..], 16)
                        : int.Parse(cp.Groups[1].Value);

                    // 寬度推定：看 GetBytes 的引數來源。無法推定時取 8（最寬）並註明。
                    int width = GuessWidth(cp.Value, body);
                    if (offset + width > size)
                        problems.Add($"{Path.GetFileName(path)} 的 {methodName}："
                            + $"{name} 只有 {size} bytes，卻在 {offset} 寫 {width} bytes"
                            + $"（需要至少 {offset + width}）");
                }

                // name[M] = ...
                foreach (Match assign in Regex.Matches(body,
                    $@"\b{Regex.Escape(name)}\[(0x[0-9A-Fa-f]+|\d+)\]\s*="))
                {
                    int offset = assign.Groups[1].Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? Convert.ToInt32(assign.Groups[1].Value[2..], 16)
                        : int.Parse(assign.Groups[1].Value);
                    if (offset + 1 > size)
                        problems.Add($"{Path.GetFileName(path)} 的 {methodName}："
                            + $"{name} 只有 {size} bytes，卻在 {offset} 寫 1 byte");
                }
            }
        }
        return problems;
    }

    /// <summary>
    /// 推定 <c>GetBytes(...)</c> 的寬度：先在方法內找那個變數的宣告型別，
    /// 找不到就取 8（最寬）——<b>保守的方向是「寧可誤報也不要漏報」</b>。
    /// </summary>
    private static int GuessWidth(string copyCall, string body)
    {
        var m = Regex.Match(copyCall, @"GetBytes\(\s*\(?(ushort|uint|ulong|short|int|long)\)?\s*([\w\.]*)");
        if (m.Success)
        {
            return m.Groups[1].Value switch
            {
                "ushort" or "short" => 2,
                "uint" or "int" => 4,
                "ulong" or "long" => 8,
                _ => 8,
            };
        }

        // 沒有轉型：看引數是不是方法參數，往方法簽章找型別。
        var arg = Regex.Match(copyCall, @"GetBytes\(\s*(\w+)\s*\)");
        if (arg.Success)
        {
            string varName = arg.Groups[1].Value;
            var typeMatch = Regex.Match(body,
                @"(ushort|uint|ulong|short|int|long|byte)\s+" + Regex.Escape(varName) + @"\b");
            if (typeMatch.Success)
            {
                return typeMatch.Groups[1].Value switch
                {
                    "byte" => 1,
                    "ushort" or "short" => 2,
                    "uint" or "int" => 4,
                    "ulong" or "long" => 8,
                    _ => 8,
                };
            }
        }
        return 8;
    }

    [Fact]
    public void 測試helper的陣列寫入不得越界()
    {
        string root = RepoRoot();

        // **檢查器不檢查自己**：這個檔案裡刻意放了一段「已知會越界」的探針
        // 當正對照，若把它自己也掃進來，主守門會被自己的探針弄紅。
        // 排除自己比把探針拆成字串更根本——後者只是繞過，前者是原則。
        string self = Path.GetFileName(Path.Combine(
            Path.Combine(root, "Tests"), nameof(TestArrayBoundsTests) + ".cs"));

        var problems = new List<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(root, "Tests"), "*.cs"))
        {
            if (Path.GetFileName(file) == self) continue;
            problems.AddRange(CheckFile(file));
        }

        Assert.True(problems.Count == 0,
            "測試 helper 有陣列越界——**這個錯誤我在同一個地方犯了四次**，"
            + "而四次都是「起點 < 長度」而不是「起點 ＋ 寬度 ≤ 長度」：\n"
            + string.Join("\n", problems.Select(p => "  • " + p)));
    }

    /// <summary>
    /// <b>這條守門自己的正對照。</b>
    /// 餵一段「已知會越界」的程式碼給檢查邏輯，要求它報出來。
    /// 沒有這一條，一個「什麼都檢查不出來」的實作也會讓上面那條綠。
    /// </summary>
    [Fact]
    public void 邊界檢查_正對照_已知的越界必須被抓到()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"bnd_{Guid.NewGuid():N}.cs");
        File.WriteAllText(temp, """
            public class Probe
            {
                private static byte[] Bad(ushort threads)
                {
                    var d = new byte[0x1A];
                    BitConverter.GetBytes(threads).CopyTo(d, 0x25);
                    return d;
                }

                private static byte[] Good(ushort threads)
                {
                    var e = new byte[0x27];
                    BitConverter.GetBytes(threads).CopyTo(e, 0x25);
                    return e;
                }
            }
            """);

        try
        {
            var problems = CheckFile(temp);
            Assert.Contains(problems, p => p.Contains("Bad") && p.Contains("0x1A") == false || p.Contains("Bad"));
            Assert.DoesNotContain(problems, p => p.Contains("Good"));
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
