using System.Reflection;
using System.IO;
using System.Text;

using static XinSpectCli.Tui;

namespace XinSpectCli;

/// <summary>
/// 書（百科全書）：把主倉庫的 README.md（曦覽百科，約 1.5MB、15 部 362 節）整本嵌入組件，
/// book 指令離線可讀。結構：## 第 N 部 ＝ 部、### N.M ＝ 節；book 列目錄、book N 列該部節清單、
/// book N.M 顯示節全文、book 關鍵字 搜節標題。
/// </summary>
internal static class Book
{
    private sealed record Section(string Number, string Title, string PartTitle, int StartLine, int EndLine);

    private static readonly string[] Markdown;
    private static readonly List<Section> Sections = [];

    static Book()
    {
        var asm = Assembly.GetExecutingAssembly();
        // P4-28：資源名與嵌入物對齊——v3.18 把嵌入物由 README.md 換成 docs/百科-v3.17.md 時
        // 沒同步這裡的查找名，book 一直以「找不到嵌入的百科資源」收場。查找順序：
        // 本組件嵌入（按尾名匹配，防命名慣例漂移）→ exe 旁 docs\ → exe 旁根目錄。
        // （Legacy 版已改為內容檔隨行、不再雙嵌；單檔發佈時 IncludeAllContentForSelfExtract
        // 會把內容檔打進 bundle、執行時解到 BaseDirectory，查找照樣命中。）
        string? markdown = null;
        string[] names = asm.GetManifestResourceNames();
        string? resName = names.FirstOrDefault(n => n.EndsWith("百科-v3.17.md", StringComparison.OrdinalIgnoreCase))
                       ?? names.FirstOrDefault(n => n.EndsWith("README.md", StringComparison.OrdinalIgnoreCase));
        if (resName is not null)
        {
            using var stream = asm.GetManifestResourceStream(resName)
                ?? throw new InvalidOperationException($"嵌入資源 {resName} 存在但無法開啟");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            markdown = reader.ReadToEnd();
        }
        else
        {
            string exeDir = AppContext.BaseDirectory;
            string[] candidates =
            [
                Path.Combine(exeDir, "docs", "百科-v3.17.md"),
                Path.Combine(exeDir, "百科-v3.17.md"),
            ];
            string? path = candidates.FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException(
                    "找不到百科：本組件未嵌入，且程式目錄（含 docs\\）也沒有 百科-v3.17.md——請隨 exe 發佈 docs\\百科-v3.17.md。");
            markdown = File.ReadAllText(path, Encoding.UTF8);
        }
        Markdown = markdown.Replace("\r\n", "\n").Split('\n');

        // 解析結構：## ＝ 部邊界（同時是節的結束），### ＝ 節起點
        string part = "（前言）";
        Section? current = null;
        for (int i = 0; i < Markdown.Length; i++)
        {
            string line = Markdown[i];
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                if (current is not null) Sections[^1] = current with { EndLine = i - 1 };
                string head = line[4..].Trim();
                int sp = head.IndexOf(' ');
                string number = sp > 0 ? head[..sp] : "";
                string title = sp > 0 ? head[(sp + 1)..] : head;
                current = new Section(number, title, part, i, Markdown.Length - 1);
                Sections.Add(current);
            }
            else if (line.StartsWith("## 第", StringComparison.Ordinal))
            {
                if (current is not null) Sections[^1] = current with { EndLine = i - 1 };
                part = line[3..].Trim();
                current = null;
            }
        }
    }

    /// <summary>書的總覽：部清單＋各部節數。</summary>
    public static void ShowToc()
    {
        var parts = Sections.GroupBy(s => s.PartTitle).ToList();
        // P4-27：文件以「## 第 N 部」分部；解析時第一群可能是前言（「（前言）」）——
        // 原版把前言當「第 1 部」（真第 1 部變第 2 部），且 book 0 因 part>=1 不可達。
        // 約定：有前言時前言＝第 0 部，其餘部號與文件一致；總數不含前言。
        int offset = parts.Count > 0 && parts[0].Key == "（前言）" ? 1 : 0;
        Console.WriteLine($"{Bold}曦覽百科 ・ 全書 {parts.Count - offset} 部{(offset == 1 ? "（另有前言＝第 0 部）" : "")} {Sections.Count} 節{Reset}");
        Console.WriteLine($"{Dim}用法：book <部號> 看該部節清單 ・ book <節號>（如 book 6.3）讀全文 ・ book <關鍵字> 搜節標題{Reset}");
        Console.WriteLine();
        var rows = parts.Select((g, i) => new[]
        {
            (i + 1 - offset).ToString(),
            Bold + g.Key + Reset,
            g.Count().ToString(),
        }).ToArray();
        Tui.Table(["#", "部", "節數"], rows);
        Console.WriteLine();
        Console.WriteLine($"  {Dim}整本百科＝主倉庫 README.md（離線嵌入，不連網）；章節編號與文件目錄一致。{Reset}");
    }

    /// <summary>列出某一部（依 1 起算序號）的所有節。</summary>
    public static void ShowPart(int partIndex)
    {
        var parts = Sections.GroupBy(s => s.PartTitle).ToList();
        int offset = parts.Count > 0 && parts[0].Key == "（前言）" ? 1 : 0;
        if (partIndex < 1 - offset || partIndex > parts.Count - offset)
        {
            Console.WriteLine($"{Tui.SwissRed}沒有第 {partIndex} 部（全書共 {parts.Count - offset} 部{(offset == 1 ? "＋前言（第 0 部）" : "")}）。{Reset}");
            return;
        }
        var g = parts[partIndex + offset - 1];
        Console.WriteLine($"{Bold}{g.Key}{Reset}");
        Console.WriteLine();
        var rows = g.Select(s => new[] { s.Number, s.Title }).ToArray();
        Tui.Table(["節號", "標題"], rows);
        Console.WriteLine();
        Console.WriteLine($"  {Dim}book <節號> 讀全文（如 book {g.First().Number}）。{Reset}");
    }

    /// <summary>顯示某一節全文（markdown 輕量轉純文字）。</summary>
    public static void ShowSection(string number)
    {
        var sec = Sections.FirstOrDefault(s =>
            s.Number.Equals(number, StringComparison.OrdinalIgnoreCase));
        if (sec is null)
        {
            Console.WriteLine($"{Tui.SwissRed}沒有節號 {number}。book 看目錄。{Reset}");
            return;
        }
        Console.WriteLine($"{Bold}{sec.Number} {sec.Title}{Reset}");
        Console.WriteLine($"{Dim}（{sec.PartTitle}）{Reset}");
        Console.WriteLine();

        int width;
        try { width = Math.Max(80, Console.WindowWidth - 2); }
        catch { width = 100; }   // 重定向輸出時 WindowWidth 會丟 IOException，退回 100 欄
        for (int i = sec.StartLine + 1; i <= sec.EndLine; i++)
        {
            // 連續表格列整組交給 Tui.Table（對齊）；單行照舊渲染折行
            if (Markdown[i].TrimStart().StartsWith("|"))
            {
                var tableLines = new List<string>();
                while (i <= sec.EndLine && Markdown[i].TrimStart().StartsWith("|"))
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(Markdown[i], @"^\|[\s:\-|]+\|$"))   // 跳過表頭分隔列
                        tableLines.Add(Markdown[i]);
                    i++;
                }
                i--;
                if (tableLines.Count > 0)
                {
                    var rows = tableLines
                        .Select(t => System.Text.RegularExpressions.Regex.Replace(t, "`|[*]{2}", "")
                            .Trim('|').Split('|').Select(c => c.Trim()).ToArray())
                        .ToList();
                    var headers = rows[0];
                    var body = rows.Skip(1).Select(r => Enumerable.Range(0, headers.Length)
                        .Select(ci => ci < r.Length ? r[ci] : "").ToArray());
                    var overflow = rows.Skip(1).Count(r => r.Length > headers.Length);
                    Tui.Table(headers, body);
                    if (overflow > 0)
                        Console.WriteLine($"  {Dim}（有 {overflow} 列的欄數多於表頭，超出部分未顯示——資料仍在原文。）{Reset}");
                    Console.WriteLine();
                }
                continue;
            }
            string line = RenderLine(Markdown[i]);
            if (line.Length == 0) { Console.WriteLine(); continue; }
            // 依可見寬度折行（CJK 2 格）
            foreach (var chunk in Wrap(line, width))
                Console.WriteLine("  " + chunk);
        }
        Console.WriteLine();
        Console.WriteLine($"  {Dim}book 看目錄 ・ book {NextSectionHint(sec)} 讀下一節{Reset}");
    }

    private static string NextSectionHint(Section sec)
    {
        int idx = Sections.IndexOf(sec);
        return idx >= 0 && idx + 1 < Sections.Count ? Sections[idx + 1].Number : sec.Number;
    }

    /// <summary>依節標題搜關鍵字（含節號與所屬部）。</summary>
    public static void Search(string keyword)
    {
        var hits = Sections
            .Where(s => s.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                     || s.Number.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                     || s.PartTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(30)
            .ToList();
        if (hits.Count == 0)
        {
            Console.WriteLine($"{Dim}節標題沒有命中「{keyword}」。試 book <關鍵字> 的其他詞，或 fact <關鍵字> 直接搜事實值。{Reset}");
            return;
        }
        Console.WriteLine($"{Bold}節標題命中「{keyword}」{Reset}{Dim}（{hits.Count} 筆）{Reset}");
        Console.WriteLine();
        var rows = hits.Select(s => new[] { s.Number, s.PartTitle, s.Title }).ToArray();
        Tui.Table(["節號", "所屬部", "標題"], rows);
        Console.WriteLine();
        Console.WriteLine($"  {Dim}book <節號> 讀全文。{Reset}");
    }

    /// <summary>book 的分派：無參數＝目錄；純數字 N.M（或 N）＝節（部）；其他＝關鍵字。</summary>
    public static void Run(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) { ShowToc(); return; }
        arg = arg.Trim();
        if (arg.EndsWith('.'))
            arg = arg[..^1];
        if (int.TryParse(arg, out int part) && !arg.Contains('.'))
        {
            // 純數字：優先當部號；但若剛好也是節號（如 0），以節為準前先試部
            var parts = Sections.GroupBy(s => s.PartTitle).ToList();
            if (part >= 0 && part <= parts.Count) { ShowPart(part); return; }
        }
        if (arg.Contains('.') && arg.Split('.')[0].Length <= 2 && int.TryParse(arg.Split('.')[0], out _))
        {
            ShowSection(arg);
            return;
        }
        Search(arg);
    }

    // ── markdown 輕量渲染 ────────────────────────────────────────────────

    private static string RenderLine(string line)
    {
        if (line.StartsWith("```", StringComparison.Ordinal))
            return line.Length > 3 ? $"{Dim}{line[3..]}{Reset}" : "";
        if (line.StartsWith("| ", StringComparison.Ordinal) || line.StartsWith("|-", StringComparison.Ordinal))
        {
            if (line.StartsWith("|--", StringComparison.Ordinal)
                || System.Text.RegularExpressions.Regex.IsMatch(line, @"^\|[\s:\-|]+\|$")) return ""; // 表頭分隔列不印（錨定全行——P4-27：原 Contains("---|") 連含此子串的資料列一起吞）
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            return $"{Dim}│{Reset} " + string.Join($" {Dim}│{Reset} ", cells) + $" {Dim}│{Reset}";
        }
        // 標題 → 粗體；連結 → 只留文字；行內代碼反白
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\*\*(.+?)\*\*", m => $"{Bold}{m.Groups[1].Value}{Reset}");
        if (line.StartsWith("#### ", StringComparison.Ordinal)) return $"{Bold}{line[5..]}{Reset}";
        if (line.StartsWith("### ", StringComparison.Ordinal)) return $"{SwissRed}{Bold}■ {line[4..]}{Reset}";
        if (line.StartsWith("## ", StringComparison.Ordinal)) return $"{SwissRed}{Bold}■■ {line[3..]}{Reset}";
        if (line.StartsWith("- ", StringComparison.Ordinal)) return $"{Dim}・{Reset}{line[2..]}";
        if (line.StartsWith("> ", StringComparison.Ordinal)) return $"{Dim}▏{line[2..]}{Reset}";
        return line;
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var sb = new StringBuilder();
        int w = 0;
        foreach (char c in text)
        {
            int cw = char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherLetter
                  || (c >= '\uFF01' && c <= '\uFF60') ? 2 : 1;
            if (w + cw > width - 2)
            {
                yield return sb.ToString();
                sb.Clear();
                w = 0;
            }
            sb.Append(c);
            w += cw;
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
