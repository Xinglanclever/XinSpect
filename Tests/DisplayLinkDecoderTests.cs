using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 顯示鏈路：目前這個模式需要多少頻寬，以及色彩有沒有被壓。
///
/// 這一份要守的是「每像素幾個位元」這件事：RGB 與 YCbCr 4:4:4 是每像素三個取樣，
/// 4:2:2 是兩個、4:2:0 是一點五個。算錯就會把「被壓成 4:2:2 才塞得進線」說成「一切正常」，
/// 而那正是「4K144 開起來了但畫面發糊」的成因。
/// </summary>
public class DisplayLinkDecoderTests
{
    [Fact]
    public void 每像素位元數依色彩編碼而不同()
    {
        Assert.Equal(24, DisplayLinkDecoder.BitsPerPixel(DisplayColorEncoding.Rgb, 8));
        Assert.Equal(24, DisplayLinkDecoder.BitsPerPixel(DisplayColorEncoding.YCbCr444, 8));
        Assert.Equal(16, DisplayLinkDecoder.BitsPerPixel(DisplayColorEncoding.YCbCr422, 8));
        Assert.Equal(12, DisplayLinkDecoder.BitsPerPixel(DisplayColorEncoding.YCbCr420, 8));
        Assert.Equal(30, DisplayLinkDecoder.BitsPerPixel(DisplayColorEncoding.Rgb, 10));
    }

    [Fact]
    public void 編碼或位元深度未知時不算頻寬()
    {
        Assert.Null(DisplayLinkDecoder.BitsPerPixel(DisplayColorEncoding.Unknown, 8));
        Assert.Null(DisplayLinkDecoder.BitsPerPixel(DisplayColorEncoding.Rgb, 0));
    }

    [Fact]
    public void 影像資料率由實際像素時鐘乘以每像素位元算出()
    {
        // 3840×2160@144 的像素時鐘約 1.1 GHz；取整數方便驗算：1e9 像素/秒 × 24 位元 = 24 Gb/s
        double? gbps = DisplayLinkDecoder.VideoGbps(1_000_000_000, DisplayColorEncoding.Rgb, 8);
        Assert.NotNull(gbps);
        Assert.Equal(24.0, gbps!.Value, 6);
    }

    [Fact]
    public void 沒有像素時鐘就不編一個頻寬出來()
    {
        Assert.Null(DisplayLinkDecoder.VideoGbps(0, DisplayColorEncoding.Rgb, 8));
    }

    // ── 判決 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 色度被降到422時明說是為了塞進鏈路()
    {
        var (text, sev) = DisplayLinkDecoder.Judge(DisplayColorEncoding.YCbCr422, 8, hdrEnabled: false);
        Assert.Contains("4:2:2", text);
        Assert.Contains("色度", text);
        Assert.Equal(Severity.Warning, sev);
    }

    [Fact]
    public void 降到420時要說壓得更凶()
    {
        var (text, sev) = DisplayLinkDecoder.Judge(DisplayColorEncoding.YCbCr420, 8, false);
        Assert.Contains("4:2:0", text);
        Assert.Equal(Severity.Serious, sev);
    }

    [Fact]
    public void RGB且十位元視為沒有被壓()
    {
        var (text, sev) = DisplayLinkDecoder.Judge(DisplayColorEncoding.Rgb, 10, false);
        Assert.Contains("沒有", text);
        Assert.Equal(Severity.Good, sev);
    }

    [Fact]
    public void 開了HDR卻只有八位元要提醒()
    {
        var (text, sev) = DisplayLinkDecoder.Judge(DisplayColorEncoding.Rgb, 8, hdrEnabled: true);
        Assert.Contains("HDR", text);
        Assert.Contains("8", text);
        Assert.Equal(Severity.Warning, sev);
    }

    [Fact]
    public void 編碼讀不到時不下判決()
    {
        var (text, sev) = DisplayLinkDecoder.Judge(DisplayColorEncoding.Unknown, 0, false);
        Assert.Contains("讀不到", text);
        Assert.Equal(Severity.Neutral, sev);
    }

    // ── 文字 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 連接技術要譯成看得懂的名字而不是代號()
    {
        Assert.Contains("DisplayPort", DisplayLinkDecoder.OutputTechText(10));
        Assert.Contains("HDMI", DisplayLinkDecoder.OutputTechText(5));
        Assert.Contains("內建", DisplayLinkDecoder.OutputTechText(0x80000000));
        // 沒收錄的代號如實顯示數字，不亂猜成 DisplayPort
        Assert.Contains("4242", DisplayLinkDecoder.OutputTechText(4242));
    }

    [Fact]
    public void 刷新率由分子分母算出並保留小數()
    {
        // 144 Hz 常見的實際值是 143.9xx；四捨五入成 144 會讓使用者以為沒問題
        Assert.Equal("143.98 Hz", DisplayLinkDecoder.RefreshText(1_439_800, 10_000));
        Assert.Equal("—", DisplayLinkDecoder.RefreshText(60, 0));
    }

    [Fact]
    public void 編碼名稱包含常見說法()
    {
        Assert.Contains("RGB", DisplayLinkDecoder.EncodingText(DisplayColorEncoding.Rgb));
        Assert.Contains("4:4:4", DisplayLinkDecoder.EncodingText(DisplayColorEncoding.YCbCr444));
        Assert.Contains("4:2:2", DisplayLinkDecoder.EncodingText(DisplayColorEncoding.YCbCr422));
        Assert.Contains("讀不到", DisplayLinkDecoder.EncodingText(DisplayColorEncoding.Unknown));
    }
}
