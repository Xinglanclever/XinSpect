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
}
