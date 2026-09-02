using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 大頁與 TLB 走表成本的判讀。
///
/// 這一份守的是「不要把配不出大頁說成沒有效益，也不要把沒有效益說成配不出來」——
/// 兩者的處理方式完全不同：前者要去開權限或重開機，後者是工作集本來就在 TLB 覆蓋範圍內，什麼都不用做。
/// </summary>
public class LargePageDecoderTests
{
    [Fact]
    public void 沒有鎖定記憶體權限時要說出權限名稱與怎麼開()
    {
        var v = LargePageDecoder.Judge(new LargePageFacts
        {
            PrivilegeHeld = false, LargePageMinimum = 2 * 1024 * 1024, AllocationOk = false,
            AllocationError = 1314,   // ERROR_PRIVILEGE_NOT_HELD
        });

        Assert.Contains("SeLockMemoryPrivilege", v.Detail);
        Assert.Contains("鎖定記憶體中的頁面", v.Detail);   // 本機安全性政策裡的實際字樣
        Assert.Equal(Severity.Neutral, v.Severity);
        Assert.DoesNotContain("快", v.Headline);           // 沒量到就不要談效益
    }

    [Fact]
    public void 有權限但配不出來時歸因於碎片化而不是沒有效益()
    {
        var v = LargePageDecoder.Judge(new LargePageFacts
        {
            PrivilegeHeld = true, LargePageMinimum = 2 * 1024 * 1024, AllocationOk = false,
            AllocationError = 1450,   // ERROR_NO_SYSTEM_RESOURCES
        });

        Assert.Contains("連續", v.Detail);
        Assert.Contains("碎片", v.Detail);
        Assert.Equal(Severity.Warning, v.Severity);
    }

    [Fact]
    public void 大頁明顯較快時把差異歸因於走表成本()
    {
        var v = LargePageDecoder.Judge(new LargePageFacts
        {
            PrivilegeHeld = true, LargePageMinimum = 2 * 1024 * 1024, AllocationOk = true,
            SmallPageNs = 120, LargePageNs = 84,   // 快三成
        });

        Assert.Contains("走表", v.Detail);
        Assert.Contains("30", v.Detail);           // 百分比要講出來
        Assert.Equal(Severity.Good, v.Severity);
    }

    [Fact]
    public void 大頁沒有比較快時不硬說有效益()
    {
        var v = LargePageDecoder.Judge(new LargePageFacts
        {
            PrivilegeHeld = true, LargePageMinimum = 2 * 1024 * 1024, AllocationOk = true,
            SmallPageNs = 100, LargePageNs = 99,
        });

        Assert.Contains("沒有", v.Headline);
        Assert.Contains("覆蓋", v.Detail);          // 要解釋為什麼沒有差
        Assert.Equal(Severity.Neutral, v.Severity);
    }

    [Fact]
    public void 還沒量測時不下判決()
    {
        var v = LargePageDecoder.Judge(new LargePageFacts
        {
            PrivilegeHeld = true, LargePageMinimum = 2 * 1024 * 1024, AllocationOk = true,
        });
        Assert.Contains("尚未量測", v.Headline);
        Assert.Equal(Severity.Neutral, v.Severity);
    }

    [Fact]
    public void 大頁尺寸以人看得懂的單位呈現()
    {
        Assert.Equal("2 MB", LargePageDecoder.SizeText(2 * 1024 * 1024));
        Assert.Equal("1 GB", LargePageDecoder.SizeText(1024L * 1024 * 1024));
        Assert.Equal("讀不到", LargePageDecoder.SizeText(0));
    }

    [Fact]
    public void 常見的配置失敗代碼都要翻成人話()
    {
        Assert.Contains("權限", LargePageDecoder.ErrorText(1314));
        Assert.Contains("連續", LargePageDecoder.ErrorText(1450));
        Assert.Contains("8", LargePageDecoder.ErrorText(8));        // ERROR_NOT_ENOUGH_MEMORY：代碼要留著
    }
}
