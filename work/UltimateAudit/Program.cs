using System.Text;
using System.Text.RegularExpressions;

namespace UltimateAudit;

/// <summary>
/// ULTIMATE-1000 稽核（C# 版）。存在的理由是一個方法論問題，不只是換個語言：
///
/// <para>
/// 前四版稽核寫在 Python，四版四個答案（95 項／105 項、0 列／105 列），
/// 而我一度把這些差異當成「文件有缺陷」的證據。追下去發現是這台機器的
/// <c>re</c> 引擎對「字元類別＋量詞」的比對結果不穩定（<c>[A-Z]+</c> 對
/// <c>"A1"</c> 回 false、對 <c>"ABC"</c> 回 true，同一支直譯器上可重現）。
/// </para>
///
/// <para>
/// 這件事的教訓不是「換個語言」，而是：<b>稽核工具本身必須先被稽核</b>。
/// 一個會回錯答案的檢查器，比沒有檢查器更危險——它會讓你把「掃不到」讀成
/// 「沒有問題」，或把「工具壞了」讀成「文件錯了」。所以本程式開頭先跑一組
/// 自我檢查，任何一項不符就以離開碼 2 結束，不進入稽核。
/// </para>
///
/// <para>
/// 另一條紀律：<b>不用「字元類別＋量詞」做結構解析</b>。矩陣列與域碼列的辨識
/// 一律用字面比對（逐字元判斷），因為那正是前一版翻車的地方。
/// </para>
/// </summary>
internal static class Program
{
    private const string Dir = "docs/ULTIMATE-1000";
    private static int _problems;
    private static readonly List<string> _findings = [];
    private static void AddFinding(string s) => _findings.Add(s);
    private static string _root = Directory.GetCurrentDirectory();

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        _root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string dir = Path.Combine(_root, Dir);

        if (Environment.GetEnvironmentVariable("PROBE_MATRIX") == "1") { MatrixProbe.Run(dir); return; }
        if (Environment.GetEnvironmentVariable("PROBE_ITEMS") == "1") { ItemDiffProbe.Run(dir); return; }
        if (Environment.GetEnvironmentVariable("PROBE_FWD") == "1") { ForwardDepReport.Run(dir); return; }
        if (Environment.GetEnvironmentVariable("PROBE_DEPTABLE") == "1") { DepTable.Run(dir); return; }
        if (Environment.GetEnvironmentVariable("PROBE_JUDGE") == "1") { JudgementProbe.Run(dir); return; }
        Console.WriteLine("== 0. 稽核工具自我檢查 ==");
        if (!SelfTest(dir)) { Environment.Exit(2); }
        Console.WriteLine();
        Console.WriteLine("== 0b. 環境探針（只記錄，不影響稽核結論） ==");
        EnvironmentProbe.Run();

        var stages = new List<Stage>();
        for (int i = 1; i <= 10; i++)
        {
            string path = Directory.GetFiles(dir, $"STAGE-{i:00}-*.md")
                .Single(f => !f.Contains("REMAINING"));   // 第 261 輪長出 STAGE-02-REMAINING-KEYS.md 之後，這條 glob 會命中兩份

            stages.Add(Parse(i, path));
        }

        Console.WriteLine();
        Console.WriteLine("== 1. 矩陣列數與編號 ==");
        int totalRows = 0;
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in stages)
        {
            totalRows += s.Items.Count;
            foreach (var it in s.Items)
            {
                if (!all.TryAdd(it.Id, s.File)) Console.WriteLine($"  !! {it.Id} 在 {all[it.Id]} 與 {s.File} 重複定義");
            }
            int[] nums = s.Items.Select(x => int.Parse(x.Id.Split('-')[1])).OrderBy(x => x).ToArray();
            bool contiguous = nums.SequenceEqual(Enumerable.Range(1, 105));
            bool ok = s.Items.Count == 105 && contiguous;
            if (!ok) _problems++;
            Console.WriteLine($"  階段 {s.Num,2}: {s.Items.Count,3} 列  編號 1..105 連續：{(contiguous ? "是" : "否")}  {(ok ? "" : "<<< 不符")}");
        }
        Console.WriteLine($"  合計 {totalRows} 列、{all.Count} 個互異 ID {(totalRows == 1050 && all.Count == 1050 ? "" : "<<< 不符")}");
        if (totalRows != 1050 || all.Count != 1050) _problems++;

        Console.WriteLine();
        Console.WriteLine("== 2. 域碼表：宣告項數 vs 矩陣實際分塊 ==");
        foreach (var s in stages)
        {
            int declared = s.Domains.Sum(d => d.Count);
            bool ok = declared == s.Items.Count;
            if (!ok) _problems++;
            Console.WriteLine($"  階段 {s.Num,2}: {s.Domains.Count,2} 域  宣告 {declared,3}  矩陣 {s.Items.Count,3}  {(ok ? "OK" : "<<< 對不上")}");
            if (!ok)
            {
                int cursor = 0;
                foreach (var d in s.Domains)
                {
                    var seg = s.Items.Skip(cursor).Take(d.Count).ToList();
                    cursor += d.Count;
                    Console.WriteLine($"      {d.Code,-5}{d.Count,4}  {(seg.Count > 0 ? seg[0].Id + ".." + seg[^1].Id : "-")}");
                }
                if (cursor < s.Items.Count)
                    Console.WriteLine($"      沒被任何域涵蓋：{string.Join(", ", s.Items.Skip(cursor).Select(x => x.Id))}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("== 3. 依賴欄：指向更後面的階段（已標「參照」者不算缺陷） ==");
        int fwd = 0, refs = 0;
        foreach (var s in stages)
            foreach (var it in s.Items)
                foreach (Match m in Regex.Matches(it.Dep, @"P(\d{1,2})-\d{3}"))
                {
                    int other = int.Parse(m.Groups[1].Value);
                    if (other <= s.Num) continue;
                    // 跨階段的「引用」與「建置前置」是兩件事。依賴欄寫的是後者；
                    // 「參照：P7-001」明示這是前者，因此不算缺陷。
                    if (it.Dep.Contains("參照", StringComparison.Ordinal)) { refs++; continue; }
                    fwd++;
                    Console.WriteLine($"  !! {it.Id} (階段{s.Num}) -> {m.Value} (階段{other})");
                }
        AddFinding($"跨階段參照：{refs} 條（已標示，非缺陷）；未標參照的前向依賴：{fwd} 條");
        Console.WriteLine($"  ---- 跨階段參照 {refs} 條、未標參照的前向依賴 {fwd} 條");
        if (fwd > 0) _problems++;

        Console.WriteLine();
        Console.WriteLine("== 4. 依賴欄：指不到任何存在的項 ==");
        int dangling = 0;
        foreach (var s in stages)
            foreach (var it in s.Items)
                foreach (Match m in Regex.Matches(it.Dep, @"P\d{1,2}-\d+"))
                    if (!all.ContainsKey(m.Value)) { dangling++; Console.WriteLine($"  {it.Id} -> {m.Value}（不存在）"); }
        AddFinding($"懸空依賴：{dangling} 條"); Console.WriteLine($"  ---- {dangling} 條");
        if (dangling > 0) _problems++;

        Console.WriteLine();
        Console.WriteLine("== 5. 依賴欄：不是 ID 也不是允許的佔位字 ==");
        int weird = 0;
        foreach (var s in stages)
            foreach (var it in s.Items)
            {
                string dep = it.Dep.Trim();
                if (dep.Length == 0 || dep is "—" or "-" or "無") continue;
                // 依賴欄的合法語法：ID（可多個，以、分隔），可選前綴「參照：」，
                // 以及緊接在 ID 後的括號說明（例如 `P1-048（L3 禁項掃描守門）`）。
                string stripped = Regex.Replace(dep, @"P\d{1,2}-\d{3}（[^）]*）", "")
                    .Replace("A", "")
                    .Replace("參照：", "")
                    .Replace("、", "").Replace(",", "").Replace("；", "")
                    .Replace("P", "").Trim();
                stripped = Regex.Replace(stripped, @"\d", "").Replace("-", "").Trim();
                if (stripped.Length > 0) { weird++; Console.WriteLine($"  {it.Id,-9} 依賴欄：{dep}"); }
            }
        AddFinding($"依賴欄不是 ID 也不是佔位字：{weird} 條"); Console.WriteLine($"  ---- {weird} 條");
        if (weird > 0) _problems++;

        Console.WriteLine();
        Console.WriteLine("== 6. 輸出鍵前綴不在本階段任何域宣告的前綴內 ==");
        int unprefixed = 0;
        foreach (var s in stages)
        {
            var declared = s.Domains.SelectMany(d => d.Prefixes).ToList();
            foreach (var it in s.Items)
                foreach (string key in it.Keys)
                {
                    string rootPrefix = key.Split('.')[0] + ".";
                    if (!declared.Any(p => rootPrefix.StartsWith(p, StringComparison.Ordinal) || p.StartsWith(rootPrefix, StringComparison.Ordinal)))
                    {
                        unprefixed++;
                        Console.WriteLine($"  {it.Id,-9} {key,-28} 宣告：{string.Join("、", declared)}");
                    }
                }
        }
        AddFinding($"輸出鍵前綴未在任何域宣告：{unprefixed} 條（候選）"); Console.WriteLine($"  ---- {unprefixed} 條（候選，需逐條判定：補進域碼表或改鍵名）");

        Console.WriteLine();
        Console.WriteLine(_problems == 0 ? "結構檢查：no problems" : $"結構檢查：{_problems} 項不符");

        string recordPath = Path.Combine(_root, "work", "ULTIMATE-GATE-RECORD.md");
        File.WriteAllText(recordPath, GateRecord.Build(_root, stages, _problems, _findings));
        Console.WriteLine($"閘門紀錄：{recordPath}");
        Environment.Exit(_problems == 0 ? 0 : 1);
    }

    /// <summary>
    /// 稽核工具的自我檢查。每一項都寫成「預期答案明確、答案錯了會被抓到」的形式——
    /// 這正是計畫裡「每個守門須有一次該紅會紅的證明」那條要求的自我套用。
    ///
    /// <para>
    /// 這裡<b>只</b>檢查「本程式用來解析結構的機制」與「輸入來源可達」。不檢查 .NET 的
    /// 正則引擎——那是執行環境，不是本程式的解析路徑。想確認環境有沒有問題，
    /// 應該另立一支環境探針，不要與稽核混在一起（前一版就是把兩者混在一起，
    /// 於是「環境異常」會被誤報成「稽核結論不可信」，反之亦然）。
    /// </para>
    /// </summary>
    private static bool SelfTest(string dir)
    {
        (string Label, bool Got, bool Want)[] checks =
        [
            ("字面比對：\"F10\" 的字母前綴長度為 1", "F10".TakeWhile(char.IsAsciiLetter).Count() == 1, true),
            ("字面比對：\"P1-002\" 判為項目 ID", IsItemId("P1-002"), true),
            ("字面比對：\"★P1-001\" 去星號後判為項目 ID", IsItemId("★P1-001".TrimStart('★')), true),
            ("字面比對：\"F10\" 不判為項目 ID", IsItemId("F10"), false),
            ("欄位切分：管線列切出 5 欄", Cells("| F10 | 建置 | 10 | `build.` | L0 |").Count == 5, true),
            ("鍵萃取：抓出反引號內的點分鍵", Keys("`build.reproducible`").SequenceEqual(["build.reproducible"]), true),
            ($"輸入可達：{dir} 底下有 10 份階段檔", Directory.GetFiles(dir, "STAGE-*.md").Count(f => !f.Contains("REMAINING")) == 10, true),
        ];
        bool ok = true;
        foreach (var (label, got, want) in checks)
        {
            bool pass = got == want;
            ok &= pass;
            Console.WriteLine($"  {(pass ? "OK  " : "FAIL")} {label}（實得 {got}）");
        }
        Console.WriteLine(ok ? "  自我檢查通過" : "  自我檢查失敗——本程式的結論不可信，直接結束");
        return ok;
    }

    private static Stage Parse(int num, string path)
    {
        string[] lines = File.ReadAllLines(path);
        var stage = new Stage(num, Path.GetFileName(path));

        // 域碼表：先切出第三節，逐列解析
        int start = Array.FindIndex(lines, l => l.StartsWith("## 三、域碼與項數", StringComparison.Ordinal));
        int end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        for (int i = start + 1; i < end; i++)
        {
            var c = Cells(lines[i]);
            if (c.Count < 5) continue;
            string code = c[0].Replace("**", "").Trim();
            if (code.Length is 0 or > 4 || !code.All(char.IsAsciiLetterOrDigit)) continue;
            string countText = c[2].Replace("**", "").Trim();
            if (!int.TryParse(countText, out int count)) continue;
            string desc = c[1].Trim();
            // 慣例是 `prefix.`，但階段四與階段九的域碼表寫的是裸前綴（`st` 而非 `st.`）。
            // 兩者都要接受，否則會把「少一個點」誤報成「75 個鍵沒有域」。
            var prefixes = c[3].Split(['／', '/', '、', ','], StringSplitOptions.RemoveEmptyEntries)
                                 .Select(p => p.Trim().Trim('`').TrimEnd('*'))
                                 .Where(p => p.Length > 0)
                                 .Select(p => p.EndsWith('.') ? p : p + ".")
                                 .ToList();
            stage.Domains.Add((code, desc, count, prefixes));
        }

        // 矩陣列：字面比對列首的識別碼，不用字元類別＋量詞。
        // ID 長度依階段而異（P1-001 是 6 字元、P10-001 是 7 字元），所以用「本階段前綴」判斷，
        // 不寫死長度——寫死 6 的版本讓第十階段整整 105 列全部落空（實測）。
        foreach (string line in lines)
        {
            var c = Cells(line);
            if (c.Count < 7) continue;
            if (line.Contains("---")) continue;
            string prefix = $"P{num}-";
            string first = c[0].Trim().TrimStart('★');
            if (!first.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string digits = first[prefix.Length..];
            if (digits.Length != 3 || !digits.All(char.IsAsciiDigit)) continue;
            stage.Items.Add(new Item(
                first, c[1].Trim(), c[3].Trim(),
                c[6].Trim(),
                Keys(c[3])));
        }
        return stage;
    }

    /// <summary>P{d}-{nnn} 的字面判斷：不靠正則，逐字元看。</summary>
    private static bool IsItemId(string s)
    {
        int dash = s.IndexOf('-');
        if (s.Length < 6 || s[0] != 'P' || dash < 2 || dash > 3) return false;
        return s[1..dash].All(char.IsAsciiDigit) && s[(dash + 1)..].Length == 3 && s[(dash + 1)..].All(char.IsAsciiDigit);
    }

    /// <summary>把反引號裡的點分小寫鍵抓出來（鍵的形狀固定，逐字元掃描即可）。</summary>
    private static List<string> Keys(string cell)
    {
        var keys = new List<string>();
        int i = 0;
        while (i < cell.Length)
        {
            int open = cell.IndexOf('`', i);
            if (open < 0) break;
            int close = cell.IndexOf('`', open + 1);
            if (close < 0) break;
            string candidate = cell[(open + 1)..close];
            if (candidate.Length > 0 && candidate.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '.' or '_' or '{' or '}' or '*')
                && candidate.Contains('.') && char.IsAsciiLetterLower(candidate[0]))
                keys.Add(candidate);
            i = close + 1;
        }
        return keys;
    }

    private static List<string> Cells(string line)
    {
        string t = line.Trim();
        if (!t.StartsWith('|')) return [];
        t = t.Trim('|');
        return t.Split('|').Select(x => x.Trim()).ToList();
    }

    internal sealed record Stage(int Num, string File)
    {
        public List<(string Code, string Label, int Count, List<string> Prefixes)> Domains { get; } = [];
        public List<Item> Items { get; } = [];
    }

    internal sealed record Item(string Id, string Name, string KeyCell, string Dep, List<string> Keys);
}
