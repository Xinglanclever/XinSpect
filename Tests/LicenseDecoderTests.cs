using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Windows 授權的白話判讀。
///
/// 一般使用者要的答案是三句話：是不是正版、重裝會不會掉、能不能移到新電腦。
/// 這一份守的是「不猜」——狀態碼沒收錄就說沒收錄，通道字樣讀不到就不假裝知道能不能轉移；
/// 以及「金鑰預設遮蔽」，只有 Windows 本來就公開的後五碼可以直接顯示。
/// </summary>
public class LicenseDecoderTests
{
    [Fact]
    public void 已授權是好狀態_未授權是嚴重()
    {
        Assert.Equal(Severity.Good, LicenseDecoder.StatusText(1).Severity);
        Assert.Equal(Severity.Critical, LicenseDecoder.StatusText(0).Severity);
        Assert.Contains("已授權", LicenseDecoder.StatusText(1).Text);
    }

    [Fact]
    public void 沒收錄的狀態碼要如實說沒收錄而不是猜()
    {
        var (text, sev) = LicenseDecoder.StatusText(99);
        Assert.Contains("99", text);
        Assert.Contains("不猜", text);
        Assert.Equal(Severity.Neutral, sev);
    }

    [Fact]
    public void 隨機版要說不能移到別的電腦()
    {
        string s = LicenseDecoder.ChannelText("OEM_DM channel");
        Assert.Contains("隨機版", s);
        Assert.Contains("不能移", s);
    }

    [Fact]
    public void 零售版要說可以轉移()
    {
        string s = LicenseDecoder.ChannelText("Retail channel");
        Assert.Contains("零售", s);
        Assert.Contains("轉移", s);
    }

    [Fact]
    public void 大量授權要說個人通常無法自行轉移()
    {
        Assert.Contains("大量授權", LicenseDecoder.ChannelText("Volume:MAK channel"));
    }

    [Fact]
    public void 通道讀不到時不猜能不能轉移()
    {
        string s = LicenseDecoder.ChannelText("");
        Assert.Contains("讀不到", s);
        Assert.DoesNotContain("可以轉移", s);
        Assert.DoesNotContain("不能移", s);
    }

    [Fact]
    public void 金鑰預設遮蔽_只留最後五碼()
    {
        string masked = LicenseDecoder.MaskKey("ABCDE-FGHIJ-KLMNO-PQRST-UVWXY");
        Assert.EndsWith("UVWXY", masked);
        Assert.DoesNotContain("ABCDE", masked);
        Assert.DoesNotContain("FGHIJ", masked);
    }

    [Fact]
    public void 沒有韌體金鑰時說讀不到而不是留空()
    {
        Assert.Contains("讀不到", LicenseDecoder.MaskKey(null));
        Assert.Contains("讀不到", LicenseDecoder.MaskKey("   "));
    }

    [Fact]
    public void 後五碼可以直接顯示_那不是祕密()
    {
        Assert.Contains("UVWXY", LicenseDecoder.PartialKeyText("UVWXY"));
        Assert.Contains("讀不到", LicenseDecoder.PartialKeyText(null));
    }

    [Fact]
    public void 判決要同時交代通道與韌體金鑰()
    {
        var v = LicenseDecoder.Judge(1, "OEM_DM channel", firmwareKeyPresent: true);
        Assert.Contains("已授權", v.Headline);
        Assert.Contains("隨機版", v.Detail);
        Assert.Contains("韌體", v.Detail);
        Assert.Equal(Severity.Good, v.Severity);
    }

    [Fact]
    public void 沒有韌體金鑰時要提醒重裝前先確認金鑰()
    {
        var v = LicenseDecoder.Judge(1, "Retail channel", firmwareKeyPresent: false);
        Assert.Contains("沒有內嵌金鑰", v.Detail);
        Assert.Contains("重裝前", v.Detail);
    }
}
