using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// BIOS／Intel ME 韌體與微碼解碼測試。基準值取自本機（Z270／i9-7980XE 平台）實際讀值：
/// 登錄檔 Update Revision = 06 70 00 02、Firmware Record Version = 0x2007006、
/// Preferred Record Version = 0x2006b06（比 BIOS 的舊），故本機跑的是 BIOS 那份微碼。
/// </summary>
public class BiosMeTests
{
    [Fact]
    public void 微碼修訂版_本機實測小端四位元組解為零x02007006()
    {
        Assert.Equal(0x02007006u, BiosMeDecoder.DecodeUpdateRevision([0x06, 0x70, 0x00, 0x02]));
    }

    [Fact]
    public void 微碼修訂版_長度不足時回空值而不是補零()
    {
        Assert.Null(BiosMeDecoder.DecodeUpdateRevision([0x06, 0x70]));
        Assert.Null(BiosMeDecoder.DecodeUpdateRevision([]));
        Assert.Null(BiosMeDecoder.DecodeUpdateRevision(null));
    }

    [Fact]
    public void 微碼來源_本機實測BIOS版本較新故Windows不會覆蓋()
    {
        string s = BiosMeDecoder.CompareMicrocode(0x02007006, 0x02006b06);
        Assert.Contains("BIOS", s);
        Assert.Contains("不會降版覆蓋", s);
        Assert.Contains("0x02007006", s);
        Assert.Contains("0x02006B06", s);
    }

    [Fact]
    public void 微碼來源_Windows版本較新時警告應由mcupdate覆蓋()
    {
        string s = BiosMeDecoder.CompareMicrocode(0x02006b06, 0x02007006);
        Assert.StartsWith("⚠", s);
        Assert.Contains("mcupdate", s);
    }

    [Fact]
    public void 微碼來源_兩邊同版時明說無從分辨是誰載入()
    {
        string s = BiosMeDecoder.CompareMicrocode(0x02007006, 0x02007006);
        Assert.Contains("同一份", s);
    }

    [Fact]
    public void 微碼來源_Windows未提供偏好版本時歸因於韌體()
    {
        Assert.Contains("來自 BIOS／韌體", BiosMeDecoder.CompareMicrocode(0x02007006, null));
        Assert.Contains("來自 BIOS／韌體", BiosMeDecoder.CompareMicrocode(0x02007006, 0));
    }

    [Fact]
    public void 微碼來源_讀不到目前版本時不做比較而不是猜()
    {
        string s = BiosMeDecoder.CompareMicrocode(null, 0x02006b06);
        Assert.StartsWith("—", s);
        Assert.Contains("不做比較", s);
    }

    [Fact]
    public void 更新狀態_未公開的值原樣呈現而不是硬翻譯()
    {
        Assert.Contains("—", BiosMeDecoder.DescribeUpdateStatus(null));
        Assert.Contains("未套用", BiosMeDecoder.DescribeUpdateStatus(0));
        string s = BiosMeDecoder.DescribeUpdateStatus(0x7);
        Assert.Contains("0x7", s);
        Assert.Contains("不翻譯", s);
    }

    [Fact]
    public void ME韌體版本_三個分割區依minor_major_build_hotfix順序解碼()
    {
        // 11.8.50.3425：minor=8 major=11 buildno=3425 hotfix=50
        byte[] payload =
        [
            0x08, 0x00, 0x0B, 0x00, 0x61, 0x0D, 0x32, 0x00,   // 作業碼 11.8.50.3425
            0x08, 0x00, 0x0B, 0x00, 0x61, 0x0D, 0x32, 0x00,   // 復原碼 同版
        ];
        string s = BiosMeDecoder.DecodeMeFwVersion(payload);
        Assert.Contains("作業碼", s);
        Assert.Contains("11.8.50.3425", s);
        Assert.Contains("復原碼", s);
    }

    [Fact]
    public void ME韌體版本_未使用的分割區全零時略過不列()
    {
        byte[] payload =
        [
            0x08, 0x00, 0x0B, 0x00, 0x61, 0x0D, 0x32, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        string s = BiosMeDecoder.DecodeMeFwVersion(payload);
        Assert.Contains("11.8.50.3425", s);
        Assert.DoesNotContain("復原碼", s);
    }

    [Fact]
    public void ME韌體版本_回應長度不足時不猜版本()
    {
        Assert.Contains("長度不足", BiosMeDecoder.DecodeMeFwVersion([0x08, 0x00, 0x0B]));
        Assert.Contains("長度不足", BiosMeDecoder.DecodeMeFwVersion(null));
    }

    [Fact]
    public void ME韌體版本_全部分割區皆為零時照實說而不是回零點零()
    {
        Assert.Contains("皆回報 0.0.0.0", BiosMeDecoder.DecodeMeFwVersion(new byte[16]));
    }

    [Fact]
    public void ME世代_已知主版號才給世代說法()
    {
        Assert.Contains("CSME 11.x", BiosMeDecoder.DescribeMeGeneration("11.8.50.3425"));
        Assert.Contains("CSME 12.x", BiosMeDecoder.DescribeMeGeneration("12.0.35.1427"));
        Assert.Contains("Skylake 以前", BiosMeDecoder.DescribeMeGeneration("9.1.25.1005"));
        Assert.Contains("CSME 19.x", BiosMeDecoder.DescribeMeGeneration("19.0.0.1826"));
    }

    [Fact]
    public void ME世代_版本字串不含數字時回破折號而不是亂猜()
    {
        Assert.Equal("—", BiosMeDecoder.DescribeMeGeneration("—（找不到 MEI 裝置介面）"));
        Assert.Equal("—", BiosMeDecoder.DescribeMeGeneration(""));
    }

    [Fact]
    public void 廠商下載頁_認得的廠商給官方網址()
    {
        Assert.Contains("asus.com", BiosMeDecoder.VendorBiosUrl("ASUSTeK COMPUTER INC."));
        Assert.Contains("gigabyte.com", BiosMeDecoder.VendorBiosUrl("Gigabyte Technology Co., Ltd."));
        Assert.Contains("msi.com", BiosMeDecoder.VendorBiosUrl("Micro-Star International Co., Ltd"));
        Assert.Contains("asrock.com", BiosMeDecoder.VendorBiosUrl("ASRock"));
    }

    [Fact]
    public void 廠商下載頁_認不出廠商時回空值而不是亂連()
    {
        Assert.Null(BiosMeDecoder.VendorBiosUrl("To Be Filled By O.E.M."));
        Assert.Null(BiosMeDecoder.VendorBiosUrl(""));
        Assert.Null(BiosMeDecoder.VendorBiosUrl(null));
    }

    [Fact]
    public void 危險警告_必須明說本程式不寫入任何韌體()
    {
        Assert.Contains("不寫入任何韌體", BiosMeDecoder.DangerNotice);
        Assert.Contains("唯讀", BiosMeDecoder.DangerNotice);
        Assert.Contains("官方", BiosMeDecoder.DangerNotice);
    }

    [Fact]
    public void ASCII轉換_去尾端填充字元空字串回空值()
    {
        Assert.Equal("PRIME Z270-A", BiosMeDecoder.AsciiOrNull("PRIME Z270-A\0\0 "u8.ToArray()));
        Assert.Null(BiosMeDecoder.AsciiOrNull("   \0"u8.ToArray()));
        Assert.Null(BiosMeDecoder.AsciiOrNull([]));
        Assert.Null(BiosMeDecoder.AsciiOrNull(null));
    }
}
