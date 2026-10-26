using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 內建知識表的清冊（第 283 輪）。
///
/// <para>
/// <b>這一組守的不是「表準不準」，而是「描述表的那句話有沒有在說謊」。</b>
/// <c>KnowledgePackService.BuiltinSnapshot</c> 原本是手寫字串，從 v2.66 起停在
/// 「OuiKnowledge／bugcheck 子集；打包於 2026-10-11」——而內建表後來長到九張、約兩萬多條。
/// 那句字串是畫面上「內建表有哪些東西」的<b>唯一</b>說明，落後六個版本沒有任何跡象。
/// </para>
///
/// <para>
/// <b>所以測試分兩層：</b>①條數是不是真的從表本身數出來（不是手抄，改表就不會分岔）；
/// ②「沒有日期」與「日期是今天」有沒有被分開（清冊最常在這一步騙人）。
/// </para>
/// </summary>
public class BuiltinKnowledgeInventoryTests
{
    /// <summary>量測時刻刻意選在最後一個快照日之後——避免「未來日期」警示干擾其他斷言。</summary>
    private static readonly DateTimeOffset At = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<BuiltinKnowledgeInventory.Entry> Entries =>
        BuiltinKnowledgeInventory.Entries;

    private static HardwareFact One(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    // ── 清冊本身 ──────────────────────────────────────────────────────────

    [Fact]
    public void 清冊列出九張內建知識表()
    {
        Assert.Equal(9, Entries.Count);
        Assert.All(Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
            Assert.False(string.IsNullOrWhiteSpace(e.Source));
            Assert.True(e.Entries > 0, $"{e.Slug} 的條數是 {e.Entries}——空表不該列進清冊冒充有內容");
        });
    }

    [Fact]
    public void 表的鍵末段是ASCII小寫且不重複()
    {
        var bad = Entries.Where(e => !Regex.IsMatch(e.Slug, "^[a-z][a-z0-9-]*$"))
            .Select(e => e.Slug).ToList();
        Assert.True(bad.Count == 0,
            "鍵一路都是 ASCII——表名可以是中文，鍵不行：" + string.Join("、", bad));

        var dup = Entries.GroupBy(e => e.Slug, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dup.Count == 0, "重複的鍵末段會讓逐表鍵互相覆蓋：" + string.Join("、", dup));
    }

    /// <summary>
    /// <b>清冊的條數必須是「問表」拿到的，不是打字打進去的。</b>
    /// 手抄的數字在下一張表擴充時就分岔，而且分岔不會有任何跡象。
    /// </summary>
    [Fact]
    public void 條數由表本身數出來_不是手抄()
    {
        var pci = PciKnowledge.Counts();
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["pci-class"] = pci.BaseClasses + pci.SubClasses,
            ["pci-vendor-curated"] = pci.Vendors,
            ["pci-registry"] = PciIdDatabase.VendorCount + PciIdDatabase.DeviceCount,
            ["superio"] = SuperIoKnowledge.Count,
            ["oui"] = OuiKnowledge.Count,
            ["cta-vic"] = VicKnowledge.Count,
            ["smart"] = SmartAttributeKnowledge.TotalCount,
            ["bugcheck-code"] = BugCheck.Count,
            ["bugcheck-symbol"] = KernelDumpDecoder.TableCount(),
        };

        Assert.Equal(expected.Count, Entries.Count);
        Assert.All(Entries, e =>
            Assert.True(expected.TryGetValue(e.Slug, out var want) && want == e.Entries,
                $"{e.Slug}：清冊寫 {e.Entries}，表本身數出來是 "
                + (expected.TryGetValue(e.Slug, out var w) ? w.ToString(CultureInfo.InvariantCulture) : "（清冊多了一張沒人認得的表）")));
    }

    [Fact]
    public void 總條數等於各表之和()
    {
        Assert.Equal(Entries.Sum(e => e.Entries), BuiltinKnowledgeInventory.TotalEntries);
        Assert.True(BuiltinKnowledgeInventory.TotalEntries > 20_000,
            $"內建知識表總條數只有 {BuiltinKnowledgeInventory.TotalEntries}——PCI 登錄檔那張沒被數進來？");
    }

    [Fact]
    public void 擔保三級都真的有人用_不是裝飾()
    {
        // 三級不是分類癖：規格錯了是實作錯，上游登錄檔錯了是資料舊，兩者的處置完全不同。
        // 若某一級永遠是 0，就代表這份分類只是文字。
        foreach (var g in new[]
                 {
                     BuiltinKnowledgeInventory.Guarantee.Specification,
                     BuiltinKnowledgeInventory.Guarantee.Curated,
                     BuiltinKnowledgeInventory.Guarantee.Upstream,
                 })
        {
            Assert.True(Entries.Any(e => e.Guarantee == g), $"擔保等級 {g} 沒有任何一張表在用");
        }
    }

    // ── 「沒有日期」不是「日期是今天」 ─────────────────────────────────────

    [Fact]
    public void 沒有抓取日期的表如實留空不編()
    {
        var undated = Entries.Where(e => e.Snapshot is null).Select(e => e.Slug).ToList();
        Assert.NotEmpty(undated);   // pci-class／pci-vendor-curated／oui／bugcheck-code
        Assert.All(Entries.Where(e => e.Snapshot is null),
            e => Assert.Equal("（無抓取日期）", e.SnapshotText));
    }

    [Fact]
    public void 有日期的表數與清冊一致()
    {
        Assert.Equal(Entries.Count(e => e.Snapshot is not null), BuiltinKnowledgeInventory.DatedTables);
        Assert.True(BuiltinKnowledgeInventory.DatedTables < Entries.Count,
            "每一張表都有日期？至少 OUI／PCI 類別碼／bugcheck 是沒有抓取日的——這個斷言紅了代表有人補了假日期");
    }

    [Fact]
    public void 最舊快照取有日期表的最小值()
    {
        var want = Entries.Where(e => e.Snapshot is not null).Min(e => e.Snapshot!.Value);
        Assert.Equal(want, BuiltinKnowledgeInventory.OldestSnapshot());

        // pci.ids 的標籤是 2026.10.01，必須被當成日期而不是被跳過。
        Assert.Equal(new DateOnly(2026, 10, 1), BuiltinKnowledgeInventory.OldestSnapshot());
    }

    [Fact]
    public void 一張有日期的都沒有時最舊鍵是NotApplicable而不是零()
    {
        var undatedOnly = Entries.Where(e => e.Snapshot is null).ToList();
        var facts = BuiltinKnowledgeInventory.Collect(At, undatedOnly);

        var oldest = One(facts, BuiltinKnowledgeInventory.OldestKey);
        Assert.Equal(FactAvailability.NotApplicable, oldest.Availability);
        Assert.Null(oldest.NumericValue);                 // 「無從回答」不得帶數字
        Assert.False(string.IsNullOrWhiteSpace(oldest.UnavailableReason));
        Assert.Contains("不是「最舊的是今天」", oldest.UnavailableReason);

        Assert.Null(BuiltinKnowledgeInventory.OldestSnapshot(undatedOnly));
        Assert.Contains("這些表都沒有抓取日期",
            BuiltinKnowledgeInventory.SnapshotStatement(undatedOnly));
    }

    [Fact]
    public void 宣告日期晚於量測日要如實標出而不代為修正()
    {
        // CTA VIC 那張表宣告的抓取日是 2026-10-27（比當時的系統日期還晚）。
        // 處置不是把日期改成「看起來合理」的那一天——那会把查得到的矛盾变成查不到的假資料。
        var entries = new List<BuiltinKnowledgeInventory.Entry>
        {
            new("future", "未來日期的表", 3,
                BuiltinKnowledgeInventory.Guarantee.Upstream, new DateOnly(2026, 12, 1), "測試"),
        };
        var fact = One(BuiltinKnowledgeInventory.Collect(At, entries), "kp.builtin.item.future");

        Assert.Contains("晚於量測日", fact.Value);
        Assert.Contains("2026-12-01", fact.Value);   // 照登原值
        Assert.Contains("不代為修正", fact.Value);
        Assert.Equal(3, fact.NumericValue);          // 條數照樣是數字
    }

    // ── 快照聲明的推導 ────────────────────────────────────────────────────

    [Fact]
    public void 快照聲明由清冊推導_含張數與總條數與最舊日期()
    {
        string s = BuiltinKnowledgeInventory.SnapshotStatement();
        Assert.Contains($"{Entries.Count} 張知識表", s);
        Assert.Contains(BuiltinKnowledgeInventory.TotalEntries.ToString("N0", CultureInfo.CurrentCulture), s);
        Assert.Contains("2026-10-01", s);
        Assert.Contains("最舊的快照是", s);
    }

    [Fact]
    public void 畫面上的內建快照說明就是清冊聲明_不再是手寫字串()
    {
        Assert.Equal(BuiltinKnowledgeInventory.SnapshotStatement(),
            KnowledgePackService.BuiltinSnapshot);

        // 舊字串的痕跡：這三個字串在 v2.66 之後就沒對過，留著代表有人又寫死了一份。
        Assert.DoesNotContain("v2.66", KnowledgePackService.BuiltinSnapshot);
        Assert.DoesNotContain("打包於 2026-10-11", KnowledgePackService.BuiltinSnapshot);
    }

    // ── 事實輸出 ──────────────────────────────────────────────────────────

    [Fact]
    public void Collect產出三把固定鍵加每張表一把()
    {
        var facts = BuiltinKnowledgeInventory.Collect(At);

        Assert.Equal(Entries.Count + 3, facts.Count);
        Assert.Equal(Entries.Count, One(facts, BuiltinKnowledgeInventory.TablesKey).NumericValue);
        Assert.Equal(BuiltinKnowledgeInventory.TotalEntries,
            One(facts, BuiltinKnowledgeInventory.EntriesKey).NumericValue);

        // 逐表一把，一把不多一把不少。
        var items = facts.Where(f => f.Key.StartsWith("kp.builtin.item.", StringComparison.Ordinal)).ToList();
        Assert.Equal(Entries.Count, items.Count);
        Assert.Equal(
            Entries.Select(e => "kp.builtin.item." + e.Slug).OrderBy(k => k, StringComparer.Ordinal),
            items.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void 逐表鍵的數字就是該表條數()
    {
        var facts = BuiltinKnowledgeInventory.Collect(At);
        foreach (var e in Entries)
        {
            var f = One(facts, "kp.builtin.item." + e.Slug);
            Assert.Equal(e.Entries, f.NumericValue);
            Assert.Contains($"{e.Entries:N0} 條", f.Value);
            Assert.Contains(e.GuaranteeText, f.Value);
            Assert.Contains(e.Source, f.Value);
        }
    }

    [Fact]
    public void 張數鍵自己說明張數不等於品質()
    {
        // 「九張表」聽起來很厲害，但其中五張是社群維護的上游登錄檔。
        // 這句話必須留在事實值裡，否則彙總數字會替品質背書。
        var f = One(BuiltinKnowledgeInventory.Collect(At), BuiltinKnowledgeInventory.TablesKey);
        Assert.Contains("張數不是品質", f.Value);
        Assert.Contains("上游登錄檔", f.Value);
    }

    [Fact]
    public void 執行期產生的每一把鍵都在目錄或動態家族裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var homeless = BuiltinKnowledgeInventory.Collect(At)
            .Select(f => f.Key)
            .Where(k => !catalog.Contains(k) && !FactKeyDynamicCatalog.Matches(k))
            .ToList();
        Assert.True(homeless.Count == 0,
            "這些鍵不在目錄也不在任何動態家族，覆蓋申報會少算它們：" + string.Join("、", homeless));
    }

    [Fact]
    public void 逐表鍵是家族成員_不冒充目錄鍵()
    {
        // 表有幾張是本版的事實，成員由清冊決定——所以走家族，不進靜態目錄。
        Assert.DoesNotContain(FactKeyCatalog.Keys,
            k => k.StartsWith("kp.builtin.item.", StringComparison.Ordinal));
        Assert.True(FactKeyDynamicCatalog.Matches("kp.builtin.item.superio"));
    }
}
