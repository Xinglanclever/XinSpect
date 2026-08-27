using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace XinSpect;

/// <summary>
/// 圓環儀表：Value / Max 決定弧長，數值變動時平滑掃掠並在中央做數字滾動。
/// ArcBrush 決定弧色與外圈輝光，Unit 顯示於數字右側，Caption 顯示於下方。
/// </summary>
public partial class RadialGauge : UserControl
{
    private const double Cx = 66, Cy = 66, R = 56;

    public RadialGauge()
    {
        InitializeComponent();
        Loaded += (_, _) => { ApplyGlow(); Redraw(); };
    }

    // ---- 對外相依屬性 -----------------------------------------------------

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RadialGauge),
        new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(
        nameof(Max), typeof(double), typeof(RadialGauge),
        new PropertyMetadata(100.0, (d, _) => ((RadialGauge)d).Redraw()));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(RadialGauge),
        new PropertyMetadata("", (d, e) => { if (((RadialGauge)d).UnitText is { } t) t.Text = (string)e.NewValue; }));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(RadialGauge), new PropertyMetadata(""));

    public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
        nameof(ArcBrush), typeof(Brush), typeof(RadialGauge),
        new PropertyMetadata(MakeAccent(), (d, _) => ((RadialGauge)d).ApplyGlow()));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public Brush ArcBrush { get => (Brush)GetValue(ArcBrushProperty); set => SetValue(ArcBrushProperty, value); }

    // ---- 內部動畫值（弧與數字皆讀此值） ----------------------------------

    private static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.Register(
        nameof(AnimatedValue), typeof(double), typeof(RadialGauge),
        new PropertyMetadata(0.0, (d, _) => ((RadialGauge)d).Redraw()));

    private double AnimatedValue { get => (double)GetValue(AnimatedValueProperty); set => SetValue(AnimatedValueProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (RadialGauge)d;
        double from = g.AnimatedValue;
        double to = (double)e.NewValue;
        if (double.IsNaN(to) || double.IsInfinity(to)) to = 0;

        // 平滑掃掠到新值（每次更新以目前動畫值為起點，銜接自然）
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(620))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        g.BeginAnimation(AnimatedValueProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private static Brush MakeAccent()
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3987e5"));
        b.Freeze();
        return b;
    }

    private void ApplyGlow()
    {
        if (ArcGlow is null) return;
        if (ArcBrush is SolidColorBrush sb) ArcGlow.Color = sb.Color;
    }

    private void Redraw()
    {
        if (NumberText is null) return;

        double v = AnimatedValue;
        NumberText.Text = ((int)Math.Round(v)).ToString();

        double max = Max <= 0 ? 100 : Max;
        double frac = Math.Clamp(v / max, 0, 1);
        double sweep = frac * 360.0;
        if (sweep <= 0.01) { ValueArc.Data = null; return; }
        if (sweep >= 360) sweep = 359.999;

        Point p0 = Pt(-90);
        Point p1 = Pt(-90 + sweep);
        var fig = new PathFigure { StartPoint = p0, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(p1, new Size(R, R), 0, sweep > 180, SweepDirection.Clockwise, true));
        ValueArc.Data = new PathGeometry(new[] { fig });
    }

    private static Point Pt(double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new Point(Cx + R * Math.Cos(rad), Cy + R * Math.Sin(rad));
    }
}
