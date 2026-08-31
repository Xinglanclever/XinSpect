using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 記憶體圖樣檢測的結果列與初始狀態測試。
/// </summary>
/// <remarks>
/// 這支測試守的是「不誇大也不隱瞞」：記憶體錯誤沒有「輕微」，量到一處就必須是最高等級；
/// 沒量到就得誠實說明這輪圖樣在抓什麼，而不是留白或亂填一個數字。
/// 只測純粹的呈現邏輯——真正的寫入／回讀要吃掉數 GB 記憶體，不適合在測試裡跑。
/// </remarks>
public sealed class MemoryTestRowTests
{
    private static MemoryTestRow Row(long errors, string? first = null, double seconds = 2.5, double mbPerSec = 1024)
        => new("全 0 / 全 1", "抓永遠卡在某一態的位元", errors, seconds, mbPerSec, first);

    [Fact]
    public void 沒有不符時為通過且評級為良好()
    {
        var row = Row(0);

        Assert.Equal("通過", row.ResultText);
        Assert.Equal(Severity.Good, row.Severity);
    }

    [Fact]
    public void 只要一處不符就是最高等級()
    {
        var row = Row(1, "位移 0x8　預期 0x0000000000000000，讀到 0x0000000000000008");

        Assert.Equal("1 處不符", row.ResultText);
        Assert.Equal(Severity.Critical, row.Severity);
    }

    [Fact]
    public void 沒有錯誤時備註說明本輪圖樣在抓什麼()
        => Assert.Equal("抓永遠卡在某一態的位元", Row(0).NoteText);

    [Fact]
    public void 有錯誤時備註改為第一處不符的實際內容()
    {
        const string first = "位移 0x10　預期 0xFFFFFFFFFFFFFFFF，讀到 0xFFFFFFFFFFFFFFFE";
        Assert.Equal(first, Row(3, first).NoteText);
    }

    [Fact]
    public void 量不到吞吐時顯示破折號而不是零()
    {
        Assert.Equal("—", Row(0, mbPerSec: 0).ThroughputText);
        Assert.Equal("1,024 MB/s", Row(0).ThroughputText);
    }

    [Fact]
    public void 尚未測試時各項讀值為破折號且無結論()
    {
        var svc = new MemoryTestService();

        Assert.False(svc.IsRunning);
        Assert.True(svc.CanStart);
        Assert.Equal(Severity.Neutral, svc.Verdict);
        Assert.Equal("—", svc.TestedText);
        Assert.Equal("—", svc.ErrorText);
        Assert.Equal("—", svc.ElapsedText);
        Assert.Equal("—", svc.SpeedText);
        Assert.Empty(svc.Rows);
        Assert.Equal(0, svc.ProgressFraction);
    }

    /// <remarks>
    /// 不能拿這台機器「當下」的可用記憶體來驗：當可用量不足時，1 GB 檔與 4 GB 檔都會被夾到同一個
    /// 上限，PlanText 一模一樣，測試就會隨機紅一次。所以改成餵一個固定的可用量進 Planned() 驗算式本身，
    /// 再另外確認 PlanText 有話說。
    /// </remarks>
    [Fact]
    public void 切換測試量會重算計畫說明()
    {
        var svc = new MemoryTestService();
        const long roomy = 64L * 1024 * 1024 * 1024;   // 假設有 64 GB 可用，兩個檔位都塞得下

        long small = svc.Planned(roomy);
        svc.SizeIndex = svc.SizeChoices.Length - 1;   // 最大檔
        long big = svc.Planned(roomy);

        Assert.NotEqual(0, svc.SizeIndex);
        Assert.True(big > small, $"最大檔應該比預設檔測更多：{big} vs {small}");
        Assert.False(string.IsNullOrWhiteSpace(svc.PlanText));
    }
}
