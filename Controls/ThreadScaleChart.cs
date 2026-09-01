using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 記憶體頻寬飽和曲線圖：X 軸為執行緒數（線性），Y 軸為頻寬 GB/s（線性）。
/// 四種存取型態（讀取／複製／相加／三元運算）各一條曲線。
/// </summary>
public sealed class ThreadScaleChart : FrameworkElement
{
    /// <summary>讀取型態的曲線數據。</summary>
    public static readonly DependencyProperty ReadDataProperty = DependencyProperty.Register(
        nameof(ReadData), typeof(object), typeof(ThreadScaleChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public object ReadData { get => GetValue(ReadDataProperty); set => SetValue(ReadDataProperty, value); }

    /// <summary>複製型態的曲線數據。</summary>
    public static readonly DependencyProperty CopyDataProperty = DependencyProperty.Register(
        nameof(CopyData), typeof(object), typeof(ThreadScaleChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public object CopyData { get => GetValue(CopyDataProperty); set => SetValue(CopyDataProperty, value); }

    /// <summary>相加型態的曲線數據。</summary>
    public static readonly DependencyProperty AddDataProperty = DependencyProperty.Register(
        nameof(AddData), typeof(object), typeof(ThreadScaleChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public object AddData { get => GetValue(AddDataProperty); set => SetValue(AddDataProperty, value); }

    /// <summary>三元運算型態的曲線數據。</summary>
    public static readonly DependencyProperty TriadDataProperty = DependencyProperty.Register(
        nameof(TriadData), typeof(object), typeof(ThreadScaleChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public object TriadData { get => GetValue(TriadDataProperty); set => SetValue(TriadDataProperty, value); }

    /// <summary>飽和所在執行緒數（在 X 軸上標記垂直線）。</summary>
    public static readonly DependencyProperty SaturationThreadProperty = DependencyProperty.Register(
        nameof(SaturationThread), typeof(int), typeof(ThreadScaleChart),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public int SaturationThread { get => (int)GetValue(SaturationThreadProperty); set => SetValue(SaturationThreadProperty, value); }

    public ThreadScaleChart()
    {
        ClipToBounds = true;
        MinHeight = 200;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var backdrop = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x23)); backdrop.Freeze();
        dc.DrawRectangle(backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));

        // 四條曲線的原始數據
        var readPts = ExtractPoints(ReadData);
        var copyPts = ExtractPoints(CopyData);
        var addPts = ExtractPoints(AddData);
        var triadPts = ExtractPoints(TriadData);
        var allPts = readPts.Concat(copyPts).Concat(addPts).Concat(triadPts).ToList();
        if (allPts.Count < 2) return;

        const double ML = 58, MR = 12, MT = 10, MB = 26;
        double w = ActualWidth - ML - MR, h = ActualHeight - MT - MB;
        if (w < 20 || h < 20) return;

        double x0 = 0.5;
        int maxX = allPts.Max(p => p.Threads);
        double x1 = maxX + 0.5;
        double yMin = allPts.Min(p => p.Gbps) * 0.85;
        double yMax = allPts.Max(p => p.Gbps) * 1.12;
        if (yMax <= yMin) yMax = yMin + 1;

        Point Map(int threads, double gbps) => new(
            ML + (threads - x0) / (x1 - x0) * w,
            MT + (1 - (gbps - yMin) / (yMax - yMin)) * h);

        var typeface = new Typeface("Microsoft JhengHei UI");
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gridBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x31)); gridBrush.Freeze();
        var textBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA)); textBrush.Freeze();
        var gridPen = new Pen(gridBrush, 1); gridPen.Freeze();

        // X 軸刻度：每個整數核心數標一格
        var threadSet = allPts.Select(p => p.Threads).Distinct().OrderBy(t => t).ToList();
        foreach (int t in threadSet)
        {
            double x = ML + (t - x0) / (x1 - x0) * w;
            dc.DrawLine(gridPen, new Point(x, MT), new Point(x, MT + h));
            dc.DrawText(new FormattedText($"{t}", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 9, textBrush, dip),
                new Point(x - 6, MT + h + 4));
        }

        // Y 軸刻度：四等分
        for (int i = 0; i <= 4; i++)
        {
            double gbps = yMin + (yMax - yMin) * i / 4.0;
            double y = MT + (1 - i / 4.0) * h;
            dc.DrawLine(gridPen, new Point(ML, y), new Point(ML + w, y));
            dc.DrawText(new FormattedText($"{gbps:0.0}", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 9, textBrush, dip),
                new Point(6, y - 7));
        }

        // 飽和垂直線（紅色虛線）
        int sat = SaturationThread;
        if (sat > 0)
        {
            double sx = ML + (sat - x0) / (x1 - x0) * w;
            var satPen = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xD0, 0x3B, 0x3B)), 1.0) { DashStyle = DashStyles.Dash };
            satPen.Freeze();
            dc.DrawLine(satPen, new Point(sx, MT), new Point(sx, MT + h));
            var satLabel = new FormattedText($"{sat} 核飽和", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 9, new SolidColorBrush(Color.FromArgb(0x99, 0xD0, 0x3B, 0x3B)), dip);
            dc.DrawText(satLabel, new Point(sx + 2, MT + 3));
        }

        // 繪製四條曲線
        DrawCurve(dc, readPts, Color.FromRgb(0x4C, 0x8D, 0xFF), Map);  // 藍
        DrawCurve(dc, copyPts, Color.FromRgb(0x39, 0xC4, 0x80), Map);  // 綠
        DrawCurve(dc, addPts, Color.FromRgb(0xE0, 0xB3, 0x41), Map);   // 琥珀
        DrawCurve(dc, triadPts, Color.FromRgb(0xC0, 0x6B, 0xE0), Map); // 紫

        // 圖例（右上角）
        double ly = MT + 2;
        DrawLegend(dc, "讀取", Color.FromRgb(0x4C, 0x8D, 0xFF), typeface, dip, ML + w - 50, ly, ref ly);
        DrawLegend(dc, "複製", Color.FromRgb(0x39, 0xC4, 0x80), typeface, dip, ML + w - 50, ly, ref ly);
        DrawLegend(dc, "相加", Color.FromRgb(0xE0, 0xB3, 0x41), typeface, dip, ML + w - 50, ly, ref ly);
        DrawLegend(dc, "三元", Color.FromRgb(0xC0, 0x6B, 0xE0), typeface, dip, ML + w - 50, ly, ref ly);
    }

    private static void DrawCurve(DrawingContext dc, List<(int Threads, double Gbps)> points, Color color,
                                  Func<int, double, Point> map)
    {
        if (points.Count < 2) return;
        var brush = new SolidColorBrush(color); brush.Freeze();
        var pen = new Pen(brush, 1.8); pen.Freeze();
        var dot = new SolidColorBrush(Colors.White); dot.Freeze();

        var sorted = points.OrderBy(p => p.Threads).ToList();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool first = true;
            foreach (var p in sorted)
            {
                var pt = map(p.Threads, p.Gbps);
                if (first) { g.BeginFigure(pt, false, false); first = false; }
                else g.LineTo(pt, true, false);
            }
        }
        dc.DrawGeometry(null, pen, geo);
        foreach (var p in sorted)
            dc.DrawEllipse(dot, null, map(p.Threads, p.Gbps), 2, 2);
    }

    private static void DrawLegend(DrawingContext dc, string label, Color color,
                                   Typeface typeface, double dip, double x, double y, ref double ly)
    {
        var brush = new SolidColorBrush(color); brush.Freeze();
        dc.DrawRectangle(brush, null, new Rect(x, ly + 3, 10, 3));
        var text = new FormattedText(label, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 9, new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA)), dip);
        dc.DrawText(text, new Point(x + 14, ly));
        ly += text.Height + 4;
    }

    private static List<(int Threads, double Gbps)> ExtractPoints(object? data)
    {
        if (data is System.Collections.IEnumerable enumerable)
        {
            var list = new List<(int, double)>();
            foreach (var item in enumerable)
            {
                if (item is MemBandwidthMath.ThreadScalePoint p)
                    list.Add((p.Threads, p.Gbps));
            }
            return list;
        }
        return [];
    }
}
