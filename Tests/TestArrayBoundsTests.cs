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
            //
            // ═══ 非字面大小的宣告：**能檢查多少就檢查多少** ═══
            //
            // 第五次的錯誤（`new byte[2 + 8]` 放 12 bytes 的字串）來自
            // 「大小是運算式」——而原本的樣式只認字面值，所以整條宣告被跳過。
            //
            // 但第一版擴充的規則太粗：它對**所有**非字面大小且「有寫入」的宣告報紅燈，
            // 結果全測試套有 56 條——而它們幾乎都是正當寫法
            // （大小由建構資料的長度決定，例如 `new byte[36 + payload.Length]`）。
            //
            // **要 56 個標記才能變綠，那就不是守門，是儀式。**
            // 而「加標記讓紅燈消失」正是這整個專案一直在防的事。
            //
            // 所以規則分兩層：
            //   ① 常數運算式（`36 + 40`）→ 算出來，照常數檢查。
            //   ② 真的變數（`36 + payload.Length`）→ **用「位移」當下限**：
            //      資料驅動的緩衝區幾乎都遠大於任何常數寫入位移，
            //      所以只在位移大得離譜時才報——那時「大小 ≥ 位移 ＋ 寬度」
            //      這個下限本身就可疑。
            //
            // 沒被檢查到的只剩「位移也是變數」——那真的驗不了，而它很少見。
            foreach (Match decl in Regex.Matches(body,
                @"var\s+(\w+)\s*=\s*new\s+byte\[([^\]]+)\]"))
            {
                string name = decl.Groups[1].Value;
                string sizeExpr = decl.Groups[2].Value.Trim();
                if (Regex.IsMatch(sizeExpr, @"^(0x[0-9A-Fa-f]+|\d+)$")) continue;

                if (TryEvalConstant(sizeExpr) is int constSize)
                {
                    foreach (Match cp2 in Regex.Matches(body,
                        $@"GetBytes\([^)]*\)\.CopyTo\(\s*{Regex.Escape(name)}\s*,\s*(0x[0-9A-Fa-f]+|\d+)\s*\)"))
                    {
                        int off2 = cp2.Groups[1].Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? Convert.ToInt32(cp2.Groups[1].Value[2..], 16)
                            : int.Parse(cp2.Groups[1].Value);
                        int w2 = GuessWidth(cp2.Value, body);
                        if (off2 + w2 > constSize)
                            problems.Add($"{Path.GetFileName(path)} 的 {methodName}："
                                + $"{name} 是常數運算式 {sizeExpr}＝{constSize} bytes，"
                                + $"卻在 {off2} 寫 {w2} bytes（需要至少 {off2 + w2}）");
                    }
                    continue;
                }

                // 真的變數：只在「常數寫入位移大得離譜」時報。
                if (Regex.IsMatch(decl.Value, @"//\s*bounds-checked-by:")
                    || HasMarkerAbove(body, decl.Index))
                    continue;

                foreach (Match cp3 in Regex.Matches(body,
                    $@"GetBytes\([^)]*\)\.CopyTo\(\s*{Regex.Escape(name)}\s*,\s*(0x[0-9A-Fa-f]+|\d+)\s*\)"))
                {
                    int off3 = cp3.Groups[1].Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? Convert.ToInt32(cp3.Groups[1].Value[2..], 16)
                        : int.Parse(cp3.Groups[1].Value);
                    if (off3 > MaxPlausibleOffsetForVariableBuffer)
                        problems.Add($"{Path.GetFileName(path)} 的 {methodName}："
                            + $"{name} 的大小是變數「{sizeExpr}」而寫入位移 {off3} 很大"
                            + $"——**這個組合驗不了，而那個位移本身就可疑**；"
                            + $"請改用常數大小、或加上界檢查、或加 `// bounds-checked-by:` 標記");
                }
            }

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
    /// 變數大小的緩衝區，其「常數寫入位移」的上限。
    ///
    /// <para>
    /// 超過這個值的位移，配上變數大小，就是一個<b>驗不了又可疑</b>的組合——
    /// 而那種組合才值得報紅燈。門檻取 0x200（512）：
    /// 資料驅動的緩衝區幾乎都遠大於它，所以誤報很少；
    /// 而寫到 0x200 之後又用變數大小的宣告，確實需要人看一眼。
    /// </para>
    /// </summary>
    private const int MaxPlausibleOffsetForVariableBuffer = 0x200;

    /// <summary>
    /// 試著把一個「大小運算式」靜態求值。
    ///
    /// <para>
    /// <b>為什麼要有這個：</b>擴充後的守門對「非字面大小」一律報紅燈，
    /// 而其中有幾條其實**全是字面值**——<c>new byte[36 + 40]</c>、
    /// <c>new byte[4 + 4 + 4 + 2 + 32 + 4 + 3]</c>。
    /// 那些**驗得出來**，所以它們不該跟「真的驗不了」的混在一起被標記。
    /// </para>
    ///
    /// <para>
    /// <b>能算就算，不能算就回 <c>null</c>。</b>只支援
    /// <c>+ - * /</c> 與十進位／十六進位字面值、括號——
    /// <b>刻意不做完整的 C# 運算式求值器</b>，因為那會讓這個守門
    /// 長成一個它不該變成的東西。遇到變數就放棄，那條留給標記。
    /// </para>
    /// </summary>
    private static int? TryEvalConstant(string expr)
    {
        // 先把十六進位字面值換成十進位，再交給一個最小解析器。
        string s = Regex.Replace(expr, @"0x([0-9A-Fa-f]+)",
            m => Convert.ToInt64(m.Groups[1].Value, 16).ToString());
        // 有任何非數字／運算子／括號／空白 → 放棄（那是變數或方法呼叫）。
        if (!Regex.IsMatch(s, @"^[\d\s\+\-\*/\(\)]+$")) return null;

        int pos = 0;
        try
        {
            int value = ParseExpr(s, ref pos);
            SkipWs(s, ref pos);
            return pos == s.Length ? value : null;
        }
        catch (FormatException) { return null; }
        catch (DivideByZeroException) { return null; }
    }

    private static void SkipWs(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    private static int ParseExpr(string s, ref int pos)
    {
        int left = ParseTerm(s, ref pos);
        while (true)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length || (s[pos] != '+' && s[pos] != '-')) return left;
            char op = s[pos++];
            int right = ParseTerm(s, ref pos);
            left = op == '+' ? left + right : left - right;
        }
    }

    private static int ParseTerm(string s, ref int pos)
    {
        int left = ParseFactor(s, ref pos);
        while (true)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length || (s[pos] != '*' && s[pos] != '/')) return left;
            char op = s[pos++];
            int right = ParseFactor(s, ref pos);
            if (op == '*') left *= right;
            else
            {
                if (right == 0) throw new DivideByZeroException();
                left /= right;
            }
        }
    }

    private static int ParseFactor(string s, ref int pos)
    {
        SkipWs(s, ref pos);
        if (pos < s.Length && s[pos] == '(')
        {
            pos++;
            int v = ParseExpr(s, ref pos);
            SkipWs(s, ref pos);
            if (pos >= s.Length || s[pos] != ')') throw new FormatException();
            pos++;
            return v;
        }
        int start = pos;
        while (pos < s.Length && char.IsDigit(s[pos])) pos++;
        if (pos == start) throw new FormatException();
        return int.Parse(s[start..pos]);
    }

    /// <summary>
    /// <b>常數運算式求值的正對照。</b>
    ///
    /// <para>
    /// 沒有這一條，「常數運算式有沒有被算出來」就只能從「違規清單裡有沒有它」
    /// 去推論——而那個推論有兩種解釋（算過了但安全／根本沒匹配到），
    /// <b>而兩者天差地遠</b>。
    /// </para>
    /// </summary>
    [Fact]
    public void 常數運算式求值_正對照()
    {
        Assert.Equal(76, TryEvalConstant("36 + 40"));
        Assert.Equal(24, TryEvalConstant("0x10 + 0x8"));
        Assert.Equal(60, TryEvalConstant("36 + 8 + 16"));
        // 4+4+4+2+20+4+1 = 39。第一版我寫 44——**我沒有實際加它，
        // 而是「看」了一眼就寫下一個看起來合理的數字。**
        // 而這條測試的價值就在這裡：它證明的是「我的心算不可靠」，
        // 而不是「求值器有效」。**如果期望值是跑一次抄下來的，它會永遠綠。**
        Assert.Equal(39, TryEvalConstant("4 + 4 + 4 + 2 + 20 + 4 + 1"));
        Assert.Equal(60, TryEvalConstant("(36 + 24)"));
        Assert.Equal(120, TryEvalConstant("36 + 4 * 21"));

        // 有變數或方法呼叫就放棄——那是「真的驗不了」的那一組。
        Assert.Null(TryEvalConstant("totalLength"));
        Assert.Null(TryEvalConstant("AcpiTable.HeaderLength + body.Count"));
        Assert.Null(TryEvalConstant("SpdReader.Ddr4Size"));
        Assert.Null(TryEvalConstant("12 + (rc == 0 ? 2 + digest.Length : 0)"));
    }

    /// <summary>宣告的前一行有沒有 <c>// bounds-checked-by:</c> 標記。</summary>
    private static bool HasMarkerAbove(string body, int declIndex)
    {
        int lineStart = body.LastIndexOf('\n', Math.Max(0, declIndex - 1));
        if (lineStart <= 0) return false;
        int prevStart = body.LastIndexOf('\n', lineStart - 1);
        if (prevStart < 0) return false;
        string prevLine = body[(prevStart + 1)..lineStart];
        return prevLine.Contains("bounds-checked-by:", StringComparison.Ordinal);
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
