using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// M.2 Key 缺口示意圖：把常見的 Key 型別並排畫出金手指邊緣，缺口畫在對應的腳位範圍。
/// </summary>
/// <remarks>
/// <para>
/// 缺口位置＝Key 的定義，也決定了哪張卡能插進哪個插槽。Key B 與 Key M 在不同腳位，
/// 所以只有 Key M 插槽吃 NVMe x4；Key B+M 兩側都有缺口，插得進兩種插槽但只走 x2。
/// </para>
/// <para>
/// <b>示意圖。</b>缺口依 M.2 標準的腳位範圍畫出相對位置（67 腳），金手指以細齒表示，
/// 不逐一對應真實腳位編號。
/// </para>
/// </remarks>
public sealed class M2KeyDiagram : FrameworkElement
{
    private const int TotalPins = 67;

    // Key 名稱、缺口腳位範圍（起、迄），可有第二個缺口（B+M）
    private static readonly (string Name, int N1a, int N1b, int N2a, int N2b, string Use)[] Keys =
    [
        ("Key A",   8, 15,  0,  0, "Wi-Fi／藍牙（CNVi）"),
        ("Key E",  24, 31,  0,  0, "Wi-Fi／藍牙、AI 加速模組"),
        ("Key B",  12, 19,  0,  0, "SATA／PCIe x2 SSD、WWAN"),
        ("Key M",  59, 66,  0,  0, "NVMe PCIe x4 SSD（最常見）"),
        ("Key B+M", 12, 19, 59, 66, "兩側缺口，插得進 B 或 M 插槽，但只走 x2"),
    ];

    private const double RowH = 40;
    private const double RowGap = 16;
    private const double LabelW = 74;
    private const double NotchDepth = 13;

    protected override Size MeasureOverride(Size available)
    {
        double h = Keys.Length * RowH + (Keys.Length - 1) * RowGap;
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
        var fill = ThemeBrush("Surface2Brush", Color.FromRgb(0x2a, 0x2a, 0x2e));
        var pinBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27));
        var tf = new Typeface("Segoe UI");

        for (int i = 0; i < Keys.Length; i++)
        {
            var (name, n1a, n1b, n2a, n2b, use) = Keys[i];
            double y = i * (RowH + RowGap);
            double x0 = LabelW;

            var label = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                tf, 13, text, 1.25);
            dc.DrawText(label, new Point(0, y + 4));

            // 缺口的中心 x（依腳位範圍換算）
            double N1 = x0 + ((n1a + n1b) / 2.0 / TotalPins) * barW;
            double? N2 = n2a > 0 ? x0 + ((n2a + n2b) / 2.0 / TotalPins) * barW : null;
            double halfW = ((n1b - n1a + 1.0) / TotalPins) * barW / 2 + 2;

            // 本體含缺口
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(x0, y), true, true);
                DrawTopWithNotches(ctx, x0, x0 + barW, y, halfW, N1, N2);
                ctx.LineTo(new Point(x0 + barW, y + RowH), true, false);
                ctx.LineTo(new Point(x0, y + RowH), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(fill, new Pen(muted, 1), geo);

            // 金手指
            double pinTop = y + RowH - 8;
            double gap = barW / TotalPins;
            for (int p = 0; p < TotalPins; p++)
            {
                double px = x0 + p * gap + 0.5;
                bool inNotch = (px > N1 - halfW && px < N1 + halfW)
                            || (N2 is double n && px > n - halfW && px < n + halfW);
                if (inNotch) continue;
                dc.DrawRectangle(pinBrush, null, new Rect(px, pinTop, Math.Max(1, gap - 1), 7));
            }

            // 用途
            var useTxt = new FormattedText(use, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                tf, 10, muted, 1.25) { MaxTextWidth = barW - 4 };
            dc.DrawText(useTxt, new Point(x0 + 2, y + 20));
        }
    }

    private static void DrawTopWithNotches(StreamGeometryContext ctx, double left, double right,
        double y, double halfW, double n1, double? n2)
    {
        var notches = new List<double> { n1 };
        if (n2 is double d) notches.Add(d);
        notches.Sort();
        foreach (var n in notches)
        {
            ctx.LineTo(new Point(n - halfW, y), true, false);
            ctx.LineTo(new Point(n - halfW, y + NotchDepth), true, false);
            ctx.LineTo(new Point(n + halfW, y + NotchDepth), true, false);
            ctx.LineTo(new Point(n + halfW, y), true, false);
        }
        ctx.LineTo(new Point(right, y), true, false);
    }

    private static SolidColorBrush ThemeBrush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b;
        return new SolidColorBrush(fallback);
    }
}
