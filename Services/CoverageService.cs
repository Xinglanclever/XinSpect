using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 覆蓋申報的 UI 服務：把「事實鍵的對帳覆蓋」與「知識表收錄率」兩份申報攤到設定頁。
/// </summary>
/// <remarks>
/// <para>
/// 這兩份申報的共同點是：它們申報的都是<b>「我們知道自己不知道什麼」</b>。
/// 這種東西不會產生任何錯誤訊息，所以特別需要一個看得見的地方。
/// </para>
/// <para>
/// 事實鍵的掃描走原始碼檔案（與 <c>FactCoverageReportTests</c> 同一套正則）——
/// 這是刻意的：讓畫面上看到的數字與測試檢查的數字來自同一個方法，
/// 兩邊各寫一份遲早會漂移。
/// </para>
/// </remarks>
public sealed class CoverageService : ObservableObject
{
    private bool _loaded;

    /// <summary>事實鍵對帳覆蓋的申報（一行結論）。</summary>
    public string RuleCoverageHeadline { get; private set; } = "尚未計算。";

    /// <summary>事實鍵對帳覆蓋的細節：未覆蓋的鍵與它們代表什麼。</summary>
    public string RuleCoverageDetail { get; private set; } = "";

    /// <summary>知識表收錄率的整體申報。</summary>
    public string KnowledgeSummary { get; private set; } = "尚未計算。";

    /// <summary>逐表申報。</summary>
    public ObservableCollection<string> KnowledgeTables { get; } = [];

    /// <summary>第一次進頁時計算一次。純讀檔與字串掃描，不碰硬體、不需權限。</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Refresh();
    }

    /// <summary>重新計算。</summary>
    public void Refresh()
    {
        try
        {
            var keys = ScanFactKeys();
            var rules = LoadRules();
            var summary = FactCoverageReport.Report(keys, rules);

            RuleCoverageHeadline = summary.Headline;
            RuleCoverageDetail = summary.Uncovered == 0
                ? "所有事實鍵都已被規則考慮或明文豁免。"
                : $"尚未被任何規則碰到的 {summary.Uncovered} 個鍵（前 12 個）："
                  + string.Join("、", summary.UncoveredKeys.Take(12))
                  + (summary.Uncovered > 12 ? "…" : "")
                  + "。未覆蓋不是錯誤——它只代表那個數字目前沒有第二個來源可以交叉；"
                  + "要嘛補一條對帳規則，要嘛加進 FactCoverageReport.Exemptions 並寫明理由。";

            KnowledgeSummary = KnowledgeCoverage.Summary();
            KnowledgeTables.Clear();
            foreach (var t in KnowledgeCoverage.Tables) KnowledgeTables.Add(t.Describe());
        }
        catch (Exception ex)
        {
            Diag.Swallow("覆蓋申報計算", ex, "申報維持尚未計算");
            RuleCoverageHeadline = "無法計算（" + ex.Message + "）";
            RuleCoverageDetail = "";
        }

        OnPropertyChanged(nameof(RuleCoverageHeadline));
        OnPropertyChanged(nameof(RuleCoverageDetail));
        OnPropertyChanged(nameof(KnowledgeSummary));
    }

    /// <summary>從程式碼掃出事實鍵（與測試同一套正則）。找不到原始碼時回空集合，不假裝有資料。</summary>
    internal static HashSet<string> ScanFactKeys()
    {
        var patterns = new[]
        {
            new Regex(@"new HardwareFact\(\s*\$?""([a-z][a-z0-9_.\[\]-]*)"""),
            new Regex(@"new\(\s*\$?""([a-z][a-z0-9_.\[\]-]*)""\s*,\s*(?:Category|\$?_?cat)"),
            new Regex(@"(?:const|static readonly)\s+string\s+\w*[Kk]ey\w*\s*=\s*""([a-z][a-z0-9_.\[\]-]*)"""),
            new Regex(@"FactKey\s*=\s*""([a-z][a-z0-9_.\[\]-]*)"""),
        };
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in EnumerateSourceFiles())
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            foreach (var p in patterns)
                foreach (Match m in p.Matches(text))
                    keys.Add(m.Groups[1].Value.TrimEnd('.'));
        }
        return keys;
    }

    /// <summary>讀規則檔。找不到時回空清單——申報會顯示全部未覆蓋，而不是假裝有規則。</summary>
    internal static List<(string Id, IReadOnlyList<string> Inputs)> LoadRules()
    {
        var list = new List<(string, IReadOnlyList<string>)>();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Rules", "builtin.json");
            if (!File.Exists(path)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var r in doc.RootElement.GetProperty("rules").EnumerateArray())
            {
                string id = r.GetProperty("id").GetString() ?? "";
                var inputs = r.GetProperty("inputs").EnumerateArray()
                    .Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
                list.Add((id, inputs));
            }
        }
        catch (Exception ex) { Diag.Swallow("規則檔讀取", ex, "覆蓋申報將顯示全部未覆蓋"); }
        return list;
    }

    /// <summary>找 Services 下的原始碼。開發環境在倉庫內；發佈後沒有原始碼就回空。</summary>
    private static IEnumerable<string> EnumerateSourceFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Services")))
            dir = dir.Parent;
        if (dir is null) yield break;
        foreach (string f in Directory.EnumerateFiles(
                     Path.Combine(dir.FullName, "Services"), "*.cs", SearchOption.AllDirectories))
            if (!f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                yield return f;
    }
}
