using System.Text;
using System.Text.RegularExpressions;

namespace UltimateAudit;

/// <summary>
/// 診斷：把每份階段檔的「## 五、矩陣」區塊逐列攤開，找出為什麼第十階段
/// 一列都數不到。前一版稽核在階段十得到 0 列，其餘九階段各 105 列——
/// 這種「只有一個檔案不同」的形狀，通常是那一份的列首寫法與其他九份不同。
/// </summary>
internal static class MatrixProbe
{
    public static void Run(string dir)
    {
        Console.OutputEncoding = Encoding.UTF8;
        for (int i = 1; i <= 10; i++)
        {
            string path = Directory.GetFiles(dir, $"STAGE-{i:00}-*.md").Single();
            string[] lines = File.ReadAllLines(path);
            int start = Array.FindIndex(lines, l => l.StartsWith("## 五、矩陣", StringComparison.Ordinal));
            int end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
            if (end < 0) end = lines.Length;

            int got = 0;
            string firstBody = "(none)";
            string? rejected = null;
            foreach (string line in lines[(start + 1)..end])
            {
                string t = line.Trim();
                if (!t.StartsWith('|')) continue;
                var c = t.Trim('|').Split('|').Select(x => x.Trim()).ToList();
                if (c.Count < 4) continue;
                if (firstBody == "(none)") firstBody = Describe(c[0]);
                if (IsItemId(c[0].TrimStart('★'))) got++;
                else if (rejected is null && c[0].Length > 0 && c[0] != "ID" && !c[0].StartsWith("---"))
                    rejected = Describe(c[0]);
            }
            Console.WriteLine($"階段 {i,2}: 矩陣列 {got,3}   首列 ID 欄 = {firstBody}");
            if (rejected is not null) Console.WriteLine($"          第一列被拒的 ID 欄 = {rejected}");
        }
    }

    private static string Describe(string cell) =>
        $"\"{cell}\"（長度 {cell.Length}，codepoints {string.Join(" ", cell.Select(ch => "U+" + ((int)ch).ToString("X4")))})";

    private static bool IsItemId(string s)
    {
        if (s.Length != 6 || s[0] != 'P' || s[2] != '-') return false;
        return char.IsAsciiDigit(s[1]) && s[3..].All(char.IsAsciiDigit);
    }
}
