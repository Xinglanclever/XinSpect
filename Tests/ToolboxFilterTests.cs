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
    public void 未篩選時說明第三方一律導向官方下載()
    {
        var s = ToolboxFilter.Summarize(null, false, 90, 90);
        Assert.Contains("共 90 項工具", s);
        Assert.Contains("不內含任何外部執行檔", s);
    }

    [Fact]
    public void 零命中要明說沒有符合而不是留白()
    {
        var s = ToolboxFilter.Summarize("不存在的東西", false, 0, 90);
        Assert.Contains("沒有符合", s);
        Assert.Contains("不存在的東西", s);
        Assert.Contains("90", s);
    }

    [Fact]
    public void 只看內建時要標明範圍已被縮小()
    {
        var s = ToolboxFilter.Summarize("", true, 30, 90);
        Assert.Contains("30 / 90", s);
        Assert.Contains("僅列曦覽自己做得到的項目", s);
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
    public void 只看內建會濾掉沒有對應功能的第三方項目()
    {
        var svc = new ToolboxService { OnlyBuiltin = true };
        Assert.All(svc.FilteredGroups.SelectMany(g => g.Items), t => Assert.True(t.HasNative));
        var kept = svc.FilteredGroups.SelectMany(g => g.Items).Count();
        Assert.True(kept > 0, "應該還留得下有自家對應頁面的項目，否則這個篩選沒有意義。");
        Assert.True(kept < svc.Tools.Count, "篩選後應少於全部項目，否則等於篩選沒有作用。");
    }

    [Fact]
    public void 清除篩選會回到完整清單()
    {
        var svc = new ToolboxService { Query = "cpu", OnlyBuiltin = true };
        svc.ClearFilter();
        Assert.Equal("", svc.Query);
        Assert.False(svc.OnlyBuiltin);
        Assert.Equal(svc.Tools.Count, svc.FilteredGroups.Sum(g => g.Items.Count));
    }
}
