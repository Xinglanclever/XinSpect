using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XinSpect;

/// <summary>
/// 甜甜圈圖：Fraction（0..1）決定已用弧長，變動時平滑掃掠並在中央顯示百分比。
/// RingBrush 決定弧色與輝光；Caption / Caption2 顯示於下方，SubText 顯示於中央下緣。
/// 動畫手法比照 RadialGauge（內部 AnimatedFraction 相依屬性）。
/// </summary>
public partial class DonutChart : UserControl
{
    private const double Cx = 80, Cy = 66, R = 59;

    public DonutChart()
    {
        InitializeComponent();
        Loaded += (_, _) => { ApplyGlow(); Redraw(); };
    }

    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(DonutChart),
        new PropertyMetadata(0.0, OnFractionChanged));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(DonutChart),
        new PropertyMetadata("", (d, e) => { if (((DonutChart)d).CaptionLabel is { } t) t.Text = (string)e.NewValue; }));

    public static readonly DependencyProperty Caption2Property = DependencyProperty.Register(
        nameof(Caption2), typeof(string), typeof(DonutChart),
        new PropertyMetadata("", (d, e) => { if (((DonutChart)d).Caption2Label is { } t) t.Text = (string)e.NewValue; }));

    public static readonly DependencyProperty SubTextProperty = DependencyProperty.Register(
        nameof(SubText), typeof(string), typeof(DonutChart),
        new PropertyMetadata("", (d, e) => { if (((DonutChart)d).SubLabel is { } t) t.Text = (string)e.NewValue; }));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(DonutChart),
        new PropertyMetadata(MakeAccent(), (d, _) => ((DonutChart)d).ApplyGlow()));

    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public string Caption2 { get => (string)GetValue(Caption2Property); set => SetValue(Caption2Property, value); }
    public string SubText { get => (string)GetValue(SubTextProperty); set => SetValue(SubTextProperty, value); }
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }

    // ---- 內部動畫值 ------------------------------------------------------
    private static readonly DependencyProperty AnimatedFractionProperty = DependencyProperty.Register(
        nameof(AnimatedFraction), typeof(double), typeof(DonutChart),
        new PropertyMetadata(0.0, (d, _) => ((DonutChart)d).Redraw()));

    private double AnimatedFraction { get => (double)GetValue(AnimatedFractionProperty); set => SetValue(AnimatedFractionProperty, value); }

    private static void OnFractionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (DonutChart)d;
        double from = g.AnimatedFraction;
        double to = (double)e.NewValue;
        if (double.IsNaN(to) || double.IsInfinity(to)) to = 0;
        to = Math.Clamp(to, 0, 1);

        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(620))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        g.BeginAnimation(AnimatedFractionProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private static Brush MakeAccent()
    {
        if (Application.Current?.Resources["AccentBrush"] is SolidColorBrush sb) return sb;
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3987e5"));
        b.Freeze();
        return b;
    }

    private void ApplyGlow()
    {
        if (ArcGlow is null) return;
        if (RingBrush is SolidColorBrush sb) ArcGlow.Color = sb.Color;
    }

    private void Redraw()
    {
        if (CenterLabel is null) return;

        double frac = Math.Clamp(AnimatedFraction, 0, 1);
        CenterLabel.Text = $"{Math.Round(frac * 100)}%";

        double sweep = frac * 360.0;
        if (sweep <= 0.01) { UsedArc.Data = null; return; }
        if (sweep >= 360) sweep = 359.999;

        Point p0 = Pt(-90);
        Point p1 = Pt(-90 + sweep);
        var fig = new PathFigure { StartPoint = p0, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(p1, new Size(R, R), 0, sweep > 180, SweepDirection.Clockwise, true));
        UsedArc.Data = new PathGeometry(new[] { fig });
    }

    private static Point Pt(double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new Point(Cx + R * Math.Cos(rad), Cy + R * Math.Sin(rad));
    }
}
