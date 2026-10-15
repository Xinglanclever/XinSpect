using System.Text;
using System.Text.RegularExpressions;

namespace UltimateAudit;

/// <summary>
/// 判決語彙掃描：計畫反覆強調「陳述非判決」（README §二 第八條、階段二 §六、
/// 階段七 Gate 第 1 條「零判決字串守門」），而輸出鍵名本身也會洩漏判決語意。
///
/// <para>
/// 這支掃描<b>只列出候選</b>，不下判決——因為同一個字在不同語境下意義不同：
/// `risk` 在「風險事實」（只列事實讓使用者判斷）是可接受的，
/// `ruling`（裁定）則幾乎必然越界。名單要人看過才算數。
/// </para>
/// </summary>
internal static class JudgementProbe
{
    /// <summary>判決語彙：鍵名裡出現這些字，就是候選。</summary>
    private static readonly string[] Words =
    [
        "ruling", "verdict", "risk", "score", "grade", "class", "delta",
        "safe", "unsafe", "malicious", "clean", "infected", "compromise", "vulnerab",
    ];

    public static void Run(string dir)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int hits = 0;
        for (int i = 1; i <= 10; i++)
        {
            string path = Directory.GetFiles(dir, $"STAGE-{i:00}-*.md").Single();
            string prefix = $"P{i}-";
            foreach (string line in File.ReadAllLines(path))
            {
                string t = line.Trim();
                if (!t.StartsWith('|')) continue;
                var c = t.Trim('|').Split('|').Select(x => x.Trim()).ToList();
                if (c.Count < 7 || t.Contains("---")) continue;
                string id = c[0].TrimStart('★');
                if (!id.StartsWith(prefix, StringComparison.Ordinal) || id.Length != prefix.Length + 3) continue;

                foreach (Match m in Regex.Matches(c[3], @"`([a-z][a-z0-9_.{}*]*)`"))
                {
                    string key = m.Groups[1].Value;
                    foreach (string w in Words)
                        if (key.Contains(w, StringComparison.OrdinalIgnoreCase))
                        {
                            hits++;
                            Console.WriteLine($"  階段 {i,2} {id,-9} `{key}`  ← 含「{w}」  項目：{c[1]}");
                            break;
                        }
                }
            }
        }
        Console.WriteLine($"  ---- 判決語彙候選 {hits} 條（需人工判定：改鍵名或改寫說明）");
    }
}
