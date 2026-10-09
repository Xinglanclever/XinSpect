using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 接線守門：<b>服務寫好了、單元測試也綠，不代表有人呼叫它。</b>
/// </summary>
/// <remarks>
/// <para>
/// 這一組存在的理由是一個已經發生的結構性缺陷：本專案的服務一律「先寫服務＋接縫＋測試」，
/// 接線（進 <c>AllFacts</c>／報告匯出／UI）是另一件事，而<b>沒有任何守門在盯那件事</b>。
/// 實測（2026-10-10）用「生產碼引用數」掃出多支服務的生產引用數為 0——
/// 其中 <see cref="AudioEndpointFactsService"/>、<see cref="BootTimingFactsService"/>、
/// <see cref="NetOffloadFactsService"/> 三支的事實鍵甚至已經登記在
/// <see cref="FactKeyCatalog"/> 裡：目錄宣稱有、執行期沒人生產，
/// 覆蓋申報於是把從未存在的事實算成「已考慮」。單元測試全部通過，因為它們測的是服務本身。
/// </para>
/// <para>
/// <b>這一條檢查的等級：</b>「有沒有人引用它」——不是完整的可達性分析。
/// 引用可能來自一條本身也沒接線的路徑，所以它擋得住「完全沒人叫」，
/// 擋不住「叫了但叫不到使用者面前」。要做到後者需要目錄／註冊／申報三方對帳（見 PROGRAM-ULTIMATE）。
/// </para>
/// <para>
/// <b>為什麼以「檔案內宣告的類別名」而不是檔名為準：</b>
/// 一個檔案可以宣告兩個服務（例如 <c>ChassisAndHpaFactsService.cs</c> 裡是
/// <c>ChassisFactsService</c> 與 <c>HpaFactsService</c>）。用檔名比對會把已接線的服務誤報成孤兒。
/// </para>
/// </remarks>
public class WiringGuardTests
{
    /// <summary>
    /// 性質上「不需要生產入口」的事實服務：鍵是類別名，值是<b>理由</b>（不得為空）。
    /// 目前為空——留著是給下一個確定屬於測試專用／被別種入口使用的服務登記，
    /// 且登記時必須寫出理由；上限見 <see cref="白名單不得無限長大"/>。
    /// </summary>
    private static readonly Dictionary<string, string> NotWired = new(StringComparer.Ordinal)
    {
    };

    /// <summary>白名單上限：超過就得先重新想一遍，而不是一直往上加。</summary>
    private const int WhitelistCap = 5;

    /// <summary>檔案內宣告的事實服務類別（本專案的命名慣例）。</summary>
    private static readonly Regex FactsServiceClass =
        new(@"public\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+)*class\s+(\w*FactsService)\b", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Services")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// 生產碼檔案：排除測試、建置產物、暫存物，以及 <c>Nav/</c>。
    /// </summary>
    /// <remarks>
    /// <b>為什麼排除 Nav/：</b>那裡是說明目錄與版本沿革——裡面有大量<b>散文</b>（字串常值）
    /// 會寫到服務名。若把它算成引用，一條「某服務還沒接線」的說明文字就會讓守門誤以為它接上了；
    /// 守門因此可以被文字欺騙。（實際開發時就是先撞到這個：章程裡寫了服務名，掃描就把孤兒當成已接線。）
    /// </remarks>
    private static IEnumerable<string> ProductionFiles(string root)
    {
        string[] skipDirs = ["obj", "bin", "bin2", "Tests", "Nav", "StrykerOutput", "lang-shots", "verify-shots",
                             "verify-shots-210", "verify-shots-omni", "_verify_probe", ".orphaned-tests"];
        foreach (string path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (!path.EndsWith(".cs", StringComparison.Ordinal) && !path.EndsWith(".xaml", StringComparison.Ordinal))
                continue;
            var parts = path[(root.Length + 1)..].Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => skipDirs.Contains(p) || p.StartsWith("publish-", StringComparison.Ordinal)))
                continue;
            if (Path.GetFileName(path) == "WiringGuardTests.cs") continue;   // 本檔自己不算引用
            yield return path;
        }
    }

    [Fact]
    public void 每個事實服務都必須被生產入口引用()
    {
        string root = RepoRoot();
        string servicesDir = Path.Combine(root, "Services");
        Assert.True(Directory.Exists(servicesDir), "找不到 Services/");

        // 事實服務＝檔案內宣告、名稱以 FactsService 結尾的公開類別。
        // （掃描原始碼而不是反射：反射看得到型別存在，看不到「有沒有被引用」。）
        var declarations = new List<(string Name, string File)>();
        foreach (string file in Directory.EnumerateFiles(servicesDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string text = File.ReadAllText(file);
            foreach (Match m in FactsServiceClass.Matches(text))
                declarations.Add((m.Groups[1].Value, file));
        }
        Assert.True(declarations.Count > 0, "找不到任何 *FactsService 類別——命名慣例改了？這條守門就失效了");

        var production = ProductionFiles(root).Select(p => (Path: p, Text: File.ReadAllText(p))).ToList();

        var orphans = new List<string>();
        foreach (var (name, file) in declarations.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            if (NotWired.ContainsKey(name)) continue;
            var pattern = new Regex($@"\b{Regex.Escape(name)}\b", RegexOptions.Compiled);
            bool referenced = production.Any(x =>
                !string.Equals(Path.GetFullPath(x.Path), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)
                && pattern.IsMatch(x.Text));
            if (!referenced) orphans.Add(name);
        }

        Assert.True(orphans.Count == 0,
            "以下事實服務沒有任何生產碼引用——它們的事實鍵就算登記在 FactKeyCatalog 也不會有人生產：\n"
            + string.Join("\n", orphans.Select(o => "  • " + o))
            + "\n請二選一：(a) 接進 EvidenceCollection.LoadUsermodeFacts → AllFacts；"
            + "(b) 若確認是測試專用／性質不同，登記到 WiringGuardTests.NotWired 並寫出理由。");
    }

    [Fact]
    public void 白名單不得無限長大()
    {
        Assert.True(NotWired.Count <= WhitelistCap,
            $"接線白名單已達 {NotWired.Count} 筆（上限 {WhitelistCap}）——先重新檢視這些服務是不是該接線了");

        foreach (var (name, reason) in NotWired)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{name}：白名單必須寫出理由");
            Assert.True(reason.Trim().Length >= 10, $"{name}：理由太短（{reason}），要說得清楚為什麼不必接線");
        }
    }
}
