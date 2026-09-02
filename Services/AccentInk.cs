using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 落在強調色上的墨色該用白的還是深的（純函式）。
/// </summary>
/// <remarks>
/// 為什麼需要這個：主要動作按鈕與命令面板的選取列，文字原本寫死白色。八個強調色的亮度差很多——
/// 琥珀（<c>#e0932a</c>）配白字只有約 2.5:1，遠低於 WCAG AA 對一般文字要求的 4.5:1；
/// 換成深字則有約 7.3:1。1.8.1 把外觀切換修好之後，那八個顏色第一次真的會出現在畫面上，
/// 這個問題也才第一次看得到。
/// <para>
/// 判斷方式是<b>算對比度、挑高的那一個</b>，不是憑亮度門檻猜。公式用 WCAG 2.x 的相對亮度
/// （sRGB 先反伽瑪，再以 0.2126／0.7152／0.0722 加權），黑對白得到 21:1，可用來驗證沒寫錯。
/// </para>
/// <para>
/// 底色取<b>漸層最亮的那一端</b>：按鈕底是漸層，白字最不利的情況就在亮端；用亮端挑就不會有
/// 某個狀態下突然看不清的情況。
/// </para>
/// </remarks>
public static class AccentInk
{
    /// <summary>深色墨（與淺色主題的主要墨色同一個值，視覺上才連貫）。</summary>
    public static readonly Color DarkInk = Color.FromRgb(0x17, 0x17, 0x1a);

    /// <summary>白色墨。</summary>
    public static readonly Color LightInk = Colors.White;

    /// <summary>WCAG 2.x 相對亮度。</summary>
    public static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>兩色的對比度（1:1 到 21:1）。</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>這個底色該用深字嗎。</summary>
    public static bool PickIsDark(Color background)
        => Contrast(DarkInk, background) >= Contrast(LightInk, background);

    /// <summary>挑對比較高的那個墨色。</summary>
    public static Color Pick(Color background) => PickIsDark(background) ? DarkInk : LightInk;
}
