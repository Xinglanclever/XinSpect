using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 工具箱搜尋核心（<see cref="ToolboxFilter"/>）與工具目錄本身的一致性測試。
/// 後者最重要的一項是「<see cref="ToolItem.Native"/> 指的頁面鍵真的存在」——
/// 打錯一個字不會編譯失敗，只會讓那顆「曦覽內建：…」按鈕悄悄消失，正是需要測試盯著的失敗模式。
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
    public void 內建項目的Target都指得到真實的頁面或檢測視窗()
    {
        var svc = new ToolboxService();
        string[] windows = ["screen", "mouse", "keyboard", "speaker", "motion"];
        var bad = svc.Tools.Where(t => t.Kind == ToolKind.Builtin && PageRegistry.FindAny(t.Target) is null)
            .Concat(svc.Tools.Where(t => t.Kind == ToolKind.BuiltinWindow && !windows.Contains(t.Target)))
            .Select(t => $"{t.Name} → {t.Target}")
            .ToList();
        Assert.True(bad.Count == 0, "以下內建項目的 Target 無效：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void 內建項目不提供插槽()
    {
        var svc = new ToolboxService();
        Assert.All(svc.Tools.Where(t => t.IsBuiltin), t => Assert.False(t.CanSlot));
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
        Assert.All(svc.FilteredGroups.SelectMany(g => g.Items), t => Assert.True(t.IsBuiltin || t.HasNative));
        Assert.True(svc.FilteredGroups.SelectMany(g => g.Items).Count() < svc.Tools.Count,
                    "篩選後應少於全部項目，否則等於篩選沒有作用。");
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
