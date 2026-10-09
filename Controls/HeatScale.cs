using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 溫度色階的<b>單一來源</b>：核心熱區圖、逐核液柱與圖例都讀這一份，所以同一個溫度在三個地方
/// 一定是同一個顏色；換掉主題或強調色也不會變——讀者記住的「幾度長什麼樣」必須一直是對的。
/// </summary>
/// <remarks>
/// <para>
/// 錨點刻意對齊 <see cref="Health.Cpu"/> 的分級（60 °C 以下冷、80 °C 起警戒、92 °C 近 TjMax）：
/// 同一顆核心的數字顏色與格子顏色若各說各話，讀者得先決定相信哪一個，那就等於沒有顏色語意。
/// 60–80 °C 這一段是真實處理器最常待的區間，錨點在這裡放得比較密，否則 62 °C 與 70 °C
/// 換算出來會是幾乎一樣的綠，等於整張圖只有一種顏色。
/// </para>
/// <para>
/// 顏色一律凍結後共用（同一色碼只建一次）：這張圖每秒重算一次，不該每次配置新筆刷。
/// </para>
/// </remarks>
public static class HeatScale
{
    /// <summary>色階上的一個錨點：這個溫度就是這個顏色。</summary>
    public readonly record struct Anchor(double Temperature, Color Color);

    // 冷 → 熱。藍 → 青綠 → 黃綠 → 琥珀 → 橙紅 → 熱紅。
    private static readonly Anchor[] AnchorsInternal =
    [
        new(30, Color.FromRgb(0x2B, 0x5C, 0xE0)),
        new(45, Color.FromRgb(0x14, 0xAA, 0x8C)),
        new(58, Color.FromRgb(0x6C, 0xBE, 0x28)),
        new(68, Color.FromRgb(0xF0, 0xAA, 0x1E)),
        new(80, Color.FromRgb(0xE0, 0x5A, 0x1E)),
        new(92, Color.FromRgb(0xB4, 0x1E, 0x1E)),
    ];

    /// <summary>色階錨點（冷 → 熱），圖例與文件都由此而生，不要另外抄一份溫度表。</summary>
    public static IReadOnlyList<Anchor> Anchors => AnchorsInternal;

    /// <summary>圖例上的一格一格：由錨點推導，溫度文字也一併帶著。</summary>
    public static IReadOnlyList<HeatLegendTick> Legend { get; } =
        [.. AnchorsInternal.Select(a => new HeatLegendTick(a.Temperature, a.Color))];

    /// <summary>色階最冷端的溫度（°C）。</summary>
    public static double Coldest => AnchorsInternal[0].Temperature;

    /// <summary>色階最熱端的溫度（°C）。</summary>
    public static double Hottest => AnchorsInternal[^1].Temperature;

    /// <summary>
    /// 溫度 → 色階上的顏色；無讀值（<c>null</c>／NaN）回 <c>null</c>，由呼叫端決定
    /// 「不知道」要長什麼樣——不要在這一層偷偷換成某個看起來很像資料的顏色。
    /// </summary>
    public static Color? ColorFor(double? value)
    {
        if (value is not double temp || double.IsNaN(temp)) return null;

        if (temp <= AnchorsInternal[0].Temperature) return AnchorsInternal[0].Color;
        if (temp >= AnchorsInternal[^1].Temperature) return AnchorsInternal[^1].Color;
        for (int i = 0; i < AnchorsInternal.Length - 1; i++)
        {
            var (t0, c0) = AnchorsInternal[i];
            var (t1, c1) = AnchorsInternal[i + 1];
            if (temp >= t0 && temp <= t1)
            {
                double f = (temp - t0) / (t1 - t0);
                return Lerp(c0, c1, f);
            }
        }
        return AnchorsInternal[^1].Color;
    }

    /// <summary>底色在這塊底色上讀得清楚的墨色（淺底配深字、深底配白字）。</summary>
    public static Brush InkFor(Color? fill)
    {
        if (fill is not Color c) return InkOnDark;
        // 相對亮度（sRGB 加權）；門檻取在青綠與黃綠之間，實測六個錨點都落在正確的一側。
        double luma = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        return luma > 0.55 ? InkOnLight : InkOnDark;
    }

    /// <summary>格子底色；無讀值回 <c>null</c>（由版面層以主題的中性底色表示「不確定」）。</summary>
    public static Brush? BrushFor(double? value)
        => ColorFor(value) is Color c ? BrushOf(c) : null;

    /// <summary>格內負載條的底槽色：跟著該格墨色走，才不會在亮底（黃綠）上消失。</summary>
    public static Brush TrackFor(double? value)
        => ColorFor(value) is Color c && ReferenceEquals(InkFor(c), InkOnLight)
            ? TrackOnLight : TrackOnDark;

    /// <summary>色階上的顏色 → 共用筆刷（凍結）。</summary>
    private static Brush BrushOf(Color c)
    {
        // 每秒重畫的路徑：同色碼共用一顆凍結筆刷。
        string key = c.ToString(CultureInfo.InvariantCulture);
        return FillCache.GetOrAdd(key, _ =>
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        });
    }

    private static readonly ConcurrentDictionary<string, Brush> FillCache = new();

    private static readonly Brush InkOnDark = Frozen(Colors.White);
    private static readonly Brush InkOnLight = Frozen(Color.FromRgb(0x14, 0x14, 0x12));
    private static readonly Brush TrackOnDark = Frozen(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
    private static readonly Brush TrackOnLight = Frozen(Color.FromArgb(0x3D, 0x14, 0x14, 0x12));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Lerp(Color a, Color b, double f) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * f),
        (byte)Math.Round(a.G + (b.G - a.G) * f),
        (byte)Math.Round(a.B + (b.B - a.B) * f));
}

/// <summary>圖例上的一格：一個錨點溫度與它代表的顏色。</summary>
public sealed class HeatLegendTick
{
    public HeatLegendTick(double temperature, Color color)
    {
        Temperature = temperature;
        Color = color;
    }

    /// <summary>錨點溫度（°C）。</summary>
    public double Temperature { get; }

    /// <summary>錨點顏色。</summary>
    public Color Color { get; }

    /// <summary>圖例上的刻度文字。</summary>
    public string Text => $"{Temperature:0}°";

    /// <summary>圖例色塊的填色。</summary>
    public Brush Fill => HeatScale.BrushFor(Temperature) ?? Brushes.Transparent;
}
