using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 幀時間圖：最後 N 個幀間隔畫成長條（時間順序），高度相對中位數；
/// 超過中位 1.3 倍為琥珀、2 倍為紅（明顯異常幀）。虛線為中位數基準。
/// </summary>
public sealed class FrameTimeChart : FrameworkElement
{
    public static readonly DependencyProperty IntervalsProperty = DependencyProperty.Register(
        nameof(Intervals), typeof(double[]), typeof(FrameTimeChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>幀間隔（ms，時間順序）。</summary>
    public double[] Intervals { get => (double[])GetValue(IntervalsProperty); set => SetValue(IntervalsProperty, value); }

    public FrameTimeChart()
    {
        this.RepaintOnThemeChange();
        ClipToBounds = true;
        MinHeight = 160;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var backdrop = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x23)); backdrop.Freeze();
        dc.DrawRectangle(backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var all = Intervals;
        if (all is null || all.Length < 2) return;
        var shown = all.Length > 240 ? all[^240..] : all;

        var sorted = (double[])shown.Clone();
        Array.Sort(sorted);
        double median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
        if (median <= 0) return;

        const double ML = 46, MR = 6, MT = 8, MB = 18;
        double w = Math.Max(0, ActualWidth - ML - MR), h = Math.Max(0, ActualHeight - MT - MB);
        double maxScale = Math.Max(median * 2.5, sorted[^1] * 1.05);   // 基準：中位 ×2.5，或最高幀時間
        double barW = Math.Max(1.5, w / shown.Length);

        var typeface = new Typeface("Microsoft JhengHei UI");
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var textBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA)); textBrush.Freeze();

        // 中位數虛線基準
        double medY = MT + h - Math.Min(median, maxScale) / maxScale * h;
        var medPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0xB3, 0x41)), 1) { DashStyle = DashStyles.Dash };
        medPen.Freeze();
        dc.DrawLine(medPen, new Point(ML, medY), new Point(ML + w, medY));
        dc.DrawText(new FormattedText($"中位 {median:0.00} ms", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, textBrush, dip),
            new Point(ML + 2, medY - 14));

        for (int i = 0; i < shown.Length; i++)
        {
            double v = Math.Min(shown[i], maxScale);
            double bh = v / maxScale * h;
            Color c = shown[i] > median * 2 ? Color.FromRgb(0xE0, 0x5B, 0x4B)
                    : shown[i] > median * 1.3 ? Color.FromRgb(0xE0, 0xB3, 0x41)
                    : Color.FromRgb(0x4C, 0xAF, 0x50);
            var fill = new SolidColorBrush(c); fill.Freeze();
            dc.DrawRoundedRectangle(fill, null,
                new Rect(ML + i * barW, MT + h - bh, Math.Max(1, barW - 1), bh), 1, 1);
        }

        dc.DrawText(new FormattedText($"最後 {shown.Length} 幀 ・ 紅＝超過中位 2 倍（明顯異常幀）",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, textBrush, dip),
            new Point(ML, ActualHeight - 14));
    }
}
