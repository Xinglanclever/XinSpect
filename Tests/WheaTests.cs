using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>WHEA 事件彙整純函式。</summary>
public class WheaTests
{
    [Fact]
    public void 彙整依層級分別計數()
    {
        var entries = new List<(DateTime, byte, int, string)>
        {
            (DateTime.Now, 1, 18, "重大"),
            (DateTime.Now, 2, 46, "錯誤"),
            (DateTime.Now, 2, 46, "錯誤"),
            (DateTime.Now, 3, 1, "警告"),
        };
        var (crit, err, warn) = WheaErrorService.Summarize(entries);
        Assert.Equal(1, crit);
        Assert.Equal(2, err);
        Assert.Equal(1, warn);
    }

    [Fact]
    public void 空清單全為零()
    {
        var (crit, err, warn) = WheaErrorService.Summarize([]);
        Assert.Equal((0, 0, 0), (crit, err, warn));
    }
}

public class WheaClassifyTests
{
    [Fact]
    public void WHEA事件ID分類()
    {
        Assert.Equal("修正的記憶體硬體錯誤", WheaErrorService.ClassifyEvent(17));
        Assert.Equal("不可修正的記憶體硬體錯誤", WheaErrorService.ClassifyEvent(18));
        Assert.Equal("PCIe 修正錯誤", WheaErrorService.ClassifyEvent(19));
        Assert.Equal("修正的硬體錯誤（機器檢查）", WheaErrorService.ClassifyEvent(46));
        Assert.Equal("不可修正的硬體錯誤（機器檢查）", WheaErrorService.ClassifyEvent(47));
        Assert.Equal("其他 WHEA 事件", WheaErrorService.ClassifyEvent(100));
    }
}
