using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 風扇葉片：七葉轉子，轉得快慢跟著真實 RPM 走，停轉就是灰的靜止圖，外圈細弧是目前輸出百分比。
/// 「有下命令卻沒在轉」（輸出 &gt; 5 % 而轉速為 0）會轉成警示色——那通常是接頭鬆了或風扇卡死。
/// </summary>
/// <remarks>
/// <para>
/// <b>葉片速度是壓縮過的相對指標，不是等比。</b>1200 RPM 換算成每秒 30 幀是每幀 237°，七葉轉子
/// 畫出來會頻閃甚至反轉（跟拍攝直升機螺旋槳同一個道理）。所以這裡把 0–3000 RPM 以對數壓到每幀
/// 3–14°：快慢的先後次序看得出來，但要精確數字請看旁邊的讀值。壓不進來的那部分改用外圈的殘影
/// 濃度表示，而不是硬畫成假的角速度。
/// </para>
/// <para>看不見或使用者關掉 <see cref="Motion"/> 時計時器停下，葉片停在最後的角度。</para>
/// </remarks>
public sealed class FanBlade : FrameworkElement
{
    private const int Blades = 7;

    /// <summary>每幀最大轉角（度）。七葉的頻閃臨界是 360/7 ≈ 51°，留三倍餘裕。</summary>
    private const double MaxDegPerFrame = 14;

    /// <summary>對數壓縮的上界：到這個轉速就是視覺上的最快。</summary>
    private const double RefRpm = 3000;

    public static readonly DependencyProperty RpmProperty = DependencyProperty.Register(
        nameof(Rpm), typeof(double?), typeof(FanBlade),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));
    /// <summary>真實轉速；<c>null</c> ＝沒有轉速感測（此時不做「該轉卻沒轉」的判斷）。</summary>
    public double? Rpm { get => (double?)GetValue(RpmProperty); set => SetValue(RpmProperty, value); }

    public static readonly DependencyProperty DutyProperty = DependencyProperty.Register(
        nameof(Duty), typeof(double), typeof(FanBlade),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));
    /// <summary>目前輸出百分比；<c>NaN</c> ＝讀不到（外圈弧不畫）。</summary>
    public double Duty { get => (double)GetValue(DutyProperty); set => SetValue(DutyProperty, value); }

    private static readonly StreamGeometry Blade = BuildBlade();

    private DispatcherTimer? _timer;
    private double _angle;

    public FanBlade()
    {
        this.RepaintOnThemeChange();
        Width = 44;
        Height = 44;
        IsVisibleChanged += (_, _) => Sync();
        Loaded += (_, _) => { Motion.Changed += Sync; Sync(); };
        Unloaded += (_, _) => { Motion.Changed -= Sync; _timer?.Stop(); };
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FanBlade)d).Sync();

    /// <summary>0（含）→ 1 的壓縮後轉速指標。</summary>
    private double Ramp()
    {
        double rpm = Rpm ?? 0;
        if (rpm <= 1) return 0;
        return Math.Clamp(Math.Log10(1 + rpm / 60) / Math.Log10(1 + RefRpm / 60), 0, 1);
    }

    private void Sync()
    {
        bool want = IsVisible && Motion.Enabled && Ramp() > 0;
        if (want)
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
                _timer.Tick += (_, _) =>
                {
                    if (!IsVisible || !Motion.Enabled) { Sync(); return; }
                    _angle = (_angle + 3 + (MaxDegPerFrame - 3) * Ramp()) % 360;
                    InvalidateVisual();
                };
            }
            _timer.Start();
        }
        else _timer?.Stop();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 12) return;
        double cx = ActualWidth / 2, cy = ActualHeight / 2;
        double r = size / 2 - 1;

        double ramp = Ramp();
        bool stalled = Rpm is <= 1 && Duty > 5;   // 有命令卻沒轉：只有真的讀到 0 才這樣說
        Brush ink = stalled ? VizPalette.Of("WarningBrush", "#e0b341")
                  : ramp > 0 ? VizPalette.Accent
                  : VizPalette.Muted;

        // 外框
        dc.DrawEllipse(null, new Pen(VizPalette.Hairline, 1), new Point(cx, cy), r, r);

        // 殘影：壓不進每幀轉角的那部分，用整圈淡環表示「比畫得出來的更快」
        if (ramp > 0.55)
        {
            dc.PushOpacity((ramp - 0.55) / 0.45 * 0.22);
            dc.DrawEllipse(ink, null, new Point(cx, cy), r * 0.95, r * 0.95);
            dc.Pop();
        }

        dc.PushTransform(new TranslateTransform(cx, cy));
        dc.PushTransform(new ScaleTransform(r * 0.86, r * 0.86));
        dc.PushTransform(new RotateTransform(_angle));
        dc.PushOpacity(ramp > 0 ? 0.95 : 0.55);
        for (int i = 0; i < Blades; i++)
        {
            dc.PushTransform(new RotateTransform(i * 360.0 / Blades));
            dc.DrawGeometry(ink, null, Blade);
            dc.Pop();
        }
        dc.Pop();
        dc.Pop();
        dc.Pop();
        dc.Pop();

        // 軸心
        dc.DrawEllipse(VizPalette.Card, new Pen(ink, 1), new Point(cx, cy), r * 0.22, r * 0.22);

        // 外圈輸出百分比弧（真實讀值；讀不到就不畫，不用 0 % 冒充）
        if (!double.IsNaN(Duty) && Duty > 0.5)
        {
            double sweep = Math.Clamp(Duty, 0, 100) / 100.0 * 360;
            dc.DrawGeometry(null, new Pen(ink, 2), Arc(cx, cy, r - 0.5, -90, sweep));
        }
    }

    /// <summary>以圓心為原點、外徑 1 的單葉幾何（前緣後掠、葉尖圓弧、葉根收窄）。</summary>
    private static StreamGeometry BuildBlade()
    {
        const double Hub = 0.30, Lead = 26, Tip = 24, Root = 18;
        static Point P(double r, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(r * Math.Cos(a), r * Math.Sin(a));
        }

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(P(Hub, 0), true, true);
            c.QuadraticBezierTo(P(0.66, 6), P(1.0, Lead), false, false);
            c.ArcTo(P(1.0, Lead + Tip), new Size(1, 1), 0, false, SweepDirection.Clockwise, false, false);
            c.QuadraticBezierTo(P(0.66, Root + 16), P(Hub, Root), false, false);
            c.ArcTo(P(Hub, 0), new Size(Hub, Hub), 0, false, SweepDirection.Counterclockwise, false, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>以 <paramref name="startDeg"/> 起算、順時針掃過 <paramref name="sweepDeg"/> 的弧。</summary>
    private static StreamGeometry Arc(double cx, double cy, double r, double startDeg, double sweepDeg)
    {
        static Point P(double cx, double cy, double r, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(P(cx, cy, r, startDeg), false, false);
            // 一段 ArcTo 畫不了整圈（起終點重合會被當成零長度）：滿載時切成兩半。
            if (sweepDeg >= 359.5)
            {
                c.ArcTo(P(cx, cy, r, startDeg + 180), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                c.ArcTo(P(cx, cy, r, startDeg + 359.9), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            }
            else
                c.ArcTo(P(cx, cy, r, startDeg + sweepDeg), new Size(r, r), 0,
                    sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    protected override Size MeasureOverride(Size available)
        => new(double.IsNaN(Width) ? 44 : Width, double.IsNaN(Height) ? 44 : Height);
}
