using System.Text;

namespace UltimateAudit;

/// <summary>
/// 依賴欄的前向引用總表：每一條前向依賴的<b>目標項名稱</b>與<b>來源項名稱</b>並列。
///
/// 有這個對照才判斷得出「這條依賴是建置順序還是交叉引用」：
/// 例如 P3-071「拓撲變更時間線」依賴 P9-030，若 P9-030 就是「硬體變更時間線」，
/// 那這條依賴的意思是「時間線格式沿用階段九那一份」——是<b>引用</b>，不是前置。
/// </summary>
internal static class DepTable
{
    public static void Run(string dir)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var items = new Dictionary<string, (int Stage, string Name, string Dep)>(StringComparer.Ordinal);
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
                items[id] = (i, c[1], c[6]);
            }
        }

        Console.WriteLine("| 來源 | 來源名稱 | 階段 | 依賴欄 | 目標項名稱 |");
        Console.WriteLine("|---|---|---:|---|---|");
        foreach (var kv in items.OrderBy(k => int.Parse(k.Key.Split('-')[0][1..])).ThenBy(k => k.Key))
        {
            var (stage, name, dep) = kv.Value;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(dep, @"P(\d{1,2})-\d{3}"))
            {
                if (int.Parse(m.Groups[1].Value) <= stage) continue;
                string target = items.TryGetValue(m.Value, out var t) ? t.Name : "（不存在）";
                Console.WriteLine($"| {kv.Key} | {name} | {stage} | {dep} | {m.Value} {target} |");
            }
        }
    }
}
