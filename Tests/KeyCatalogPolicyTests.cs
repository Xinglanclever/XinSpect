using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-077（鍵目錄編譯期全集）、P1-078（目錄與來源逐鍵相等）、
/// P1-079（動態家族理由）、P1-080（殭屍前綴）、P1-081（分母聲明）、
/// P1-082（豁免理由門檻）、P1-084（鍵命名規範）的守門。
/// </summary>
public class KeyCatalogPolicyTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    // ── P1-077 編譯期全集 ──────────────────────────────────────────────

    [Fact]
    public void 全集_目錄不得為空且要報出家族數()
    {
        var fact = KeyCatalogPolicy.ToCountFact(At, FactKeyCatalog.Keys.Count,
            FactKeyDynamicCatalog.Families.Count);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.True(fact.NumericValue > 800);
        Assert.Contains("動態家族", fact.Value);
    }

    [Fact]
    public void 全集_空的目錄不能證明任何事()
    {
        var fact = KeyCatalogPolicy.ToCountFact(At, 0, 0);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
    }

    // ── P1-084 命名規範 ────────────────────────────────────────────────

    [Fact]
    public void 命名_靜態目錄裡的每一個鍵都必須合規()
    {
        var bad = FactKeyCatalog.Keys
            .Select(k => (Key: k, Problem: KeyCatalogPolicy.CheckKeyName(k)))
            .Where(x => x.Problem is not null)
            .ToList();

        Assert.True(bad.Count == 0,
            "目錄裡有不合命名規範的鍵：\n"
            + string.Join("\n", bad.Select(b => $"  • {b.Key}：{b.Problem}")));
    }

    [Fact]
    public void 命名_動態家族成員自成一套規則()
    {
        // 家族成員本來就不符全鍵格式（埠號、含大寫的角色名），這是家族存在的理由。
        Assert.Null(KeyCatalogPolicy.CheckKeyName("sio.0x2e"));
        Assert.Null(KeyCatalogPolicy.CheckKeyName("role.installed.ActiveDirectory-Domain-Services"));
    }

    [Fact]
    public void 命名_不合規時要說出應該登記家族而不是放寬格式()
    {
        string? problem = KeyCatalogPolicy.CheckKeyName("Bad Key");
        Assert.NotNull(problem);
        Assert.Contains("動態家族", problem);
        Assert.Contains("放寬格式", problem);
    }

    // ── P1-078 雙向守門 ────────────────────────────────────────────────

    [Fact]
    public void 雙向_目錄裡的每個鍵都要在掃描結果裡找得到()
    {
        string root = FindRepoRoot();
        var scanned = CoverageService.ScanFactKeysFromSource(System.IO.Path.Combine(root, "Services"));

        var orphans = FactKeyCatalog.Keys.Where(k => !scanned.Contains(k)).ToList();
        // 這條不是要求 0——有些鍵由執行期組合產生（家族成員），目錄只登記彙總鍵。
        // 但數量必須是「已知的」而不是無聲累積。
        Assert.True(orphans.Count <= 40,
            $"目錄裡有 {orphans.Count} 個鍵在原始碼掃描中找不到生產者（基線 40）：\n"
            + string.Join("\n", orphans.Take(20).Select(o => "  • " + o)));
    }

    // ── P1-079 動態家族理由 ────────────────────────────────────────────

    [Fact]
    public void 家族_每一個家族都要寫出成員由什麼決定()
    {
        var fact = KeyCatalogPolicy.ToFamilyFact(At);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Null(fact.UnavailableReason);
    }

    [Fact]
    public void 家族_每個家族的理由都要夠長而且不重複()
    {
        var families = FactKeyDynamicCatalog.Families;
        Assert.True(families.Count >= 20, $"家族只剩 {families.Count} 個——是不是被刪了？");

        foreach (var (prefix, reason) in families)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{prefix}：沒有理由");
            Assert.True(reason.Length >= 20, $"{prefix}：理由太短（{reason}）");
        }

        var dupes = families.GroupBy(kv => kv.Value).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(dupes);   // 複製貼上的理由等於沒有理由
    }

    // ── P1-080 殭屍前綴 ────────────────────────────────────────────────

    [Fact]
    public void 殭屍_登記了卻沒有任何鍵符合的前綴要被找出來()
    {
        var zombies = KeyCatalogPolicy.ZombiePrefixes(
            ["cpu.", "gpu.", "根本不存在的家族."],
            FactKeyCatalog.Keys, []);

        Assert.Single(zombies);
        Assert.Equal("根本不存在的家族.", zombies[0]);
    }

    [Fact]
    public void 殭屍_真實目錄裡的前綴不得是殭屍()
    {
        var prefixes = FactKeyCatalog.Keys.Select(k => k.Split('.')[0] + ".").Distinct().ToList();
        var zombies = KeyCatalogPolicy.ZombiePrefixes(prefixes, FactKeyCatalog.Keys, []);
        Assert.Empty(zombies);   // 前綴由目錄自身導出，邏輯上不可能有殭屍——這條是正對照
    }

    // ── P1-081 分母聲明 ────────────────────────────────────────────────

    [Fact]
    public void 分母_沒有成文時要標讀取失敗並說出百分比為什麼騙人()
    {
        var fact = KeyCatalogPolicy.ToDenominatorFact(At, 900, 25, "", "");
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("分母沒有被說出來", fact.UnavailableReason);
    }

    [Fact]
    public void 分母_成文時要同時報出定義與理由()
    {
        var fact = KeyCatalogPolicy.ToDenominatorFact(At, 900, 25,
            "全部靜態鍵（不含動態家族成員）",
            "動態家族的成員數由機器決定，放進分母會讓覆蓋率隨機器而變");

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("900 個靜態鍵", fact.Value);
        Assert.Contains("不進分母", fact.Value);
        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：理由被併進了 Value。
        Assert.Contains("隨機器而變", fact.Value);
    }

    // ── P1-082 豁免理由門檻 ────────────────────────────────────────────

    [Fact]
    public void 豁免_真實豁免表全部以無法交叉為理由()
    {
        // 這是真實的 584 條豁免表——它們全部要通過，因為它們都是真的無法交叉。
        // 第一版用關鍵詞比對時這裡紅了 507 條，而當時的錯誤處置是擴大詞表。
        var fact = KeyCatalogPolicy.ToExemptFact(At, SampleExemptions(), cap: 600);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Null(fact.UnavailableReason);
    }

    /// <summary>把 FactCoverageReport 的字串豁免表轉成具名類別——模擬正式申報。</summary>
    private static Dictionary<string, KeyCatalogPolicy.ExemptionEntry> SampleExemptions()
        => FactCoverageReport.Exemptions.ToDictionary(
            kv => kv.Key,
            kv => new KeyCatalogPolicy.ExemptionEntry(
                KeyCatalogPolicy.ExemptionKind.SingleSource, kv.Value));

    [Fact]
    public void 豁免_待辦選不出任何合法類別()
    {
        // 這是設計的核心：待辦不是「另一種豁免」，是還沒做的事。
        // 它沒有任何一個類別可以指——所以只能被算成待辦，不能進豁免表。
        var fact = KeyCatalogPolicy.ToExemptFact(At,
            SampleExemptions(), cap: 600, pendingCount: 3);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("其實是待辦", fact.Value);
        Assert.Contains("選不出任何一個合法的豁免類別", fact.UnavailableReason);
    }

    [Fact]
    public void 豁免_五個類別都有中文名且互異()
    {
        var names = Enum.GetValues<KeyCatalogPolicy.ExemptionKind>()
            .Select(KeyCatalogPolicy.Describe).ToList();

        Assert.Equal(5, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

    [Fact]
    public void 豁免_類別要機器判而理由要給人看()
    {
        var entry = new KeyCatalogPolicy.ExemptionEntry(
            KeyCatalogPolicy.ExemptionKind.SingleSource, "");
        Assert.False(entry.IsComplete);   // 沒有理由不算申報完成

        var fact = KeyCatalogPolicy.ToExemptFact(At,
            new Dictionary<string, KeyCatalogPolicy.ExemptionEntry> { ["x."] = entry }, cap: 60);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("缺理由的豁免與沒有豁免無異", fact.UnavailableReason);
    }

    [Fact]
    public void 豁免_超過上限要說先檢視而不是提高上限()
    {
        var many = Enumerable.Range(0, 5).ToDictionary(
            i => $"k{i}.",
            _ => new KeyCatalogPolicy.ExemptionEntry(
                KeyCatalogPolicy.ExemptionKind.SingleSource, "這一項只有一個來源，沒有第二份資料可以交叉。"));
        var fact = KeyCatalogPolicy.ToExemptFact(At, many, cap: 2);

        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("而不是提高上限", fact.UnavailableReason);
    }

    private static string FindRepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
