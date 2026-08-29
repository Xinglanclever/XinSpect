using System.Windows.Media;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 圖表取色的無 UI 路徑測試。
/// </summary>
/// <remarks>
/// 測試行程裡沒有 <c>Application.Current</c>（也沒有合併 Theme.xaml），所以每一次取色都會走
/// 後備色分支。這正是要守住的行為：自繪圖表在設計時期預覽與單元測試中<b>不得</b>丟例外，
/// 否則往後任何用到轉換器的測試都會連帶爆掉。
/// </remarks>
public sealed class VizPaletteTests
{
    [Fact]
    public void 沒有Application時取色回傳後備色而不丟例外()
    {
        var b = VizPalette.Of("BaselineBrush", "#383835");
        Assert.NotNull(b);
        Assert.Equal(Color.FromRgb(0x38, 0x38, 0x35), b.Color);
    }

    [Fact]
    public void 後備筆刷已凍結且同色碼共用同一物件()
    {
        var a = VizPalette.Of("SomeMissingKey", "#112233");
        var b = VizPalette.Of("AnotherMissingKey", "#112233");

        Assert.True(a.IsFrozen);        // 凍結才能跨執行緒共用，也避免呼叫端改到全域色
        Assert.Same(a, b);              // 每秒重畫的格線不該每次配置新筆刷
    }

    [Fact]
    public void 圖表語彙各角色都取得到色()
    {
        Assert.NotNull(VizPalette.Grid);
        Assert.NotNull(VizPalette.Hairline);
        Assert.NotNull(VizPalette.Muted);
        Assert.NotNull(VizPalette.Ink);
        Assert.NotNull(VizPalette.Card);
        Assert.NotNull(VizPalette.Accent);
        Assert.Equal(VizPalette.Accent.Color, VizPalette.AccentColor);
    }

    [Fact]
    public void 混色端點與中點正確()
    {
        var black = Color.FromRgb(0, 0, 0);
        var white = Color.FromRgb(255, 255, 255);

        Assert.Equal(black, VizPalette.Blend(black, white, 0));
        Assert.Equal(white, VizPalette.Blend(black, white, 1));

        var mid = VizPalette.Blend(black, white, 0.5);
        Assert.Equal(128, mid.R);
        Assert.Equal(128, mid.G);
        Assert.Equal(128, mid.B);
    }

    [Theory]
    [InlineData(Severity.Good)]
    [InlineData(Severity.Warning)]
    [InlineData(Severity.Serious)]
    [InlineData(Severity.Critical)]
    [InlineData(Severity.Neutral)]
    public void 狀態色四階皆有對應且不丟例外(Severity s)
    {
        Assert.NotNull(SeverityToBrushConverter.Brush(s));
    }

    [Fact]
    public void 四階狀態色彼此不同色以免混淆()
    {
        var cols = new[]
        {
            SeverityToBrushConverter.Brush(Severity.Good).Color,
            SeverityToBrushConverter.Brush(Severity.Warning).Color,
            SeverityToBrushConverter.Brush(Severity.Serious).Color,
            SeverityToBrushConverter.Brush(Severity.Critical).Color,
        };
        Assert.Equal(cols.Length, cols.Distinct().Count());
    }
}
