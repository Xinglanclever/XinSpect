using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>兩份匯出報告鍵級對照的結果（U10-PF-0002，追蹤鍵 <c>rep.diff.mode</c>）。</summary>
/// <param name="FormatBefore">A 檔的格式（html／markdown／text）。</param>
/// <param name="FormatAfter">B 檔的格式。</param>
/// <param name="KeyCountBefore">A 檔解析出的鍵數。</param>
/// <param name="KeyCountAfter">B 檔解析出的鍵數。</param>
/// <param name="Added">新增鍵（B 有、A 沒有）。<b>只有鍵名</b>。</param>
/// <param name="Removed">消失鍵（A 有、B 沒有）。<b>只有鍵名</b>。</param>
/// <param name="Changed">值變化的鍵（兩邊都有、值不同）。<b>只有鍵名——新舊值一律不進輸出</b>：
/// 報告裡有序號、MAC、主機名稱這類識別資訊，diff 的輸出經常被貼到論壇或議題，
/// 值一旦進了 diff 就等於把「遮蔽可識別資訊」整個繞過。</param>
public sealed record ReportDiff(
    string FormatBefore,
    string FormatAfter,
    int KeyCountBefore,
    int KeyCountAfter,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed)
{
    /// <summary>鍵級是否完全相同（新增、消失、變化皆空）。</summary>
    public bool Identical => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

/// <summary>
/// 報告 diff（U10-PF-0002，追蹤鍵 <c>rep.diff.mode</c>）：把兩份<b>匯出檔</b>（HTML／Markdown／純文字，
/// <see cref="ReportService"/> 的三種格式）各自解析回「鍵 → 值」，逐鍵對照出新增、消失與值變化。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼解析匯出檔而不比對活體：</b>「兩份歷史報告互比」的輸入就是兩個檔案——
/// 產生它們的行程可能早就關了。與時間膠囊比較（<c>EvidenceLabService.CompareAsync</c>，
/// 一側恆為現況）互補：那條路比的是結構化快照，這條路比的是<b>使用者手上已有的檔案</b>。
/// </para>
/// <para>
/// <b>鍵的定義：</b>報告裡每一列「名稱／數值」與表格列，以「節 › 首欄」為鍵（表格另帶小標時插在中間）——
/// 同名的欄位在不同節／不同表不互相冒充。值變化只記<b>鍵名</b>與「有變」，新舊值一概不進輸出。
/// </para>
/// <para>
/// <b>格式差異不是差異：</b>A 用 HTML、B 用 Markdown 照樣可比（三種格式共用同一份中介表示產生，
/// 鍵的形狀一致）；三種特徵都不中的檔案如實標「unknown」回空集，不硬解析出半份假結果。
/// </para>
/// </remarks>
public static partial class ReportDiffService
{
    /// <summary>本功能的追蹤鍵（U10-PF-0002）。以 const 字面寫在原始碼裡，鍵掃描才找得到
    /// （XML 註解裡的鍵掃描不到——第 271 輪教訓）。</summary>
    public const string ReportDiffKey = "rep.diff.mode";

    /// <summary>解析兩份報告並逐鍵對照。輸出只含鍵名與計數，不含任何值。</summary>
    public static ReportDiff Compare(string textBefore, string textAfter)
    {
        var before = Parse(textBefore);
        var after = Parse(textAfter);

        var added = new List<string>();
        var removed = new List<string>();
        var changed = new List<string>();

        foreach (var (key, value) in after.Keys)
            if (!before.Keys.TryGetValue(key, out var old)) added.Add(key);
            else if (!string.Equals(old, value, StringComparison.Ordinal)) changed.Add(key);
        foreach (var key in before.Keys.Keys)
            if (!after.Keys.ContainsKey(key)) removed.Add(key);

        return new ReportDiff(before.Format, after.Format, before.Keys.Count, after.Keys.Count,
            added, removed, changed);
    }

    /// <summary>一份解析結果：格式與鍵值表。公開是為了測試直接驗「解析」這一層。</summary>
    public sealed record Parsed(string Format, Dictionary<string, string> Keys);

    /// <summary>把匯出檔解析回鍵值表。格式按內容特徵辨認（副檔名不可信）。</summary>
    public static Parsed Parse(string text)
    {
        if (text.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<table", StringComparison.OrdinalIgnoreCase))
            return ParseHtml(text);
        if (text.Contains("\n| ", StringComparison.Ordinal) || text.StartsWith("| ", StringComparison.Ordinal))
            return ParseMarkdown(text);
        if (text.Contains('【') && text.Contains('】'))
            return ParseText(text);
        return new Parsed("unknown", []);
    }

    // ── Markdown ────────────────────────────────────────────────────────────
    // 產生端（ReportService.RenderMarkdown）：## 節、**小標**、| a | b | 列（表頭列的下一列是 | --- |）。

    private static Parsed ParseMarkdown(string text)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        string section = "", caption = "";
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal)) { section = line[3..].Trim(); caption = ""; continue; }
            if (line.StartsWith("**", StringComparison.Ordinal) && line.EndsWith("**", StringComparison.Ordinal))
            { caption = line[2..^2].Trim(); continue; }
            if (!line.StartsWith('|')) continue;
            // 表頭列（下一列是分隔線）不是資料；分隔線本身（| --- | --- |）也不是
            if (i + 1 < lines.Length && lines[i + 1].Trim().StartsWith("| ---", StringComparison.Ordinal)) continue;

            var cells = MdCells(line);
            if (cells.Length < 2 || cells.All(c => c.Replace("-", "").Length == 0)) continue;   // 分隔線全由 - 組成
            string key = KeyOf(section, caption, cells[0]);
            keys[key] = cells.Length == 2 ? cells[1] : string.Join(" ・ ", cells[1..]);
        }
        return new Parsed("markdown", keys);
    }

    private static string[] MdCells(string line)
    {
        string body = line.Trim().Trim('|');
        return [.. body.Split('|').Select(c => c.Trim().Replace("\\|", "|"))];
    }

    // ── HTML ────────────────────────────────────────────────────────────────
    // 產生端（ReportService.RenderHtml）把列連成一串、不按行斷——所以整份掃、按文件序取
    // <h2>（節）／<div class="cap2">（小標）／<tr>（列）；<th> 表頭不是鍵值對。

    private static Parsed ParseHtml(string text)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        string section = "", caption = "";

        foreach (Match m in HtmlToken().Matches(text))
        {
            if (m.Groups["h2"].Success) { section = Unescape(m.Groups["h2"].Value); caption = ""; }
            else if (m.Groups["cap"].Success) { caption = Unescape(m.Groups["cap"].Value); }
            else
            {
                var cells = Td().Matches(m.Groups["row"].Value)
                    .Select(t => Unescape(t.Groups[1].Value)).ToList();
                if (cells.Count < 2) continue;
                string key = KeyOf(section, caption, cells[0]);
                keys[key] = cells.Count == 2 ? cells[1] : string.Join(" ・ ", cells.Skip(1));
            }
        }
        return new Parsed("html", keys);
    }

    // ── 純文字 ──────────────────────────────────────────────────────────────
    // 產生端（ReportService.RenderText）：【節】頂格；小標頂格；資料列一律兩格內縮、
    // 欄間隔兩格空白（Pad 對齊）；表頭列的下一列是虛線。

    private static Parsed ParseText(string text)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        string section = "", caption = "";
        var lines = text.Replace("\r", "").Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string t = line.Trim();

            if (t.StartsWith('【') && t.EndsWith('】')) { section = t[1..^1]; caption = ""; continue; }
            if (t.Length == 0 || t.StartsWith('＝') || t.StartsWith("==", StringComparison.Ordinal)) continue;
            if (t.StartsWith("---", StringComparison.Ordinal) || t.StartsWith("──")) continue;
            if (t.StartsWith('※')) continue;                       // 誠實註記不是資料
            // AI 回覆（ReportService.AiVerdict 唯一寫 Pre 的節）是自由文字，縮排行會被誤當資料列——整節不當鍵源
            if (section == "AI 評價") continue;
            if (!line.StartsWith("  ", StringComparison.Ordinal))
            {
                // 頂格、非節、非分隔的行＝表格小標（產生端把 Caption 頂格寫）
                caption = t;
                continue;
            }

            // 表頭列（下一列是虛線分隔）不是資料
            if (i + 1 < lines.Length && lines[i + 1].Trim().StartsWith("---", StringComparison.Ordinal)) continue;

            // 資料列：以「兩格以上的連續空白」切第一欄與其餘（與產生端的 Pad＋兩格間隔同形）
            int split = IndexOfRun(line, 2, startIndex: 2);
            if (split < 0) continue;
            string key = KeyOf(section, caption, line[2..split].TrimEnd());
            keys[key] = line[split..].TrimStart();
        }
        return new Parsed("text", keys);
    }

    /// <summary>從 <paramref name="startIndex"/> 起找連續 <paramref name="run"/> 個空白的第一個位置。</summary>
    private static int IndexOfRun(string s, int run, int startIndex)
    {
        for (int i = Math.Max(0, startIndex); i <= s.Length - run; i++)
        {
            int j = 0;
            while (j < run && s[i + j] == ' ') j++;
            if (j == run) return i;
        }
        return -1;
    }

    // ── 共用 ────────────────────────────────────────────────────────────────

    /// <summary>鍵＝「節 › 首欄」；表格另有小標時插在中間——同名的欄位在不同節／不同表不互相冒充。</summary>
    private static string KeyOf(string section, string caption, string firstCell)
        => (section, caption) switch
        {
            ("", "") => firstCell,
            ("", _) => $"{caption} › {firstCell}",
            (_, "") => $"{section} › {firstCell}",
            _ => $"{section} › {caption} › {firstCell}",
        };

    private static string Unescape(string s) => s
        .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
        .Replace("&quot;", "\"").Replace("&#39;", "'");

    // HTML 的節／小標／列三種記號，按文件序一次掃出（具名群組讓分支判斷可讀）
    [GeneratedRegex("<h2[^>]*>(?<h2>[^<]*)</h2>|<div class=\"cap2\">(?<cap>[^<]*)</div>|<tr>(?<row>.*?)</tr>")]
    private static partial Regex HtmlToken();

    [GeneratedRegex("<td[^>]*>(.*?)</td>")]
    private static partial Regex Td();
}
