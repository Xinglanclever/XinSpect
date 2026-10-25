using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 事實總表（<see cref="FactAtlasService"/>）的守門。
///
/// <para>
/// <b>這一組守的是一個很容易安靜壞掉的性質：分母。</b>
/// 這一頁存在的理由是「做了但畫面上看不到的鍵要被看見」，所以只要分母退化成
/// 「本輪收集到的事實」，它就會變成「這次掃到什麼列什麼」——而那正好是它要修的問題，
/// 而且畫面上看起來完全正常（一張整齊的表）。所以分母被三條測試釘住：
/// 目錄鍵一把不少、不重複、逐域加總等於總數。
/// </para>
/// </summary>
public class FactAtlasTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, string value, FactAvailability availability = FactAvailability.Present,
        double? numeric = null, string? reason = null)
        => new(key, "測試", $"{key} 的名稱", value, "", "單元測試", FactTrustLevel.Derived, false, At,
            numeric, availability, reason);

    [Fact]
    public void 分母是編譯期目錄_一把都不少()
    {
        var svc = new FactAtlasService();
        svc.Load([]);   // 這一輪什麼都沒收集到

        Assert.Equal(FactKeyCatalog.Keys.Count, svc.Keys.Count);
        Assert.Equal(FactKeyCatalog.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     svc.Keys.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void 沒有事實時_每一把都是本次未收集而不是0()
    {
        var svc = new FactAtlasService();
        svc.Load(null);

        Assert.All(svc.Keys, r =>
        {
            Assert.False(r.Collected);
            Assert.Equal("本次未收集", r.StateText);
            Assert.Equal("—", r.ValueText);
        });
        Assert.Equal(FactKeyCatalog.Keys.Count, svc.AbsentCount);
        Assert.Contains("未收集，不等於未實作", svc.Summary);
    }

    [Fact]
    public void 逐域加總等於目錄總數_不重不漏()
    {
        var svc = new FactAtlasService();
        svc.Load([Fact("tpm.present", "有"), Fact("ac.madt.summary", "1 張")]);

        Assert.Equal(FactKeyCatalog.Keys.Count, svc.Domains.Sum(d => d.CatalogKeys));
        // 每個域只能出現一次（重複會讓逐域加總看起來沒少，實際上有一把被算了兩次）
        var prefixes = svc.Domains.Select(d => d.Prefix).ToList();
        Assert.Equal(prefixes.Count, prefixes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(svc.Domains.Select(d => d.Prefix).OrderBy(p => p, StringComparer.Ordinal),
                     FactKeyCatalog.Keys.Select(FactAtlasService.PrefixOf).Distinct().OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(2, svc.Domains.Sum(d => d.Collected));
        Assert.Equal(2, svc.Domains.Sum(d => d.Present));
        Assert.Equal(1, svc.Domains.Single(d => d.Prefix == "tpm").Present);
    }

    [Fact]
    public void 收到但讀不到_算收到不算量到()
    {
        var svc = new FactAtlasService();
        svc.Load([Fact("sb.memmap.count", "", FactAvailability.NotSupported, null, "這台平台沒有 Type 19")]);

        var row = svc.Keys.Single(r => r.Key == "sb.memmap.count");
        Assert.True(row.Collected);
        Assert.Equal("平台不支援", row.StateText);
        Assert.Equal("—", row.ValueText);                       // 非 Present 不得帶值
        Assert.Contains("這台平台沒有 Type 19", row.Name);   // 原因寫在名稱欄後面

        var dom = svc.Domains.Single(d => d.Prefix == "sb");
        Assert.Equal(1, dom.Collected);
        Assert.Equal(0, dom.Present);
        Assert.Equal(1, dom.Unavailable);
    }

    [Fact]
    public void 讀到值的那一列_值與來源都帶出來()
    {
        var svc = new FactAtlasService();
        svc.Load([Fact("fw.doc.reconcile", "65／114 把鍵已實作")]);

        var row = svc.Keys.Single(r => r.Key == "fw.doc.reconcile");
        Assert.Equal("65／114 把鍵已實作", row.ValueText);
        Assert.Equal("讀到", row.StateText);
        Assert.Equal("單元測試", row.Source);
    }

    [Fact]
    public void 搜尋比對鍵與值_且不分大小寫()
    {
        var svc = new FactAtlasService();
        svc.Load([Fact("sb.memmap.count", "這台平台沒有 Type 19")]);

        svc.Filter = "TYPE 19";                       // 對值的比對也不分大小寫
        Assert.Contains(svc.Keys, r => r.Key == "sb.memmap.count");

        svc.Filter = "tpm.present";
        Assert.All(svc.Keys, r => Assert.Contains("tpm.present", r.Key));
        Assert.Contains("篩選中", svc.FilterNote);   // 過濾條件要可見（U10-UI-0003 措辭）
    }

    [Fact]
    public void 域篩選只留那個域()
    {
        var svc = new FactAtlasService();
        svc.Load([]);

        svc.DomainFilter = "spi";
        Assert.NotEmpty(svc.Keys);
        Assert.All(svc.Keys, r => Assert.Equal("spi", r.Domain));
        Assert.Contains(svc.DomainOptions, o => o == "spi");
        Assert.Contains(svc.DomainOptions, o => o == "");   // 空字串＝全部
    }

    [Fact]
    public void 動態家族照目錄登記_不混進目錄鍵()
    {
        var svc = new FactAtlasService();
        svc.Load([]);

        Assert.Equal(FactKeyDynamicCatalog.Families.Count, svc.Families.Count);
        Assert.DoesNotContain("pci.dev.", svc.Keys.Select(r => r.Key));   // 家族不是目錄鍵
    }

    [Theory]
    [InlineData("ac.dmar.summary", "ac")]
    [InlineData("pci.dev.00.0", "pci")]
    [InlineData("nodot", "nodot")]
    [InlineData(".leading", ".leading")]
    public void 域的第一段切法(string key, string expected)
        => Assert.Equal(expected, FactAtlasService.PrefixOf(key));

    [Fact]
    public void 三態的每一種都有中文說法_不留英文原文()
    {
        foreach (FactAvailability a in Enum.GetValues<FactAvailability>())
        {
            string text = FactAtlasService.StateText(a);
            Assert.False(string.IsNullOrWhiteSpace(text), $"{a} 沒有中文說法");
            Assert.DoesNotContain(a.ToString(), text);   // 不得原樣吐出英文列舉名
        }
    }

    [Fact]
    public void 同一把鍵多筆時取最後一筆_因為管線上每鍵唯一()
    {
        var svc = new FactAtlasService();
        svc.Load([Fact("tpm.present", "舊值"), Fact("tpm.present", "新值")]);

        Assert.Single(svc.Keys, r => r.Key == "tpm.present");
        Assert.Equal("新值", svc.Keys.Single(r => r.Key == "tpm.present").ValueText);
    }

    /// <summary>
    /// 動態家族的成員不是目錄鍵——它們身上的事實只有證據實驗室與匯出檔看得到。
    /// 這條把界線寫成測試：<b>這一頁的分母是目錄，不是執行期出現過的鍵</b>，
    /// 兩者的差別寫在頁面的「動態家族」區塊，不靠讀者自己發現。
    /// </summary>
    [Fact]
    public void 動態家族的成員不會冒充目錄鍵()
    {
        var svc = new FactAtlasService();
        svc.Load([Fact("reconcile.rule-001", "結論")]);   // 動態家族 reconcile. 的成員

        Assert.DoesNotContain(svc.Keys, r => r.Key == "reconcile.rule-001");
        Assert.True(FactKeyDynamicCatalog.Families.ContainsKey("reconcile."));
    }
}
