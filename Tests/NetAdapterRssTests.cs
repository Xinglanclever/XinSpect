using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 網卡的接收端調整（RSS）判讀。
///
/// 這一份守的是「RSS 開著」不等於「有效」：只有一條接收佇列時，所有收包處理仍然落在同一顆核上，
/// 效果跟關掉一樣。這正是「中斷落在哪顆核」那張卡點名某顆核被網卡打爆時，下一步該看的地方。
/// </summary>
public class NetAdapterRssTests
{
    [Fact]
    public void RSS關閉時要說全部收包落在單一核心()
    {
        var (text, sev) = NetAdapterDecoder.JudgeRss(enabled: false, queues: 0, logicalProcessors: 36);
        Assert.Contains("單一", text);
        Assert.Equal(Severity.Warning, sev);
    }

    [Fact]
    public void RSS開著但只有一條佇列等於沒開()
    {
        var (text, sev) = NetAdapterDecoder.JudgeRss(true, 1, 36);
        Assert.Contains("一條", text);
        Assert.Equal(Severity.Warning, sev);
    }

    [Fact]
    public void 多條佇列視為正常並說出條數()
    {
        var (text, sev) = NetAdapterDecoder.JudgeRss(true, 4, 36);
        Assert.Contains("4", text);
        Assert.Equal(Severity.Good, sev);
    }

    [Fact]
    public void 佇列數讀不到時不下判斷()
    {
        var (text, sev) = NetAdapterDecoder.JudgeRss(true, queues: 0, logicalProcessors: 36);
        Assert.Contains("讀不到", text);
        Assert.Equal(Severity.Neutral, sev);
    }

    [Fact]
    public void 核心很多而佇列很少時要提醒比例()
    {
        // 36 顆核卻只有 2 條佇列：能用到的核心比例很低，值得說出來
        var (text, _) = NetAdapterDecoder.JudgeRss(true, 2, 36);
        Assert.Contains("36", text);
    }

    [Fact]
    public void 屬性值原樣呈現_不翻譯也不猜意思()
    {
        var row = NetAdapterDecoder.Property("中斷節流", "啟用", "*InterruptModeration");
        Assert.Equal("中斷節流", row.Name);
        Assert.Equal("啟用", row.Value);
        Assert.Contains("*InterruptModeration", row.Keyword);
    }

    [Fact]
    public void 沒有登錄關鍵字時欄位留白而不是編一個()
    {
        var row = NetAdapterDecoder.Property("某屬性", "某值", "");
        Assert.Equal("—", row.Keyword);
    }
}
