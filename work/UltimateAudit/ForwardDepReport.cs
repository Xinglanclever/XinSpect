using System.Text;

namespace UltimateAudit;

/// <summary>
/// 逐條列出「依賴欄指向更後面的階段」的每一條，並附上該項自己的名稱與依賴欄原文，
/// 讓修法可以逐條判斷：這是<b>建置順序</b>（要改依賴）還是<b>交叉引用</b>（要改措辭）。
///
/// 這個區分是必要的，不能一律照「階段只能依賴更早的階段」砍掉：
/// 例如「信任根與 VBS 對照」本來就要看階段七的 VBS 事實，那是引用不是前置。
/// </summary>
internal static class ForwardDepReport
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
                if (c.Count < 7) continue;
                if (t.Contains("---")) continue;
                string id = c[0].TrimStart('★');
                if (!id.StartsWith(prefix, StringComparison.Ordinal) || id.Length != prefix.Length + 3) continue;
                items[id] = (i, c[1], c[6]);
            }
        }

        foreach (var kv in items.OrderBy(k => int.Parse(k.Key.Split('-')[0][1..])).ThenBy(k => k.Key))
        {
            var (stage, name, dep) = kv.Value;
            var forward = System.Text.RegularExpressions.Regex
                .Matches(dep, @"P(\d{1,2})-\d{3}")
                .Where(m => int.Parse(m.Groups[1].Value) > stage)
                .Select(m => m.Value)
                .ToList();
            if (forward.Count == 0) continue;
            Console.WriteLine($"{kv.Key}｜{name}");
            Console.WriteLine($"    階段 {stage}；依賴欄：{dep}");
            Console.WriteLine($"    → 前向：{string.Join("、", forward)}");
        }
    }
}
