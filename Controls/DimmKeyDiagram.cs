using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// DIMM 缺口（key/notch）示意圖：把各世代的金手指邊緣並排畫出來，缺口畫在各自的特徵位置。
/// </summary>
/// <remarks>
/// <para>
/// 這張圖回答的是「為什麼 DDR4 插不進 DDR5 插槽」——答案就是<b>缺口位置不同</b>，
/// 插槽的凸起對不上就是插不進去。所以缺口的相對位置是這裡唯一要畫對的東西。
/// </para>
/// <para>
/// <b>示意圖，不是工程圖。</b>缺口位置依 JEDEC「偏左／偏右／中央」的定性描述與腳位數畫出相對位置，
/// 不宣稱是腳位精確座標。金手指只畫出疏密不同的紋理表示腳位多寡，不逐一對應真實腳位編號。
/// </para>
/// </remarks>
public sealed class DimmKeyDiagram : FrameworkElement
{
    /// <summary>目前偵測到的世代名稱（例如 "DDR4"）；用來高亮對應那一列。空字串＝都不高亮。</summary>
    public static readonly DependencyProperty DetectedProperty = DependencyProperty.Register(
        nameof(Detected), typeof(string), typeof(DimmKeyDiagram),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Detected { get => (string)GetValue(DetectedProperty); set => SetValue(DetectedProperty, value); }

    // 世代：名稱、腳位數、缺口相對位置（0=最左 1=最右）。
    // 位置與各代規格卡的「偏左／偏右／中央」描述一致——DDR4 偏左、DDR5 偏右是這一對現役記憶體
    // 不能互插的關鍵。DDR2 與 DDR3 同為 240 腳但缺口位置不同，所以也不能互插。
    private static readonly (string Name, int Pins, double Notch)[] Gens =
    [
        ("DDR",  184, 0.525),
        ("DDR2", 240, 0.500),
        ("DDR3", 240, 0.440),
        ("DDR4", 288, 0.460),
        ("DDR5", 288, 0.545),
    ];

    private const double RowH = 44;
    private const double RowGap = 14;
    private const double LabelW = 64;
    private const double NotchW = 10;
    private const double NotchDepth = 14;

    protected override Size MeasureOverride(Size available)
    {
        double h = Gens.Length * RowH + (Gens.Length - 1) * RowGap;
        double w = double.IsInfinity(available.Width) ? 480 : available.Width;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double totalW = ActualWidth > 0 ? ActualWidth : 480;
        double barW = totalW - LabelW - 8;
        if (barW < 40) return;

        var text = ThemeBrush("TextBrush", Colors.White);
        var muted = ThemeBrush("MutedBrush", Color.FromRgb(0x9a, 0x9a, 0x9a));
        var accent = ThemeBrush("AccentBrush", Color.FromRgb(0x4C, 0x8D, 0xFF));
        var fill = ThemeBrush("Surface2Brush", Color.FromRgb(0x2a, 0x2a, 0x2e));
        var pinBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27)); // 金手指

        var tf = new Typeface("Segoe UI");

        for (int i = 0; i < Gens.Length; i++)
        {
            var (name, pins, notch) = Gens[i];
            double y = i * (RowH + RowGap);
            bool hot = !string.IsNullOrEmpty(Detected)
                       && Detected.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                       // 避免 "DDR" 前綴誤配 "DDR2"：偵測名要嘛完全等於，要嘛下一字不是數字
                       && (Detected.Length == name.Length || !char.IsDigit(Detected[name.Length]));

            // 世代標籤
            var label = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                tf, 14, hot ? accent : text, 1.25);
            dc.DrawText(label, new Point(0, y + (RowH - label.Height) / 2));
            var pinLabel = new FormattedText($"{pins}p", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                tf, 10, muted, 1.25);
            dc.DrawText(pinLabel, new Point(0, y + RowH - 14));

            double x0 = LabelW;
            double notchX = x0 + notch * barW;

            // 模組本體：一個帶缺口的長條（金手指在底邊）
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(x0, y), true, true);
                ctx.LineTo(new Point(notchX - NotchW / 2, y), true, false);
                ctx.LineTo(new Point(notchX - NotchW / 2, y + NotchDepth), true, false);
                ctx.LineTo(new Point(notchX + NotchW / 2, y + NotchDepth), true, false);
                ctx.LineTo(new Point(notchX + NotchW / 2, y), true, false);
                ctx.LineTo(new Point(x0 + barW, y), true, false);
                ctx.LineTo(new Point(x0 + barW, y + RowH), true, false);
                ctx.LineTo(new Point(x0, y + RowH), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(fill, new Pen(hot ? accent : muted, hot ? 2 : 1), geo);

            // 金手指紋理（底邊一排細齒，密度隨腳位數）
            double pinAreaTop = y + RowH - 9;
            int teeth = Math.Min(pins / 4, (int)(barW / 3));
            double gap = barW / teeth;
            for (int t = 0; t < teeth; t++)
            {
                double px = x0 + t * gap + 1;
                // 缺口範圍內不畫金手指
                if (px > notchX - NotchW / 2 - 2 && px < notchX + NotchW / 2 + 2) continue;
                dc.DrawRectangle(pinBrush, null, new Rect(px, pinAreaTop, Math.Max(1, gap - 2), 8));
            }

            // 缺口位置標註
            var notchTag = new FormattedText(
                notch < 0.48 ? "缺口偏左" : notch > 0.52 ? "缺口偏右" : "缺口中央",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 9, muted, 1.25);
            dc.DrawText(notchTag, new Point(notchX - notchTag.Width / 2, y + NotchDepth + 3));
        }
    }

    private static SolidColorBrush ThemeBrush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b;
        return new SolidColorBrush(fallback);
    }
}
