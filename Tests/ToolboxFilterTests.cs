using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 工具箱搜尋核心（<see cref="ToolboxFilter"/>）與工具目錄本身的一致性測試。
/// 後者最重要的一項是「<see cref="ToolItem.Native"/> 指的頁面鍵真的存在」——
/// 打錯一個字不會編譯失敗，只會讓那枚「曦覽內建：…」標籤悄悄消失，正是需要測試盯著的失敗模式。
/// </summary>
public class ToolboxFilterTests
{
    // ── Tokenize ────────────────────────────────────────────────
    [Fact]
    public void 空查詢切不出詞彙()
    {
        Assert.Empty(ToolboxFilter.Tokenize(null));
        Assert.Empty(ToolboxFilter.Tokenize(""));
        Assert.Empty(ToolboxFilter.Tokenize("   "));
    }

    [Fact]
    public void 全角空白也視為分隔()
    {
        var t = ToolboxFilter.Tokenize("ssd　健康");
        Assert.Equal(new[] { "ssd", "健康" }, t);
    }

    [Fact]
    public void 連續空白不產生空詞彙()
        => Assert.Equal(new[] { "a", "b" }, ToolboxFilter.Tokenize("  a \t  b  "));

    // ── MatchesToken / Matches ──────────────────────────────────
    [Fact]
    public void 單一詞彙不分大小寫且可命中任一欄位()
    {
        Assert.True(ToolboxFilter.MatchesToken("CPU", "cpu-z", null));
        Assert.True(ToolboxFilter.MatchesToken("cpu", null, "處理器 CPU 規格"));
        Assert.False(ToolboxFilter.MatchesToken("gpu", "cpu-z", "處理器"));
    }

    [Fact]
    public void 空欄位不會誤判命中()
        => Assert.False(ToolboxFilter.MatchesToken("x", null, ""));

    [Fact]
    public void 全部詞彙皆命中才算符合()
    {
        Assert.True(ToolboxFilter.Matches("ssd 健康", "CrystalDiskInfo", "SSD 健康度與 SMART"));
        // 「ssd」命中、「風扇」沒命中 → 整筆不符（刻意不是模糊比對）
        Assert.False(ToolboxFilter.Matches("ssd 風扇", "CrystalDiskInfo", "SSD 健康度與 SMART"));
    }

    [Fact]
    public void 查詢為空時一律符合()
    {
        Assert.True(ToolboxFilter.Matches(null, "任何東西"));
        Assert.True(ToolboxFilter.Matches("  ", "任何東西"));
    }

    // ── Summarize ───────────────────────────────────────────────
    [Fact]
    public void 未篩選時只報數量不與狀態列重複()
    {
        var s = ToolboxFilter.Summarize(null, 90, 90);
        Assert.Contains("共 90 項工具", s);
        // 導向官方下載的說明留在 StatusLine，兩行不要長得一樣
        Assert.DoesNotContain("外部執行檔", s);
    }

    [Fact]
    public void 零命中要明說沒有符合而不是留白()
    {
        var s = ToolboxFilter.Summarize("不存在的東西", 0, 90);
        Assert.Contains("沒有符合", s);
        Assert.Contains("不存在的東西", s);
        Assert.Contains("90", s);
    }

    [Fact]
    public void 有命中時報出命中筆數與總數()
    {
        var s = ToolboxFilter.Summarize("ssd", 7, 90);
        Assert.Contains("7 / 90", s);
    }

    // ── 工具目錄一致性 ──────────────────────────────────────────
    [Fact]
    public void 每個Native頁面鍵都在註冊表裡找得到()
    {
        var svc = new ToolboxService();
        var bad = svc.Tools
            .Where(t => t.Native is { Length: > 0 } && PageRegistry.FindAny(t.Native) is null)
            .Select(t => $"{t.Name} → {t.Native}")
            .ToList();
        Assert.True(bad.Count == 0, "以下項目的 Native 頁面鍵不存在：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void 工具箱不再放曦覽自己的功能()
    {
        // 1.6.2 起工具箱只有 Windows 內建工具與第三方導向；自家功能一律走左側欄與 Ctrl+K，
        // 這裡只留不可點的「曦覽內建：X」對照標籤。若有人又把跳頁項目塞回來，這條會擋下。
        var svc = new ToolboxService();
        var bad = svc.Tools
            .Where(t => t.Kind is not (ToolKind.System or ToolKind.WebLink or ToolKind.DetectApp))
            .Select(t => $"{t.Name}（{t.Kind}）")
            .ToList();
        Assert.True(bad.Count == 0, "工具箱不該有非「系統／網頁／偵測」類的項目：\n" + string.Join("\n", bad));
        Assert.DoesNotContain("曦覽內建", svc.Tools.Select(t => t.Group));
    }

    [Fact]
    public void 五項全螢幕檢測都在實用工具裡找得到()
    {
        // 檢測從工具箱搬到「實用工具 → 硬體檢測」；這一頁不存在的話那五項就徹底沒有入口了。
        var def = PageRegistry.FindUtility("hwtest");
        Assert.NotNull(def);
        Assert.Equal("硬體檢測", def.Title);
        foreach (var kw in new[] { "螢幕", "壞點", "滑鼠", "鍵盤", "喇叭", "動態" })
            Assert.Contains(kw, def.Keywords);
    }

    [Fact]
    public void Windows內建工具不提供插槽()
    {
        var svc = new ToolboxService();
        Assert.All(svc.Tools.Where(t => t.Kind == ToolKind.System), t => Assert.False(t.CanSlot));
    }

    [Fact]
    public void 建構後篩選結果即等於全部項目()
    {
        var svc = new ToolboxService();
        Assert.Equal(svc.Tools.Count, svc.FilteredGroups.Sum(g => g.Items.Count));
    }

    [Fact]
    public void 清除搜尋會回到完整清單()
    {
        var svc = new ToolboxService { Query = "cpu" };
        Assert.True(svc.FilteredGroups.Sum(g => g.Items.Count) < svc.Tools.Count);
        svc.ClearFilter();
        Assert.Equal("", svc.Query);
        Assert.Equal(svc.Tools.Count, svc.FilteredGroups.Sum(g => g.Items.Count));
    }

    [Fact]
    public void 相同正規化網址合併而不同查詢參數不合併()
    {
        Assert.Equal(
            ToolboxIdentity.ForUrl("HTTPS://EXAMPLE.COM:443/tools/#overview"),
            ToolboxIdentity.ForUrl("https://example.com/tools"));
        Assert.NotEqual(
            ToolboxIdentity.ForUrl("https://example.com/tools?mode=read"),
            ToolboxIdentity.ForUrl("https://example.com/tools?mode=write"));
        Assert.Equal(
            ToolboxIdentity.ForUrl("https://example.com/tools/?mode=read#details"),
            ToolboxIdentity.ForUrl("https://example.com/tools?mode=read"));
    }

    [Fact]
    public void 同一路徑同參數合併為別名而保留不同參數動作()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, []);
        try
        {
            var service = new ToolboxService();
            int builtInCount = service.Tools.Count;
            var first = service.AddCustomTool(new ToolboxCustomTool
            {
                Name = "我的掃描器",
                Description = "本機工具",
                Group = "自訂工具",
                Kind = ToolKind.CustomApp,
                Target = path,
                Arguments = ["--scan", "disk 1"],
            });
            var duplicate = service.AddCustomTool(new ToolboxCustomTool
            {
                Name = "磁碟掃描",
                Group = "磁碟工具",
                Kind = ToolKind.CustomApp,
                Target = Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path)),
                Arguments = ["--scan", "disk 1"],
            });
            var differentAction = service.AddCustomTool(new ToolboxCustomTool
            {
                Name = "全碟掃描",
                Group = "自訂工具",
                Kind = ToolKind.CustomApp,
                Target = path,
                Arguments = ["--scan-all"],
            });

            Assert.True(first.Added);
            Assert.True(duplicate.Merged);
            Assert.Same(first.Item, duplicate.Item);
            Assert.Contains("磁碟掃描", first.Item.UserAliases);
            Assert.True(differentAction.Added);
            Assert.Equal(builtInCount + 2, service.Tools.Count);
            Assert.Equal(builtInCount + 2, service.FilteredGroups.Sum(g => g.Items.Count));

            var updated = service.UpdateCustomTool(first.Item, new ToolboxCustomTool
            {
                Name = "自訂磁碟掃描",
                Description = "更新過的本機工具",
                Group = "自訂工具",
                Kind = ToolKind.CustomApp,
                Target = path,
                Arguments = ["--scan", "disk 1"],
            });
            Assert.True(updated.Added);
            Assert.Contains("磁碟掃描", updated.Item.UserAliases);
            Assert.Contains("我的掃描器", updated.Item.UserAliases);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 新增網址重複項合併別名且不替換任何內建項目()
    {
        var service = new ToolboxService();
        var builtin = service.Tools.First(t => t.Name == "CPU-Z");
        int count = service.Tools.Count;
        const string duplicateUrl = "HTTPS://WWW.CPUID.COM:443/softwares/cpu-z.html#download";
        Assert.Equal(ToolboxIdentity.ForUrl(builtin.Target), ToolboxIdentity.ForUrl(duplicateUrl));
        var urlResult = service.AddCustomTool(new ToolboxCustomTool
        {
            Name = "CPU-Z 官網別名",
            Group = "自訂工具",
            Kind = ToolKind.WebLink,
            Target = duplicateUrl,
        });

        Assert.True(urlResult.Merged, $"{urlResult.Item.Name}; kind={builtin.Kind}; target={builtin.Target}; status={service.StatusLine}; "
            + $"builtinId={ToolboxIdentity.ForUrl(builtin.Target)}; newId={ToolboxIdentity.ForUrl(duplicateUrl)}");
        Assert.Same(builtin, urlResult.Item);
        Assert.Contains("CPU-Z 官網別名", builtin.UserAliases);
        Assert.Equal(count, service.Tools.Count);

        string path = Path.Combine(Environment.SystemDirectory, "dxdiag.exe");
        if (!File.Exists(path)) return;
        var dxdiag = service.Tools.First(t => t.Name == "DirectX 診斷");
        var systemResult = service.AddCustomTool(new ToolboxCustomTool
        {
            Name = "DirectX 診斷自訂捷徑",
            Group = "自訂工具",
            Kind = ToolKind.CustomApp,
            Target = path,
        });

        Assert.True(systemResult.Merged);
        Assert.Same(dxdiag, systemResult.Item);
        Assert.Contains("DirectX 診斷自訂捷徑", dxdiag.UserAliases);
        Assert.Equal(count, service.Tools.Count);
    }

    // ── 危險等級 ────────────────────────────────────────────────
    //
    // 這一組守的是「警告要有資訊量」。一枚寫著「危險」卻不說會發生什麼的徽章，
    // 和沒有徽章一樣沒用——1.9.0 把「曦覽內建：X」那枚不可點的標籤換掉，
    // 換上來的東西不能重蹈覆轍。

    [Fact]
    public void 非一般等級的工具都要寫清楚最壞情況()
    {
        foreach (var t in new ToolboxService().Tools.Where(t => t.HasRisk))
        {
            Assert.False(string.IsNullOrWhiteSpace(t.RiskNote), $"{t.Name}：標了危險等級卻沒說後果");
            Assert.DoesNotContain("請小心使用", t.RiskNote!);
            Assert.DoesNotContain("請謹慎使用", t.RiskNote!);
            Assert.True(t.RiskNote!.Length >= 20, $"{t.Name}：後果說明太短，等於沒說（{t.RiskNote}）");
            Assert.Contains(t.RiskLabel, t.RiskTip);
        }
    }

    [Fact]
    public void 一般等級的工具不掛徽章也不寫後果()
    {
        foreach (var t in new ToolboxService().Tools.Where(t => t.Risk == ToolRisk.Normal))
        {
            Assert.False(t.HasRisk);
            Assert.Equal("", t.RiskLabel);
            Assert.Equal("", t.RiskTip);
        }
    }

    [Fact]
    public void 會寫韌體或整碟抹除的那幾支必須是危險等級()
    {
        // 這幾支的共同點是「出錯要靠外部工具或送修才救得回來」，不是重開機能解決的。
        var byName = new ToolboxService().Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (string name in new[] { "Intel FPT（fptw64）", "HDD Low Level Format Tool",
                                        "RWEverything", "Thaiphoon Burner" })
        {
            Assert.True(byName.ContainsKey(name), $"目錄裡找不到「{name}」");
            Assert.Equal(ToolRisk.Danger, byName[name].Risk);
        }
    }

    [Fact]
    public void 主按鈕的提示會把危險後果一起帶上()
    {
        var fpt = new ToolboxService().Tools.First(t => t.Name.Contains("fptw64"));
        Assert.Contains(fpt.Description, fpt.Tip);
        Assert.Contains("危險：", fpt.Tip);
        Assert.Contains("SPI", fpt.Tip);
    }
}
