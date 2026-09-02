using System.Windows.Media;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 主要動作按鈕的底色與文字色。
///
/// 外觀修好之後，八個強調色第一次真的會被畫出來——於是暴露一件先前不可能被發現的事：
/// 按鈕的文字寫死白色，配上琥珀那種亮底只有約 2.5:1，低於 WCAG AA 對一般文字要求的 4.5:1。
///
/// 這一份守的是「壓暗底色而不是換掉字色」。換字色是 Material 那一套的標準解，但在這裡不成立：
/// 按鈕底是一道跨度很大的漸層，深字在暗端只有 2.7:1——換字色只是把問題從一端搬到另一端。
/// 所以規則是：白字保留，底色整組壓暗到最亮的那一點也達標，且壓暗只動亮度不動色相。
/// </summary>
public class AccentInkTests
{
    private static Color Hex(string h) => (Color)ColorConverter.ConvertFromString(h);

    [Fact]
    public void 對比度公式對得上已知值()
    {
        // 黑對白是 21:1，這是 WCAG 的定義上限，用它驗證公式沒寫錯
        Assert.Equal(21.0, AccentInk.Contrast(Colors.Black, Colors.White), 2);
        Assert.Equal(1.0, AccentInk.Contrast(Colors.White, Colors.White), 3);
    }

    [Fact]
    public void 壓暗只動亮度_色相比例不變()
    {
        var amber = Hex("#e0932a");
        var dim = AccentInk.Scale(amber, 0.5);

        Assert.True(AccentInk.Luminance(dim) < AccentInk.Luminance(amber));
        // 三個通道的相對關係維持（紅 > 綠 > 藍）：琥珀壓暗後還是琥珀，不會變成別的顏色
        Assert.True(dim.R > dim.G && dim.G > dim.B);
    }

    [Fact]
    public void 本來就達標的底色一個位元都不改()
    {
        var top = Hex("#1c3f6e");     // 深藍：白字本來就過
        var c = AccentInk.ButtonColors(top, top, top);

        Assert.Equal(top, c.Top);
        Assert.Equal(top, c.Bottom);
        Assert.Equal(top, c.Solid);
        Assert.Equal(AccentInk.Ink, c.Ink);
    }

    [Fact]
    public void 亮底會被壓暗_但仍是同一個色相()
    {
        var top = Hex("#f0a83f");     // 琥珀的漸層亮端
        var c = AccentInk.ButtonColors(top, Hex("#9c6111"), Hex("#e0932a"));

        Assert.True(AccentInk.Luminance(c.Top) < AccentInk.Luminance(top));
        Assert.True(c.Top.R > c.Top.G && c.Top.G > c.Top.B);
        Assert.Equal(AccentInk.Ink, c.Ink);
    }

    [Fact]
    public void 漸層的三個底色一起壓_順序不會反過來()
    {
        var c = AccentInk.ButtonColors(Hex("#f0a83f"), Hex("#9c6111"), Hex("#e0932a"));
        // 壓暗前亮端 > 純色 > 暗端；壓暗後這個順序必須維持，否則漸層方向會反
        Assert.True(AccentInk.Luminance(c.Top) > AccentInk.Luminance(c.Solid));
        Assert.True(AccentInk.Luminance(c.Solid) > AccentInk.Luminance(c.Bottom));
    }

    [Fact]
    public void 八個強調色的三個底色配白字都要達到AA()
    {
        foreach (var p in ThemeService.Presets)
        {
            var c = AccentInk.ButtonColors(p.GradTopColor, p.DimColor, p.MainColor);

            foreach (var (name, bg) in new[] { ("漸層亮端", c.Top), ("漸層暗端", c.Bottom), ("滑過純色", c.Solid) })
            {
                double ratio = AccentInk.Contrast(c.Ink, bg);
                Assert.True(ratio >= AccentInk.AaNormalText,
                    $"{p.Name}（{p.Main}）的{name}對比只有 {ratio:0.00}:1，低於 AA 的 4.5:1");
            }
        }
    }

    [Fact]
    public void 沒有任何強調色被壓成近乎全黑()
    {
        // 壓暗是為了對比，不是為了把顏色弄不見：最亮那一端壓完仍要看得出是彩色
        foreach (var p in ThemeService.Presets)
        {
            var c = AccentInk.ButtonColors(p.GradTopColor, p.DimColor, p.MainColor);
            Assert.True(AccentInk.Luminance(c.Top) > 0.02,
                $"{p.Name} 的漸層亮端被壓到亮度 {AccentInk.Luminance(c.Top):0.000}，幾乎全黑了");
        }
    }
}
