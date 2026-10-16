using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Amcache 解碼（FS-002）的單元測試。
/// 這一項的兩個紅線是「答對問題」（裝了什麼，不是跑了什麼）與「不把路徑送出去」；
/// 另外要釘住的是「空白欄位不併成一類」與「樣本少時不宣稱分布有代表性」。
/// </summary>
public class AmcacheFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    private static AmcacheEntry E(string name = "Tool", string ver = "1.0",
        string pub = "Vendor", DateTimeOffset? link = null)
        => new(name, ver, pub, link ?? new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero));

    private static AmcacheEntry[] Many(int n) =>
        Enumerable.Range(0, n).Select(i => E($"App{i}", $"1.{i % 3}", $"Pub{i % 2}")).ToArray();

    // ── 未讀取 ───────────────────────────────────────────────────────────

    [Fact]
    public void 未注入探針是不適用不是零筆()
    {
        var facts = AmcacheFactsService.Collect(At);

        Assert.Equal(3, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.All(facts, f => Assert.Null(f.NumericValue));
        Assert.Contains("未讀不等於沒裝任何程式", Fact(facts, "fs.amcache.count").UnavailableReason);
        Assert.All(facts, f => Assert.Contains("未讀不等於", f.UnavailableReason));
    }

    [Fact]
    public void 未讀取的理由說出欄位語意未對準出處()
    {
        var r = Fact(AmcacheFactsService.Collect(At), "fs.amcache.count").UnavailableReason;
        Assert.Contains("欄位語意未對準出處", r);
    }

    // ── 條目數與問題界定 ─────────────────────────────────────────────────

    [Fact]
    public void 條目數答裝了什麼不是跑了什麼()
    {
        var f = Fact(AmcacheFactsService.Collect(At, Many(5)), "fs.amcache.count");
        Assert.Contains("裝了什麼", f.Value);
        Assert.Contains("不是「跑了什麼」", f.Value);
        Assert.Contains("fs.shim.*", f.Value);
    }

    [Fact]
    public void 來源自報與解析數不同時兩個都並列()
    {
        var f = Fact(AmcacheFactsService.Collect(At, Many(5), declared: 9), "fs.amcache.count");
        Assert.Contains("來源自報 9 筆", f.Value);
        Assert.Contains("解析出 5 筆", f.Value);
        Assert.Contains("照實並列", f.Value);
    }

    [Fact]
    public void 沒有條目時說空快取不是沒裝過程式()
    {
        var facts = AmcacheFactsService.Collect(At, [], declared: 3);
        var c = Fact(facts, "fs.amcache.count");

        Assert.Equal(0, c.NumericValue);
        Assert.Contains("空的 Amcache 不是「沒裝過程式」", c.Value);
        Assert.Contains("沒有這份資料＝沒有這份資料", c.Value);
    }

    [Fact]
    public void 沒有條目時分布不以零種冒充()
    {
        var facts = AmcacheFactsService.Collect(At, []);
        Assert.Equal(FactAvailability.NotApplicable, Fact(facts, "fs.amcache.versions").Availability);
        Assert.Equal(FactAvailability.NotApplicable, Fact(facts, "fs.amcache.publishers").Availability);
        Assert.Contains("不以「0 種版本」冒充", Fact(facts, "fs.amcache.versions").UnavailableReason);
    }

    // ── 隱私 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 不列完整路徑()
    {
        var f = Fact(AmcacheFactsService.Collect(At, Many(5)), "fs.amcache.count");
        Assert.Contains("不列完整路徑", f.Value);
        Assert.Contains("fs.shim.sample", f.Value);
    }

    [Fact]
    public void 條目型別裡根本沒有路徑欄位()
    {
        // 「從一開始就沒有讀」比「記得不要寫」可靠：紀錄型別本身不帶路徑
        var props = typeof(AmcacheEntry).GetProperties().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "LinkDate", "Name", "Publisher", "Version" },
            props.OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(props, n => n.Contains("Path", StringComparison.OrdinalIgnoreCase));
    }

    // ── 分布 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 版本分布只算有版本欄位的()
    {
        var entries = new[] { E(ver: "1.0"), E(ver: "1.0"), E(ver: ""), E(ver: "2.0") };
        var f = Fact(AmcacheFactsService.Collect(At, entries), "fs.amcache.versions");

        Assert.Contains("1.0×2", f.Value);
        Assert.Contains("2.0×1", f.Value);
        Assert.Equal(2, f.NumericValue);      // 空白不算一種版本
    }

    [Fact]
    public void 全部沒有版本欄位時明說而不是造一個未知類()
    {
        var f = Fact(AmcacheFactsService.Collect(At, [E(ver: ""), E(ver: "")]), "fs.amcache.versions");
        Assert.Contains("不如實列個「版本未知」的假分類", f.Value);
    }

    [Fact]
    public void 空白發行者不併進未知那一類()
    {
        var entries = new[] { E(pub: "A"), E(pub: ""), E(pub: ""), E(pub: "unknown") };
        var f = Fact(AmcacheFactsService.Collect(At, entries), "fs.amcache.publishers");

        Assert.Contains("另有 2 筆沒有發行者欄位", f.Value);
        Assert.Contains("沒有欄位與有欄位但寫著 unknown 是兩件事", f.Value);
        Assert.Contains("unknown×1", f.Value);          // 寫著 unknown 的仍算一類
    }

    [Fact]
    public void 全部沒有發行者時明說不補未知()
    {
        var f = Fact(AmcacheFactsService.Collect(At, [E(pub: ""), E(pub: "")]), "fs.amcache.publishers");
        Assert.Contains("不補一個「未知」把它算成一類", f.Value);
    }

    [Fact]
    public void 樣本不足時自陳只作方向參考()
    {
        var few = Fact(AmcacheFactsService.Collect(At, Many(AmcacheFactsService.MinEntriesForDistribution - 1)),
            "fs.amcache.versions");
        Assert.Contains("只作方向參考", few.Value);
        Assert.Contains("不足以代表這台機器的軟體組成", few.Value);
    }

    [Fact]
    public void 樣本充足時不降級()
    {
        var many = Fact(AmcacheFactsService.Collect(At, Many(AmcacheFactsService.MinEntriesForDistribution)),
            "fs.amcache.versions");
        Assert.DoesNotContain("只作方向參考", many.Value);
    }

    [Fact]
    public void 發行者過多時收尾成其餘幾個()
    {
        var entries = Enumerable.Range(0, 20).Select(i => E(pub: $"P{i:D2}")).ToArray();
        var f = Fact(AmcacheFactsService.Collect(At, entries), "fs.amcache.publishers");

        Assert.Contains("其餘", f.Value);
        Assert.Equal(20, f.NumericValue);
    }

    // ── 痕跡不是判決 ─────────────────────────────────────────────────────

    [Fact]
    public void 值裡聲明痕跡不是判決()
    {
        var f = Fact(AmcacheFactsService.Collect(At, Many(5)), "fs.amcache.count");
        Assert.Contains("痕跡不是判決", f.Value);
        Assert.Contains("殘留與缺漏都是常態", f.Value);
    }

    [Fact]
    public void 鍵名就是目錄登記的那三把()
    {
        var keys = AmcacheFactsService.Collect(At).Select(f => f.Key).ToList();
        Assert.Equal(new[] { "fs.amcache.count", "fs.amcache.publishers", "fs.amcache.versions" },
            keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void 分布入上限以後仍報總種數()
    {
        var entries = Enumerable.Range(0, 12).Select(i => E(ver: $"9.{i}")).ToArray();
        var f = Fact(AmcacheFactsService.Collect(At, entries), "fs.amcache.versions");

        Assert.Contains("共 12 種版本", f.Value);
        Assert.Contains("其餘", f.Value);
    }
}
