using System.Windows.Media;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 強調色上的墨色選擇。
///
/// 外觀修好之後，八個強調色第一次真的會出現在畫面上——於是暴露一件先前不可能被發現的事：
/// 主要動作按鈕的文字寫死白色，配上琥珀那種亮底只有約 2.5:1 的對比，低於 WCAG AA 對一般文字
/// 要求的 4.5:1。這一份守的是「墨色依底色亮度自動挑，挑對比高的那一個」。
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
    public void 亮底挑深字暗底挑白字()
    {
        Assert.True(AccentInk.PickIsDark(Hex("#ffffff")));
        Assert.False(AccentInk.PickIsDark(Hex("#000000")));
    }

    [Fact]
    public void 琥珀這種亮底必須挑深字()
    {
        // 白字落在 #e0932a 上只有約 2.5:1；深字約 7.3:1
        var amber = Hex("#e0932a");
        Assert.True(AccentInk.PickIsDark(amber));
        Assert.True(AccentInk.Contrast(AccentInk.Pick(amber), amber) > AccentInk.Contrast(Colors.White, amber));
    }

    [Fact]
    public void 挑出來的墨色一定是兩個候選裡對比較高的那個()
    {
        foreach (var p in ThemeService.Presets)
        {
            // 按鈕底色最亮的部分是漸層頂端，最不利的情況就在那裡
            var bg = p.GradTopColor;
            var picked = AccentInk.Pick(bg);
            double best = Math.Max(AccentInk.Contrast(AccentInk.DarkInk, bg),
                                   AccentInk.Contrast(AccentInk.LightInk, bg));
            Assert.Equal(best, AccentInk.Contrast(picked, bg), 3);
        }
    }

    [Fact]
    public void 八個強調色挑完之後都要達到AA的四點五比一()
    {
        foreach (var p in ThemeService.Presets)
        {
            var bg = p.GradTopColor;
            double c = AccentInk.Contrast(AccentInk.Pick(bg), bg);
            Assert.True(c >= 4.5, $"{p.Name}（{p.GradTop}）挑完墨色後對比只有 {c:0.00}:1，低於 AA 的 4.5:1");
        }
    }
}
