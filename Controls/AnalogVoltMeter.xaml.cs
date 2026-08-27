using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace XinSpect;

/// <summary>
/// 類比電壓錶：指針隨 Value 於 Min～Max 間掃掠。中央（範圍中點）時指針垂直向上，
/// 電壓越高越向右偏、越低越向左偏；接近危險電壓的右側刻度以紅色標示。
/// 指針以動畫平滑轉動，模擬真實錶頭的擺動慣性。
/// </summary>
public partial class AnalogVoltMeter : UserControl
{
    // 錶盤幾何（對應 XAML 中 Needle 的樞軸與長度）
    private const double Px = 117, Py = 118;   // 樞軸
    private const double SweepDeg = 80;         // 兩側各 80°（中點為 0° 垂直向上）

    public AnalogVoltMeter()
    {
        InitializeComponent();
        Loaded += (_, _) => { BuildFace(); MoveNeedle(false); };
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(AnalogVoltMeter),
        new PropertyMetadata(double.NaN, (d, _) => ((AnalogVoltMeter)d).MoveNeedle(true)));

    public static readonly DependencyProperty MinProperty = DependencyProperty.Register(
        nameof(Min), typeof(double), typeof(AnalogVoltMeter),
        new PropertyMetadata(0.0, (d, _) => { ((AnalogVoltMeter)d).BuildFace(); ((AnalogVoltMeter)d).MoveNeedle(false); }));

    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(
        nameof(Max), typeof(double), typeof(AnalogVoltMeter),
        new PropertyMetadata(2.0, (d, _) => { ((AnalogVoltMeter)d).BuildFace(); ((AnalogVoltMeter)d).MoveNeedle(false); }));

    public static readonly DependencyProperty DangerFromProperty = DependencyProperty.Register(
        nameof(DangerFrom), typeof(double), typeof(AnalogVoltMeter),
        new PropertyMetadata(1.4, (d, _) => ((AnalogVoltMeter)d).BuildFace()));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(AnalogVoltMeter), new PropertyMetadata("電壓錶"));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Min { get => (double)GetValue(MinProperty); set => SetValue(MinProperty, value); }
    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public double DangerFrom { get => (double)GetValue(DangerFromProperty); set => SetValue(DangerFromProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    private double _faceMin, _faceMax;

    /// <summary>frac(0~1) → 螢幕角度（-80° 左 ～ +80° 右，0° 垂直向上）。</summary>
    private static double Angle(double frac) => -SweepDeg + Math.Clamp(frac, 0, 1) * (2 * SweepDeg);

    private static Point Radial(double angleDeg, double r)
    {
        double a = angleDeg * Math.PI / 180.0;
        return new Point(Px + r * Math.Sin(a), Py - r * Math.Cos(a));   // 0°=上，順時針為正
    }

    private void BuildFace()
    {
        if (Dial is null) return;

        // 移除先前繪製的刻度物件（保留 Needle / 樞軸 / 文字），以 Tag 標記
        for (int i = Dial.Children.Count - 1; i >= 0; i--)
            if (Dial.Children[i] is FrameworkElement fe && Equals(fe.Tag, "face"))
                Dial.Children.RemoveAt(i);

        double min = Min, max = Max <= Min ? Min + 1 : Max;
        _faceMin = min; _faceMax = max;

        // 主刻度弧（安全段）＋ 危險段（DangerFrom 以上以紅色）
        double dangerFrac = Math.Clamp((DangerFrom - min) / (max - min), 0, 1);
        AddArc(0, dangerFrac, (Brush)FindResource("HairlineBrush"), 3);
        if (dangerFrac < 1) AddArc(dangerFrac, 1, (Brush)FindResource("CriticalBrush"), 3);

        // 刻度線與數字（0 / ¼ / ½ / ¾ / 1）
        for (int i = 0; i <= 4; i++)
        {
            double frac = i / 4.0;
            double ang = Angle(frac);
            Point p1 = Radial(ang, 96), p2 = Radial(ang, 84);
            var tick = new Line { X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y, StrokeThickness = 1.5, Tag = "face",
                                  Stroke = (Brush)FindResource("SecondaryInkBrush") };
            Dial.Children.Add(tick);

            double val = min + frac * (max - min);
            Point pt = Radial(ang, 72);
            var lbl = new TextBlock
            {
                Text = val.ToString("0.0", CultureInfo.InvariantCulture),
                FontSize = 10, Tag = "face",
                Foreground = (Brush)FindResource("MutedInkBrush"),
            };
            lbl.Measure(new Size(40, 20));
            Canvas.SetLeft(lbl, pt.X - lbl.DesiredSize.Width / 2);
            Canvas.SetTop(lbl, pt.Y - lbl.DesiredSize.Height / 2);
            Dial.Children.Add(lbl);
        }
    }

    private void AddArc(double f0, double f1, Brush brush, double thickness)
    {
        if (f1 <= f0) return;
        Point a = Radial(Angle(f0), 90), b = Radial(Angle(f1), 90);
        var fig = new PathFigure { StartPoint = a, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(b, new Size(90, 90), 0, (Angle(f1) - Angle(f0)) > 180,
                                        SweepDirection.Clockwise, true));
        Dial.Children.Add(new Path
        {
            Data = new PathGeometry(new[] { fig }),
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Tag = "face",
        });
    }

    private void MoveNeedle(bool animate)
    {
        if (NeedleRotate is null) return;

        double min = _faceMin, max = _faceMax <= _faceMin ? _faceMin + 1 : _faceMax;
        double v = Value;
        bool has = !double.IsNaN(v) && !double.IsInfinity(v);
        double frac = has ? (v - min) / (max - min) : 0.5;   // 無讀值時居中
        double target = Angle(frac);

        ValueText.Text = has ? $"{v.ToString("0.000", CultureInfo.InvariantCulture)} V" : "—";

        if (animate)
        {
            var anim = new DoubleAnimation(NeedleRotate.Angle, target, TimeSpan.FromMilliseconds(480))
            {
                EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut },
            };
            NeedleRotate.BeginAnimation(RotateTransform.AngleProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            NeedleRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            NeedleRotate.Angle = target;
        }
    }
}
