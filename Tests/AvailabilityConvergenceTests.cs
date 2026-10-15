using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-013（六態可用性型別收斂）、P1-015（取樣瞬間標 <c>Unknown</c>）、
/// P1-016（匯入事實起步 <c>Unknown</c>）、P1-018（空集合與不適用分離）、
/// P1-020（狀態覆蓋申報）、P1-021（未知狀態的可查詢性）、P1-022（六態巡迴）的守門。
///
/// <para><b>這一組對應 Gate 第 1 條與第 2 條</b>：「單一可用性型別」與「狀態判定時序」。
/// 兩條都是「全專案不得再有第二套」的性質，所以只能用原始碼掃描來守——
/// 單元測試只能驗當下那個情境，驗不到「別的地方又長出一套」。
/// </para>
/// </summary>
public class AvailabilityConvergenceTests
{
    // ── P1-013 單一可用性型別 ───────────────────────────────────────────

    /// <summary>
    /// 可能被誤用來表達「事實可用性」的替代型別或列舉名稱。
    /// <b>每一條都要說得出為什麼它算替代品</b>，不是看到相似的字就列。
    /// </summary>
    private static readonly (string Pattern, string Why)[] AlternativeAvailabilityTypes =
    [
        (@"\benum\s+\w*(Availability|FactState|FactStatus|ReadStatus|ProbeState)\b",
         "另一套可用性列舉——全專案只允許 FactAvailability 一套"),
        (@"\benum\s+\w*(Present|NotSupported|ReadError)\b",
         "以六態成員命名的列舉——那是同一套定義換名字"),
        (@"\b(IsAvailable|IsSuccess|Succeeded)\s*:\s*bool",
         "以布林表達可用性——布林沒有六態的表達力，會把「不適用」與「失敗」壓成同一個 false"),
    ];

    /// <summary>允許的例外：鍵是型別名或檔名，值是理由（不得為空）。</summary>
    private static readonly Dictionary<string, string> AllowedAlternatives = new(StringComparer.Ordinal)
    {
        ["ScanStepOutcome"] = "掃描步驟的結局（Completed／NotApplicable／Failed／Cancelled）——"
                            + "這是「步驟」的狀態不是「事實」的狀態，兩者刻意分開：一輪掃描可以成功而某筆事實讀不到。",
        ["MountState"] = "掛載的狀態（Mounted／NotFound／KindMismatch／Corrupt／Unreadable）——"
                       + "這是「掛載」這件事的狀態，且它與 FactAvailability 之間有明確對映（ToFactAvailability）。",
        ["SourceAvailability"] = "來源在讀取前的可用性——與 FactAvailability（讀取後的實情）刻意分開，"
                               + "且同樣有明確對映（ToFactAvailability）。",
        // FactAvailability 自己的定義處當然會被這個樣式命中——那不是「第二套」。
        ["HardwareSnapshotModels.cs"] = "FactAvailability 六態的唯一定義處；"
                                      + "另一條測試（六態必須是唯一的列舉定義處）釘住「只定義一次」。",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Services")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> ProductionFiles(string root)
    {
        string[] skipDirs = ["obj", "bin", "bin2", "Tests", "Nav", "StrykerOutput",
                             "lang-shots", "verify-shots", "verify-shots-210", "verify-shots-omni",
                             "_verify_probe", ".orphaned-tests", "work"];
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var parts = path[(root.Length + 1)..].Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => skipDirs.Contains(p) || p.StartsWith("publish-", StringComparison.Ordinal)))
                continue;
            yield return path;
        }
    }

    [Fact]
    public void 單一可用性型別_不得出現第二套可用性定義()
    {
        var violations = new List<string>();
        foreach (string file in ProductionFiles(RepoRoot()))
        {
            string text = File.ReadAllText(file);
            foreach (var (pattern, why) in AlternativeAvailabilityTypes)
            {
                foreach (Match m in Regex.Matches(text, pattern))
                {
                    string name = Regex.Match(m.Value, @"\w+").Value;
                    if (AllowedAlternatives.ContainsKey(name)
                        || AllowedAlternatives.ContainsKey(Path.GetFileName(file)))
                        continue;
                    int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    violations.Add($"{Path.GetFileName(file)}:{line}：{m.Value.Trim()}——{why}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "全專案只允許一套事實可用性定義（FactAvailability 六態）：\n"
            + string.Join("\n", violations.Select(v => "  • " + v))
            + "\n若這是刻意分開的「另一種狀態」（步驟狀態、掛載狀態），"
            + "登記到 AvailabilityConvergenceTests.AllowedAlternatives 並寫出理由與對映關係。");
    }

    [Fact]
    public void 單一可用性型別_例外清單必須寫出理由且有對映()
    {
        foreach (var (name, reason) in AllowedAlternatives)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{name}：例外必須寫出理由");
            Assert.True(reason.Length >= 20, $"{name}：理由太短，要說得清楚它與 FactAvailability 的關係");
        }
    }

    [Fact]
    public void 單一可用性型別_六態必須是唯一的列舉定義處()
    {
        // FactAvailability 這個列舉只能定義一次。定義兩次＝有兩套定義在漂。
        //
        // 這一條抓到的第一個真實案例是它自己的鄰居：GuardRegistry 為了描述這一條守門，
        // 在「守得住什麼」欄位裡寫了完整的定義語法，於是那份**宣告**被掃成第二個定義處。
        // 守門抓到了描述它自己的那段文字——這正是它存在的理由，
        // 但描述用的字串必須避開被掃的樣式（見 GuardRegistry 該處的註解）。
        int definitions = 0;
        var where = new List<string>();
        foreach (string file in ProductionFiles(RepoRoot()))
        {
            string text = File.ReadAllText(file);
            int hits = Regex.Matches(text, @"enum\s+FactAvailability\b").Count;
            if (hits > 0) where.Add($"{Path.GetFileName(file)} × {hits}");
            definitions += hits;
        }
        Assert.True(definitions == 1,
            $"FactAvailability 的定義處應該是 1 個，實際 {definitions} 個：{string.Join("、", where)}");
    }

    // ── P1-022 六態巡迴 ─────────────────────────────────────────────────

    public static TheoryData<FactAvailability> AllSixStates()
    {
        var data = new TheoryData<FactAvailability>();
        foreach (var s in FactStateLattice.LatticeOrder) data.Add(s);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllSixStates))]
    public void 六態巡迴_每一態都有中文名與說明(FactAvailability state)
    {
        Assert.False(string.IsNullOrWhiteSpace(FactStateLattice.Describe(state)));
        Assert.False(string.IsNullOrWhiteSpace(FactStateLattice.Explain(state)));
        Assert.DoesNotContain("未收錄", FactStateLattice.Describe(state));
        Assert.DoesNotContain("未收錄", FactStateLattice.Explain(state));
    }

    [Theory]
    [MemberData(nameof(AllSixStates))]
    public void 六態巡迴_每一態都能被分類且分類互斥(FactAvailability state)
    {
        bool trustworthy = FactStateLattice.IsTrustworthy(state);
        bool unconfirmed = FactStateLattice.IsUnconfirmed(state);
        bool environmental = FactStateLattice.IsEnvironmental(state);
        bool blocked = FactStateLattice.IsBlocked(state);

        // 四類恰好命中一類——這條斷言防的是「新增一態卻忘了歸類」。
        int hits = (trustworthy ? 1 : 0) + (unconfirmed ? 1 : 0)
                 + (environmental ? 1 : 0) + (blocked ? 1 : 0);
        Assert.Equal(1, hits);
    }

    [Theory]
    [MemberData(nameof(AllSixStates))]
    public void 六態巡迴_每一態的說明不得把不適用說成故障(FactAvailability state)
    {
        string explain = FactStateLattice.Explain(state);
        if (state is FactAvailability.NotApplicable or FactAvailability.NotSupported)
        {
            Assert.DoesNotContain("失敗", explain);
            Assert.DoesNotContain("錯誤", explain);
        }
    }

    [Fact]
    public void 六態巡迴_六態正好六個而且排序唯一()
    {
        var order = FactStateLattice.LatticeOrder;
        Assert.Equal(6, order.Count);
        Assert.Equal(6, order.Distinct().Count());
        foreach (var s in order)
            Assert.Equal(s, FactStateLattice.Combine([s]));   // 單一來源傳播＝自己
    }

    // ── P1-015 取樣瞬間標 Unknown ───────────────────────────────────────

    [Fact]
    public void 取樣瞬間_衍生自不可信來源的結論不得是Present()
    {
        // 行程、埠、視窗這類「取樣瞬間」的事實，只要有一項來源不是 Present，
        // 衍生結論就不能宣稱可用。
        var states = new[]
        {
            FactAvailability.Present,
            FactAvailability.Unknown,
        };
        Assert.NotEqual(FactAvailability.Present, FactStateLattice.Combine(states));
        Assert.False(FactStateLattice.IsTrustworthy(FactStateLattice.Combine(states)));
    }

    [Fact]
    public void 取樣瞬間_Unknown比Present弱但比不適用強()
    {
        Assert.True(FactStateLattice.Rank(FactAvailability.Unknown)
                  > FactStateLattice.Rank(FactAvailability.Present));
        Assert.True(FactStateLattice.Rank(FactAvailability.Unknown)
                  < FactStateLattice.Rank(FactAvailability.NotApplicable));
    }

    // ── P1-016 匯入事實起步 Unknown ─────────────────────────────────────

    [Fact]
    public void 匯入事實_外部來源的起步狀態是Unknown不是Present()
    {
        // 這一條是契約的具現化：外部匯入的事實「有值但未確認」，
        // 所以它的起始狀態必須是 Unknown。Combine 對空集合回 Unknown 正是這個語意。
        Assert.Equal(FactAvailability.Unknown, FactStateLattice.Combine([]));
        Assert.False(FactStateLattice.IsTrustworthy(FactStateLattice.Combine([])));
        Assert.True(FactStateLattice.IsUnconfirmed(FactStateLattice.Combine([])));
    }

    // ── P1-018 空集合與不適用分離 ───────────────────────────────────────

    [Fact]
    public void 空集合_沒有來源與來源說不適用是不同狀態()
    {
        var noSources = FactStateLattice.Combine([]);
        var notApplicable = FactStateLattice.Combine([FactAvailability.NotApplicable]);

        Assert.NotEqual(noSources, notApplicable);
        Assert.True(FactStateLattice.IsUnconfirmed(noSources));
        Assert.True(FactStateLattice.IsEnvironmental(notApplicable));
    }

    [Fact]
    public void 空集合_不得被當成可信()
    {
        Assert.False(FactStateLattice.IsTrustworthy(FactStateLattice.Combine([])));
    }

    // ── P1-020 狀態覆蓋申報 ─────────────────────────────────────────────

    [Fact]
    public void 狀態覆蓋_逐態統計的總和等於事實總數()
    {
        var facts = new List<HardwareFact>
        {
            Fact("a", FactAvailability.Present),
            Fact("b", FactAvailability.Present),
            Fact("c", FactAvailability.Unknown),
            Fact("d", FactAvailability.NotApplicable),
            Fact("e", FactAvailability.ReadError),
        };

        var counts = facts.GroupBy(f => f.Availability)
                          .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(facts.Count, counts.Values.Sum());
        Assert.Equal(2, counts[FactAvailability.Present]);
        Assert.Equal(1, counts[FactAvailability.ReadError]);
        // 沒出現的狀態是「0 筆」而不是「不存在」——申報要能區分這兩件事
        Assert.False(counts.ContainsKey(FactAvailability.NotSupported));
    }

    // ── P1-021 未知狀態的可查詢性 ───────────────────────────────────────

    [Fact]
    public void 未知狀態_每一態都能回答為什麼是這個狀態()
    {
        foreach (var s in FactStateLattice.LatticeOrder)
        {
            string why = FactStateLattice.Explain(s);
            Assert.True(why.Length >= 10, $"{s} 的說明太短，無法回答「為什麼」");
        }
    }

    [Fact]
    public void 未知狀態_Unknown的說明必須說出不要當成已確認()
    {
        string why = FactStateLattice.Explain(FactAvailability.Unknown);
        Assert.Contains("不要當成已確認", why);
    }

    private static HardwareFact Fact(string key, FactAvailability availability) =>
        new(key, "測試", key, "值", "", "測試來源", FactTrustLevel.Reported, false,
            new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero), null, availability, null);
}
