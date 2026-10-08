using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 事實鍵目錄與原始碼的一致性，以及<b>「掃不到」不得冒充「沒有問題」</b>。
/// </summary>
/// <remarks>
/// 這一組存在的理由是一個已發佈的缺陷：覆蓋申報原本在執行期掃 <c>Services/</c> 原始碼，
/// 而發佈版是單一 exe、沒有那個目錄——掃不到時回報「0 個鍵」，
/// 申報再把 0 讀成「所有事實鍵都已被規則考慮或明文豁免」。
/// <b>畫面顯示一個肯定的結論，那個結論是空的。</b>
/// </remarks>
public class FactKeyCatalogTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Services")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── 目錄與原始碼一致 ──────────────────────────────────────────────────

    [Fact]
    public void 目錄與原始碼掃描逐鍵相等()
    {
        var scanned = CoverageService.ScanFactKeysFromSource(Path.Combine(RepoRoot(), "Services"));
        Assert.True(scanned.Count > 0, "原始碼掃描回空集合——掃描邏輯或路徑壞了");

        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);

        var missing = scanned.Except(catalog).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var extra = catalog.Except(scanned).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0 && extra.Count == 0,
            "事實鍵目錄與原始碼不一致——新增或移除事實後請更新 Services/FactKeyCatalog.cs。\n"
            + $"原始碼有而目錄沒有（{missing.Count}）：{string.Join("、", missing.Take(20))}\n"
            + $"目錄有而原始碼沒有（{extra.Count}）：{string.Join("、", extra.Take(20))}");
    }

    [Fact]
    public void 目錄不得有重複鍵且要排序()
    {
        Assert.Equal(FactKeyCatalog.Keys.Count, FactKeyCatalog.Keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(FactKeyCatalog.Keys.OrderBy(x => x, StringComparer.Ordinal), FactKeyCatalog.Keys);
        Assert.Equal(FactKeyCatalog.Keys.Count, FactKeyCatalog.Count);
    }

    [Fact]
    public void 目錄鍵要符合事實鍵格式()
    {
        foreach (string k in FactKeyCatalog.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(k));
            Assert.Matches(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)*$", k);
        }
    }

    // ── 缺陷的迴歸測試：掃不到不得變成「沒有問題」 ────────────────────────

    [Fact]
    public void 沒有事實鍵時_申報要說無法申報而不是全部覆蓋()
    {
        // 這是已發佈缺陷的核心：Total=0 時第一版顯示「所有事實鍵都已被規則考慮或明文豁免」
        var summary = FactCoverageReport.Report(new HashSet<string>(), []);
        string detail = CoverageService.BuildDetail(summary);

        Assert.Contains("無法申報", detail);
        Assert.Contains("不代表沒有未覆蓋的鍵", detail);
        Assert.DoesNotContain("所有事實鍵都已被規則考慮或明文豁免", detail);
        Assert.DoesNotContain("全部都已覆蓋", detail);
    }

    [Fact]
    public void 沒有規則時_申報要說無法申報()
    {
        var summary = FactCoverageReport.Report(FactKeyCatalog.Keys, []);
        string detail = CoverageService.BuildDetail(summary);

        Assert.Contains("無法申報", detail);
        Assert.Contains("不代表沒有未覆蓋的鍵", detail);
    }

    [Fact]
    public void 掃不到的目錄_回空集合而不是丟例外()
    {
        var keys = CoverageService.ScanFactKeysFromSource(Path.Combine(Path.GetTempPath(), "不存在-" + Guid.NewGuid()));
        Assert.Empty(keys);
    }

    // ── 執行期資料來源正確 ────────────────────────────────────────────────

    [Fact]
    public void 申報的規則來源是內建規則而不是外部檔案()
    {
        // 生產路徑跑的是 FactRelationRules.All；外部 JSON 沒有隨程式出貨。
        // 若申報去讀那個檔，會得到 0 條規則，進而給出假結論。
        var rules = CoverageService.RuntimeRules();
        Assert.Equal(FactRelationRules.All.Count, rules.Count);
        Assert.All(rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Id));
            Assert.NotEmpty(r.Inputs);
        });
    }

    [Fact]
    public void 用目錄與內建規則算出的申報_覆蓋數不得為零()
    {
        var s = FactCoverageReport.Report(FactKeyCatalog.Keys, CoverageService.RuntimeRules());
        Assert.True(s.Total > 0, "目錄是空的");
        Assert.True(s.RuleCount > 0, "規則數是 0");
        Assert.True(s.Covered > 0, $"已覆蓋數為 0——{s.RuleCount} 條規則的輸入鍵一個都沒對上目錄");
    }

    // ── 規則來源的申報 ────────────────────────────────────────────────────

    [Fact]
    public void 規則來源申報要說出內建幾條()
    {
        string text = RuleSourceReport.Describe();
        Assert.Contains($"內建 {RuleSourceReport.RuntimeRuleCount} 條", text);
        Assert.Contains("生產路徑實際使用的是這一份", text);
    }

    [Fact]
    public void 外部規則檔不存在時_申報要如實說沒有出貨()
    {
        // 測試環境的 BaseDirectory 下沒有 Rules/ ——與發佈版一致
        if (RuleSourceReport.ExternalRuleFilePresent) return;   // 有出貨時這條不適用

        string text = RuleSourceReport.Describe();
        Assert.Contains("沒有隨程式出貨", text);
        Assert.Contains("不是發佈版的功能", text);
    }

    [Fact]
    public void 規則來源申報不得宣稱外部形式可用_除非它真的在()
    {
        string text = RuleSourceReport.Describe();
        if (!RuleSourceReport.ExternalRuleFilePresent)
            Assert.DoesNotContain("可編輯、可分享）位於", text);
    }
}
