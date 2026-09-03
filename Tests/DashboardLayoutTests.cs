using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 總覽磁貼版面的存檔解析測試。
/// </summary>
/// <remarks>
/// 這支測試守的是「升級不掉東西、改版不炸版面」：設定檔是使用者自己的，裡面可能留著上一版的
/// 識別碼、也可能還沒聽過本版新增的磁貼。解析必須兩邊都能吸收，且來回轉換要穩定，
/// 否則使用者一升級就會發現總覽整頁空白或是自訂順序被吃掉。
/// 只測純函式（<c>Plan</c>／<c>Serialize</c>）：建構實例得先有 MainViewModel，會拉起整組服務。
/// </remarks>
public sealed class DashboardLayoutTests
{
    [Fact]
    public void 沒有存檔時採用內建版面與內建顯示狀態()
    {
        var plan = DashboardLayout.Plan(null);

        Assert.Equal(DashboardLayout.Catalog.Length, plan.Count);
        for (int i = 0; i < plan.Count; i++)
        {
            Assert.Equal(DashboardLayout.Catalog[i].Id, plan[i].Id);
            Assert.Equal(DashboardLayout.Catalog[i].Default, plan[i].Visible);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,,")]
    public void 空白或空存檔等同沒有存檔(string saved)
        => Assert.Equal(DashboardLayout.Plan(null), DashboardLayout.Plan(saved));

    [Fact]
    public void 減號前綴代表隱藏()
    {
        var plan = DashboardLayout.Plan("-gauges,trends");

        Assert.False(plan[0].Visible);
        Assert.Equal("gauges", plan[0].Id);
        Assert.True(plan[1].Visible);
    }

    [Fact]
    public void 存檔順序優先於內建順序()
    {
        var plan = DashboardLayout.Plan("specs,gauges");

        Assert.Equal("specs", plan[0].Id);
        Assert.Equal("gauges", plan[1].Id);
    }

    [Fact]
    public void 未知識別碼直接忽略而不影響其餘版面()
    {
        var plan = DashboardLayout.Plan("specs,已被移除的磁貼,gauges");

        Assert.DoesNotContain(plan, t => t.Id == "已被移除的磁貼");
        Assert.Equal("specs", plan[0].Id);
        Assert.Equal("gauges", plan[1].Id);
        Assert.Equal(DashboardLayout.Catalog.Length, plan.Count);
    }

    [Fact]
    public void 存檔未提及的磁貼補在最後並採其內建顯示狀態()
    {
        var plan = DashboardLayout.Plan("specs");

        Assert.Equal("specs", plan[0].Id);
        Assert.Equal(DashboardLayout.Catalog.Length, plan.Count);

        // 補進來的每一塊都保持內建預設：本版新增的磁貼不該因為「舊存檔沒寫」而被永久藏起來
        foreach (var (id, visible) in plan.Skip(1))
            Assert.Equal(DashboardLayout.Catalog.First(c => c.Id == id).Default, visible);
    }

    [Fact]
    public void 存檔重複出現只認第一次的順位()
    {
        var plan = DashboardLayout.Plan("specs,gauges,specs");

        Assert.Equal("specs", plan[0].Id);
        Assert.Single(plan, t => t.Id == "specs");
        Assert.Equal(DashboardLayout.Catalog.Length, plan.Count);
    }

    [Fact]
    public void 解析與序列化來回不變()
    {
        const string saved = "specs,-gauges,trends";
        var once = DashboardLayout.Plan(saved);
        var text = DashboardLayout.Serialize(once);

        Assert.StartsWith("specs,-gauges,trends", text);
        Assert.Equal(once, DashboardLayout.Plan(text));   // 二次解析完全一致＝存檔可安全反覆讀寫
    }

    [Fact]
    public void 磁貼識別碼不重複且都有標題與說明()
    {
        Assert.Equal(DashboardLayout.Catalog.Length,
                     DashboardLayout.Catalog.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());

        foreach (var c in DashboardLayout.Catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Title));
            Assert.False(string.IsNullOrWhiteSpace(c.Hint));
        }
    }

    /// <summary>
    /// 目錄裡的每一塊磁貼，總覽頁都要有對應的 <c>Tile.{識別碼}</c> 樣板；反之也不該留下孤兒樣板。
    /// 少了樣板，那塊磁貼在畫面上就是一片空白；多了樣板則是刪磁貼時忘了收尾（1.9.0 移除
    /// 「AI 評價」磁貼時，目錄與樣板必須一起拿掉，只動一邊都會壞）。
    /// </summary>
    [Fact]
    public void 每塊磁貼都有對應的版面樣板且沒有孤兒樣板()
    {
        string xaml = System.IO.File.ReadAllText(
            System.IO.Path.Combine(RepoRoot(), "Views", "OverviewView.xaml"));
        var inXaml = System.Text.RegularExpressions.Regex
            .Matches(xaml, "x:Key=\"Tile\\.([A-Za-z0-9_]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var inCatalog = DashboardLayout.Catalog.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(inCatalog.Except(inXaml));   // 目錄有、版面沒有 → 空白磁貼
        Assert.Empty(inXaml.Except(inCatalog));   // 版面有、目錄沒有 → 孤兒樣板
    }

    /// <summary>
    /// 舊使用者的設定檔裡還留著已移除的 <c>ai</c>：必須被安靜忽略，其餘順序與顯示狀態照舊。
    /// 這是「移除磁貼」的升級路徑，錯了會讓老使用者的整份版面讀不進來。
    /// </summary>
    [Fact]
    public void 存檔裡留著已移除的AI磁貼不影響其餘版面()
    {
        var plan = DashboardLayout.Plan("gauges,trends,ai,-brands,specs");
        Assert.DoesNotContain("ai", plan.Select(p => p.Id));
        Assert.Equal(["gauges", "trends", "brands", "specs"], plan.Take(4).Select(p => p.Id));
        Assert.False(plan.First(p => p.Id == "brands").Visible);   // 減號前綴仍然有效
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Views", "OverviewView.xaml")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new System.IO.DirectoryNotFoundException("找不到原始碼樹");
    }
}
