using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// SLC 快取耗盡曲線圖：X 軸為持續寫入的時間（秒），Y 軸為寫入速度（MB/s）；
/// 琥珀色垂直虛線標示偵測到的速度斷崖。
/// </summary>
public sealed class SlcCurveChart : FrameworkElement
{
    public static readonly DependencyProperty TimesProperty = DependencyProperty.Register(
        nameof(Times), typeof(double[]), typeof(SlcCurveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double[] Times { get => (double[])GetValue(TimesProperty); set => SetValue(TimesProperty, value); }

    public static readonly DependencyProperty SpeedsProperty = DependencyProperty.Register(
        nameof(Speeds), typeof(double[]), typeof(SlcCurveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double[] Speeds { get => (double[])GetValue(SpeedsProperty); set => SetValue(SpeedsProperty, value); }

    public static readonly DependencyProperty CliffMarksProperty = DependencyProperty.Register(
        nameof(CliffMarks), typeof(double[]), typeof(SlcCurveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double[] CliffMarks { get => (double[])GetValue(CliffMarksProperty); set => SetValue(CliffMarksProperty, value); }

    public SlcCurveChart()
    {
        this.RepaintOnThemeChange();
        ClipToBounds = true;
        MinHeight = 200;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var backdrop = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x23)); backdrop.Freeze();
        dc.DrawRectangle(backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var ts = Times; var sp = Speeds;
        if (ts is null || sp is null || ts.Length < 2 || ts.Length != sp.Length) return;

        const double ML = 56, MR = 10, MT = 10, MB = 24;
        double w = Math.Max(0, ActualWidth - ML - MR), h = Math.Max(0, ActualHeight - MT - MB);
        double t1 = ts[^1];
        if (t1 <= 0) return;
        double yMin = 0, yMax = sp.Max() * 1.08;
        if (yMax <= 0) return;

        Point Map(double sec, double mbps) => new(ML + sec / t1 * w, MT + (1 - (mbps - yMin) / (yMax - yMin)) * h);

        var typeface = new Typeface("Microsoft JhengHei UI");
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gridBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x31)); gridBrush.Freeze();
        var textBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA)); textBrush.Freeze();
        var gridPen = new Pen(gridBrush, 1); gridPen.Freeze();

        // X 軸：每 1/5 時長標一格
        for (int i = 0; i <= 5; i++)
        {
            double sec = t1 * i / 5.0, x = ML + w * i / 5.0;
            dc.DrawLine(gridPen, new Point(x, MT), new Point(x, MT + h));
            dc.DrawText(new FormattedText($"{sec:0}s", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 10, textBrush, dip),
                new Point(x - 8, MT + h + 4));
        }
        // Y 軸：四等分
        for (int i = 0; i <= 4; i++)
        {
            double v = yMax * i / 4.0, y = MT + (1 - i / 4.0) * h;
            dc.DrawLine(gridPen, new Point(ML, y), new Point(ML + w, y));
            dc.DrawText(new FormattedText($"{v:N0}", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 10, textBrush, dip),
                new Point(6, y - 7));
        }

        // 斷崖標線
        var marks = CliffMarks;
        if (marks is not null)
        {
            var bpen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0xB3, 0x41)), 1.4) { DashStyle = DashStyles.Dash };
            bpen.Freeze();
            foreach (var m in marks)
            {
                if (m < 0 || m > t1) continue;
                dc.DrawLine(bpen, new Point(ML + m / t1 * w, MT), new Point(ML + m / t1 * w, MT + h));
            }
        }

        var linePen = new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)), 1.8);
        linePen.Freeze();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(Map(ts[0], sp[0]), false, false);
            for (int i = 1; i < ts.Length; i++) g.LineTo(Map(ts[i], sp[i]), true, false);
        }
        dc.DrawGeometry(null, linePen, geo);
        var dot = new SolidColorBrush(Colors.White); dot.Freeze();
        for (int i = 0; i < ts.Length; i++) dc.DrawEllipse(dot, null, Map(ts[i], sp[i]), 2, 2);
    }
}
