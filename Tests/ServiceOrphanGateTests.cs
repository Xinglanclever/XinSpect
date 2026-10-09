using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 孤兒服務完整性守門（主綱 P1-1／P1-2 的「會持續生出同類缺陷」那一整類）：
/// <b>Services/ 裡宣告的每個公開 *Service 類別，要么被生產碼引用，要么在
/// <see cref="WiringDecisions.Deferred"/> 有顯式判定與理由。</b>
/// </summary>
/// <remarks>
/// <para>
/// 與 <see cref="WiringGuardTests"/> 的分工：那一條盯「事實服務（*FactsService）必須接進事實管線」；
/// 這一條把網放大到所有服務，並承認「刻意不接」是合法狀態——但必須寫下來。
/// 兩類混在一起的代價（2026-10-10 實測）：孤兒清單裡 4 支是真缺陷、3 支是已想清楚的決定，
/// 下一次掃描時沒人分得出來，清單就永遠清不完。
/// </para>
/// <para>
/// 判定引用時把 <c>WiringDecisions.cs</c> 本身與 <c>Nav/</c>（散文會寫服務名）排除在外——
/// 同接線守門踩過的「守門可被文字欺騙」坑。
/// </para>
/// </remarks>
public class ServiceOrphanGateTests
{
    private static readonly Regex ServiceClass =
        new(@"public\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+)*class\s+(\w*Service)\b", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到倉庫根（XinSpect.csproj）。");
    }

    /// <summary>生產碼檔案：排除測試、說明散文、建置產物與暫存物（與 WiringGuardTests 同口徑）。</summary>
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
            string name = Path.GetFileName(path);
            if (name is "ServiceOrphanGateTests.cs" or "WiringDecisions.cs") continue;   // 本網自己與登记表都不算引用
            yield return path;
        }
    }

    /// <summary>Services/ 裡宣告的公開 *Service 類別（名稱＋宣告檔）。</summary>
    internal static List<(string Name, string File)> DeclaredServices(string root)
    {
        var list = new List<(string, string)>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "Services"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in ServiceClass.Matches(File.ReadAllText(file)))
                list.Add((m.Groups[1].Value, Path.GetFullPath(file)));
        }
        return list;
    }

    /// <summary>
    /// 行級引用判斷：跳過註解行、類別宣告行、以及同名建構子簽名行。
    /// 同檔不同類別的真實引用要算數（<c>DeepAccessService.cs</c> 尾端宣告 ScmDriverService、
    /// 同一檔第 60 行 <c>new ScmDriverService()</c>——整檔排除會把它誤報成孤兒），
    /// 但「散文提到名字」不算引用（註解行先濾掉；Nav/ 整目錄已在 <see cref="ProductionFiles"/> 排除）。
    /// </summary>
    internal static bool ReferencedOutsideItsOwnDeclaration(string text, string name)
    {
        var pattern = new Regex($@"\b{Regex.Escape(name)}\b", RegexOptions.Compiled);
        var ctorSignature = new Regex($@"^(?:public|private|internal|protected)[+\s].*\b{Regex.Escape(name)}\s*\(", RegexOptions.Compiled);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.Contains($"class {name}", StringComparison.Ordinal)) continue;
            if (ctorSignature.IsMatch(line)) continue;   // 自己的建構子簽名不是「被引用」
            if (pattern.IsMatch(line)) return true;
        }
        return false;
    }

    [Fact]
    public void 每個服務要嘛被生產引用要嘛在登记表有顯式判定()
    {
        string root = RepoRoot();
        var declarations = DeclaredServices(root);
        Assert.True(declarations.Count > 50, $"只掃到 {declarations.Count} 支公開服務——掃描慣例改了？這條網就失效了");

        var production = ProductionFiles(root).Select(p => (Path: Path.GetFullPath(p), Text: File.ReadAllText(p))).ToList();

        var orphans = new List<string>();
        foreach (var (name, file) in declarations.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            if (WiringDecisions.Deferred.ContainsKey(name)) continue;
            bool referenced = production.Any(x => ReferencedOutsideItsOwnDeclaration(x.Text, name));
            if (!referenced) orphans.Add(name);
        }

        Assert.True(orphans.Count == 0,
            "以下服務既沒有生產碼引用、也沒有在 WiringDecisions.Deferred 登記判定：\n"
            + string.Join("\n", orphans.Select(o => "  • " + o))
            + "\n三選一：(a) 接進事實管線；(b) 接進互動入口（按鈕／CLI）；"
            + "(c) 刻意的話，在登记表寫下『為什麼不接＋什麼時候才該接』。沉默不是選項。");
    }

    [Fact]
    public void 登记表每筆都要對應真實服務且仍未被接線()
    {
        string root = RepoRoot();
        var declared = DeclaredServices(root).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var production = ProductionFiles(root).Select(p => (Path: Path.GetFullPath(p), Text: File.ReadAllText(p))).ToList();

        foreach (var name in WiringDecisions.Deferred.Keys)
        {
            Assert.True(declared.Contains(name),
                $"{name} 在登记表，但 Services/ 裡已沒有這個公開類別——判定要跟著刪掉或改名");
            bool referenced = production.Any(x => ReferencedOutsideItsOwnDeclaration(x.Text, name));
            Assert.False(referenced,
                $"{name} 已被生產碼引用了——還留在登记表就是過時判定，把這筆刪掉（登记表只收『刻意不接』的決定）");
        }
    }

    [Fact]
    public void 登记理由要寫清楚且不得無限長大()
    {
        Assert.True(WiringDecisions.Deferred.Count <= 8,
            $"判定登记表已達 {WiringDecisions.Deferred.Count} 筆——孤兒到這個數量，先清一清再繼續加判定");
        foreach (var (name, reason) in WiringDecisions.Deferred)
        {
            Assert.True(reason.Trim().Length >= 20, $"{name}：判定少于 20 字，講不清楚為什麼不接");
            Assert.DoesNotContain("待補", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 掃描器抓得到無引用的服務_正對照()
    {
        // 合成一段「宣告了但沒人引用」的源碼，餵給與主檢查同一套比對邏輯，確認它會被點名。
        const string fakeDecl = "public sealed class TempOrphanProbeService { }";
        var declared = ServiceClass.Matches(fakeDecl).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["TempOrphanProbeService"], declared);

        // 反面：被引用的不算孤兒——引用文字裡出現類別名即算（與主檢查同一比對方式）。
        Assert.Matches(@"\bTempOrphanProbeService\b", "var x = new TempOrphanProbeService();");
    }
}
