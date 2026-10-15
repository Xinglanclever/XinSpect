using System.Text;

namespace UltimateAudit;

/// <summary>
/// 逐階段列出「差異那一項只有它不同」的形狀。
///
/// 使用時機：稽核報告說「階段 2 只有 102 列、階段 3／7 只有 103 列」，
/// 而總檢查器說十階段各 105 列。兩個結論不能同時成立，必須找出
/// 是哪一份文件的哪一列讓兩者分道揚鑣——而且要找的是<b>列</b>，不是數字。
/// </summary>
internal static class ItemDiffProbe
{
    public static void Run(string dir)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var perStage = new List<(int Num, List<string> Ids)>();
        for (int i = 1; i <= 10; i++)
        {
            string path = Directory.GetFiles(dir, $"STAGE-{i:00}-*.md").Single();
            var ids = new List<string>();
            foreach (string line in File.ReadAllLines(path))
            {
                string t = line.Trim();
                if (!t.StartsWith('|')) continue;
                var c = t.Trim('|').Split('|').Select(x => x.Trim()).ToList();
                if (c.Count < 7) continue;                      // 矩陣列是 7 欄
                if (t.Contains("---")) continue;                 // 分隔列
                string first = c[0].TrimStart('★');
                // 識別碼一律用「本階段自己的階段號」判斷，這樣第十階段（三位數）
                // 與第一階段（一位數）走同一條路徑，不必為 10 開特例。
                string prefix = $"P{i}-";
                if (first.StartsWith(prefix, StringComparison.Ordinal)
                    && first.Length == prefix.Length + 3
                    && first[prefix.Length..].All(char.IsAsciiDigit))
                    ids.Add(first);
            }
            var nums = ids.Select(x => int.Parse(x.Split('-')[1])).OrderBy(x => x).ToList();
            var missing = Enumerable.Range(1, 105).Except(nums).ToList();
            var extra = nums.Except(Enumerable.Range(1, 105)).ToList();
            Console.WriteLine($"階段 {i,2}: {ids.Count,3} 列"
                + (missing.Count > 0 ? $"  缺：{string.Join(",", missing)}" : "")
                + (extra.Count > 0 ? $"  多：{string.Join(",", extra)}" : "")
                + (ids.Count != ids.Distinct().Count() ? "  有重複" : ""));
            perStage.Add((i, ids));
        }
    }
}
