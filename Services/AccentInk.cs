using System.Windows.Media;

namespace XinSpect;

/// <summary>主要動作按鈕的底色（漸層兩端＋滑過時的純色）與落在上面的文字色。</summary>
public readonly record struct AccentButtonColors(Color Top, Color Bottom, Color Solid, Color Ink);

/// <summary>
/// 讓落在強調色上的白字達到無障礙對比（純函式）。
/// </summary>
/// <remarks>
/// 為什麼需要這個：主要動作按鈕與命令面板選取列的文字寫死白色。八個強調色的亮度差很多——
/// 琥珀（<c>#e0932a</c>）配白字只有約 2.5:1，遠低於 WCAG AA 對一般文字要求的 4.5:1。
/// 1.8.1 把外觀切換修好之後，那八個顏色第一次真的會被畫出來，這個問題也才第一次看得到。
/// <para>
/// <b>為什麼不是「亮底改用深字」</b>（Material 那一套的做法）：按鈕底是一道<b>跨度很大的漸層</b>
/// （亮端到暗端），深字在暗端只有 2.7:1——換了字色只是把問題從一端搬到另一端。
/// 單一文字色要在整道漸層上都成立，就只能是白色，並把底色壓暗到最亮的那一點也達標。
/// </para>
/// <para>
/// 壓暗只動<b>亮度</b>：在線性光空間對三個通道同乘一個係數，通道之間的比例不變，
/// 所以「琥珀還是琥珀」，只是更深。而且只動按鈕自己的底色資源，
/// <c>AccentBrush</c>（別處當文字色與框線用的那一支）完全不受影響。
/// </para>
/// <para>對比度公式用 WCAG 2.x 的相對亮度；黑對白得到 21:1，可用來驗證沒寫錯。</para>
/// </remarks>
public static class AccentInk
{
    /// <summary>按鈕上的文字色。整道漸層都要成立，只有白色做得到（見類別註解）。</summary>
    public static readonly Color Ink = Colors.White;

    /// <summary>一般文字的無障礙門檻（WCAG AA）。</summary>
    public const double AaNormalText = 4.5;

    /// <summary>
    /// 實際瞄準的對比度，比門檻高一點。壓暗算出來的顏色要量化回 8 位元 sRGB，
    /// 四捨五入可能讓亮度回升幾個千分點——剛好瞄 4.5 會落在 4.4999 而不達標。
    /// </summary>
    private const double TargetRatio = 4.6;

    /// <summary>WCAG 2.x 相對亮度。</summary>
    public static double Luminance(Color c)
        => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    /// <summary>兩色的對比度（1:1 到 21:1）。</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// 決定按鈕的底色與文字色：把整組底色壓暗到「最亮的那一點」也讓白字達標。
    /// 本來就達標的顏色一個位元都不改。
    /// </summary>
    /// <param name="top">漸層亮端。</param>
    /// <param name="bottom">漸層暗端。</param>
    /// <param name="solid">滑過時的純色底。</param>
    public static AccentButtonColors ButtonColors(Color top, Color bottom, Color solid)
    {
        // 白字最不利的地方：這三個底色裡最亮的那一個
        double worst = Math.Max(Luminance(top), Math.Max(Luminance(bottom), Luminance(solid)));

        // 白字（亮度 1.0）達標所需的底色亮度上限
        double limit = 1.05 / TargetRatio - 0.05;

        if (worst <= limit) return new AccentButtonColors(top, bottom, solid, Ink);

        // 三個底色同乘一個係數：漸層還是漸層，色相不變，只是整組更深
        double k = limit / worst;
        return new AccentButtonColors(Scale(top, k), Scale(bottom, k), Scale(solid, k), Ink);
    }

    /// <summary>在線性光空間把亮度乘上 <paramref name="k"/>：通道之間的比例不變，色相與飽和度因此保留。</summary>
    public static Color Scale(Color c, double k)
        => Color.FromRgb(Encode(Linear(c.R) * k), Encode(Linear(c.G) * k), Encode(Linear(c.B) * k));

    // ── sRGB ↔ 線性光 ─────────────────────────────────────────────────────

    private static double Linear(byte v)
    {
        double s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static byte Encode(double lin)
    {
        lin = Math.Clamp(lin, 0, 1);
        double s = lin <= 0.00304 ? lin * 12.92 : 1.055 * Math.Pow(lin, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(s, 0, 1) * 255);
    }
}
