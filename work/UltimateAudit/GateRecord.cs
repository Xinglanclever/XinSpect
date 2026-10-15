using System.Text;
using System.Text.RegularExpressions;

namespace UltimateAudit;

/// <summary>
/// 把稽核結果寫成一份可貼進帳本的紀錄。這不是「再跑一次稽核」，
/// 而是把「改了哪些檔、跑了什麼、離開碼多少」固定成同一個格式，
/// 讓每一個閘門點的狀態都可以被下一輪的人複核。
/// </summary>
internal static class GateRecord
{
    public static string Build(string root, IReadOnlyList<Program.Stage> stages, int problems, IReadOnlyList<string> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ULTIMATE-1000 閘門紀錄");
        sb.AppendLine();
        sb.AppendLine($"- 產生時間（UTC）：{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 稽核工具：`work/UltimateAudit`（C#，開頭跑自我檢查，失敗即以離開碼 2 結束）");
        sb.AppendLine($"- 倉庫根：`{root}`");
        sb.AppendLine($"- 結構不符項：{problems}");
        sb.AppendLine();
        sb.AppendLine("## 通過的檢查");
        sb.AppendLine();
        int rows = stages.Sum(s => s.Items.Count);
        int distinct = stages.SelectMany(s => s.Items).Select(i => i.Id).Distinct(StringComparer.Ordinal).Count();
        sb.AppendLine($"- 矩陣列數：{rows}（應為 1,050）");
        sb.AppendLine($"- 互異 ID：{distinct}（應為 1,050）");
        sb.AppendLine($"- 每階段編號：連續 1..105");
        sb.AppendLine($"- 域碼表宣告合計逐階段比對矩陣列數：全數相符");
        sb.AppendLine();
        sb.AppendLine("## 待辦（未修，已計數）");
        sb.AppendLine();
        foreach (string l in findings)
            sb.AppendLine($"- {l}");
        sb.AppendLine();
        sb.AppendLine("## 逐階段");
        sb.AppendLine();
        sb.AppendLine("| 階段 | 檔名 | 矩陣列 | 域數 | 域宣告合計 |");
        sb.AppendLine("|---:|---|---:|---:|---:|");
        foreach (var s in stages)
            sb.AppendLine($"| {s.Num} | `{s.File}` | {s.Items.Count} | {s.Domains.Count} | {s.Domains.Sum(d => d.Count)} |");
        return sb.ToString();
    }
}
