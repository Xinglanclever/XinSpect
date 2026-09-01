using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 記憶體延遲曲線圖：X 軸為工作集大小（log2），Y 軸為平均存取延遲（線性）；
/// 琥珀色虛線標示由曲線階梯推導的快取邊界，旁邊顯示偏差百分比與可信度標籤。
/// 只有量到的點才畫——不做任何插值美化。
/// </summary>
public sealed class LatencyCurveChart : FrameworkElement
{
    public static readonly DependencyProperty XsProperty = DependencyProperty.Register(
        nameof(Xs), typeof(long[]), typeof(LatencyCurveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public long[] Xs { get => (long[])GetValue(XsProperty); set => SetValue(XsProperty, value); }

    public static readonly DependencyProperty YsProperty = DependencyProperty.Register(
        nameof(Ys), typeof(double[]), typeof(LatencyCurveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double[] Ys { get => (double[])GetValue(YsProperty); set => SetValue(YsProperty, value); }

    public static readonly DependencyProperty BoundaryMarksProperty = DependencyProperty.Register(
        nameof(BoundaryMarks), typeof(double[]), typeof(LatencyCurveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double[] BoundaryMarks { get => (double[])GetValue(BoundaryMarksProperty); set => SetValue(BoundaryMarksProperty, value); }

    /// <summary>各邊界的偏差標籤（如 "+8%"、"-22%"）。與 BoundaryMarks 一一對應。</summary>
    public static readonly DependencyProperty BoundaryLabelsProperty = DependencyProperty.Register(
        nameof(BoundaryLabels), typeof(string[]), typeof(LatencyCurveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public string[] BoundaryLabels { get => (string[])GetValue(BoundaryLabelsProperty); set => SetValue(BoundaryLabelsProperty, value); }

    /// <summary>各邊界的可信度等級（"high"/"medium"/"low"）。決定邊界線顏色。</summary>
    public static readonly DependencyProperty BoundaryConfidenceProperty = DependencyProperty.Register(
        nameof(BoundaryConfidence), typeof(string[]), typeof(LatencyCurveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public string[] BoundaryConfidence { get => (string[])GetValue(BoundaryConfidenceProperty); set => SetValue(BoundaryConfidenceProperty, value); }

    public LatencyCurveChart()
    {
        ClipToBounds = true;
        MinHeight = 200;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var backdrop = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x23)); backdrop.Freeze();
        dc.DrawRectangle(backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var xs = Xs; var ys = Ys;
        if (xs is null || ys is null || xs.Length < 2 || ys.Length != xs.Length) return;

        const double ML = 58, MR = 12, MT = 10, MB = 26;
        double w = ActualWidth - ML - MR, h = ActualHeight - MT - MB;
        if (w < 20 || h < 20) return;

        double x0 = Math.Log2(xs[0]), x1 = Math.Log2(xs[^1]);
        if (x1 - x0 < 1e-9) return;
        double yMin = ys.Min() * 0.9, yMax = ys.Max() * 1.08;
        if (yMax - yMin < 1e-9) yMax = yMin + 1;

        Point Map(long size, double lat) => new(
            ML + (Math.Log2(size) - x0) / (x1 - x0) * w,
            MT + (1 - (lat - yMin) / (yMax - yMin)) * h);

        var typeface = new Typeface("Microsoft JhengHei UI");
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gridBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x31)); gridBrush.Freeze();
        var textBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA)); textBrush.Freeze();
        var gridPen = new Pen(gridBrush, 1); gridPen.Freeze();

        // X 軸刻度：每個 2 的冪次標一格
        for (int e = (int)Math.Ceiling(x0); e <= (int)Math.Floor(x1); e++)
        {
            double x = ML + (e - x0) / (x1 - x0) * w;
            var size = (long)Math.Round(Math.Pow(2, e));
            dc.DrawLine(gridPen, new Point(x, MT), new Point(x, MT + h));
            dc.DrawText(new FormattedText(FormatSize(size), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 10, textBrush, dip),
                new Point(x - 18, MT + h + 4));
        }
        // Y 軸刻度：四等分
        for (int i = 0; i <= 4; i++)
        {
            double lat = yMin + (yMax - yMin) * i / 4.0;
            double y = MT + (1 - i / 4.0) * h;
            dc.DrawLine(gridPen, new Point(ML, y), new Point(ML + w, y));
            dc.DrawText(new FormattedText($"{lat:0}", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 10, textBrush, dip),
                new Point(6, y - 7));
        }

        // 推導邊界：依可信度著色（高＝琥珀、中＝橙紅、低＝灰），旁邊顯示偏差百分比標籤
        var marks = BoundaryMarks;
        if (marks is not null)
        {
            var labels = BoundaryLabels;
            var confs = BoundaryConfidence;
            for (int idx = 0; idx < marks.Length; idx++)
            {
                var b = marks[idx];
                if (b < xs[0] || b > xs[^1]) continue;
                double x = ML + (Math.Log2(b) - x0) / (x1 - x0) * w;

                // 依可信度選色
                byte r = 0xE0, g = 0xB3, b_ = 0x41; // 預設琥珀
                string conf = (confs is not null && idx < confs.Length) ? confs[idx] : "high";
                if (conf == "low") { r = 0x80; g = 0x80; b_ = 0x80; }       // 灰
                else if (conf == "medium") { r = 0xEC; g = 0x83; b_ = 0x5A; } // 橙紅
                var color = Color.FromRgb(r, g, b_);
                var brush = new SolidColorBrush(color); brush.Freeze();
                var bpen = new Pen(brush, 1.2) { DashStyle = DashStyles.Dash };
                bpen.Freeze();
                dc.DrawLine(bpen, new Point(x, MT), new Point(x, MT + h));

                // 偏差標籤
                if (labels is not null && idx < labels.Length && !string.IsNullOrEmpty(labels[idx]))
                {
                    var labelText = new FormattedText(labels[idx], CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, 9, brush, dip);
                    // 標籤放在邊界線頂端，旋轉 -90 度沿線顯示
                    var labelBg = new SolidColorBrush(Color.FromArgb(0xCC, 0x1E, 0x20, 0x23)); labelBg.Freeze();
                    dc.DrawRectangle(labelBg, null, new Rect(x + 2, MT + 2, labelText.Width + 4, labelText.Height + 2));
                    dc.DrawText(labelText, new Point(x + 4, MT + 3));
                }
            }
        }

        // 曲線本體：折線＋點
        var linePen = new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)), 1.8);
        linePen.Freeze();
        var dot = new SolidColorBrush(Colors.White); dot.Freeze();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool first = true;
            for (int i = 0; i < xs.Length; i++)
            {
                var p = Map(xs[i], ys[i]);
                if (first) { g.BeginFigure(p, false, false); first = false; }
                else g.LineTo(p, true, false);
            }
        }
        dc.DrawGeometry(null, linePen, geo);
        for (int i = 0; i < xs.Length; i++)
            dc.DrawEllipse(dot, null, Map(xs[i], ys[i]), 2, 2);

        static string FormatSize(long b) => b switch
        {
            >= 1024 * 1024 => $"{b / (1024.0 * 1024):0.#}M",
            >= 1024 => $"{b / 1024.0:0.#}K",
            _ => $"{b}B",
        };
    }
}
