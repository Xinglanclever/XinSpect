using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace XinSpect;

/// <summary>
/// 風扇曲線編輯畫布。橫軸為溫度（<see cref="FanCurve.TempMin"/>–<see cref="FanCurve.TempMax"/> °C），
/// 縱軸為輸出百分比；左鍵拖動控制點、空白處雙擊新增、右鍵移除（至少保留兩點）。
/// 灰色垂直線與圓點為即時溫度／目前輸出，方便一眼看出現在落在曲線哪一段。
/// </summary>
public partial class FanCurveEditor : UserControl
{
    // 內距：左側留給百分比刻度、底部留給溫度刻度
    private const double PadL = 34, PadR = 12, PadT = 12, PadB = 20;
    private const double HitRadius = 11;

    private FanCurve? _hooked;
    private FanCurvePoint? _drag;

    public FanCurveEditor()
    {
        InitializeComponent();
        SizeChanged += (_, _) => { Render(); RenderLive(); };
        Loaded += (_, _) => { Render(); RenderLive(); };
        // 曲線與刻度是用「複製出來的色值」畫的（見 Render 裡的 VizPalette.AccentColor），
        // 換強調色後不會自己變，必須重畫一次
        this.OnThemeChange(() => { Render(); RenderLive(); });
    }

    // ── 對外屬性 ────────────────────────────────────────────────────────────

    public static readonly DependencyProperty CurveProperty =
        DependencyProperty.Register(nameof(Curve), typeof(FanCurve), typeof(FanCurveEditor),
            new PropertyMetadata(null, OnCurveChanged));

    /// <summary>目前編輯的曲線。</summary>
    public FanCurve? Curve
    {
        get => (FanCurve?)GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public static readonly DependencyProperty TempProperty =
        DependencyProperty.Register(nameof(Temp), typeof(double), typeof(FanCurveEditor),
            new PropertyMetadata(double.NaN, (d, _) => ((FanCurveEditor)d).RenderLive()));

    /// <summary>即時溫度（°C）；<c>NaN</c> 表示無讀值，不畫標記。</summary>
    public double Temp
    {
        get => (double)GetValue(TempProperty);
        set => SetValue(TempProperty, value);
    }

    public static readonly DependencyProperty OutputProperty =
        DependencyProperty.Register(nameof(Output), typeof(double), typeof(FanCurveEditor),
            new PropertyMetadata(double.NaN, (d, _) => ((FanCurveEditor)d).RenderLive()));

    /// <summary>風扇目前實際輸出（%）；<c>NaN</c> 時改以曲線推算值繪製。</summary>
    public double Output
    {
        get => (double)GetValue(OutputProperty);
        set => SetValue(OutputProperty, value);
    }

    private static void OnCurveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ed = (FanCurveEditor)d;
        if (ed._hooked is not null) ed._hooked.Changed -= ed.OnCurveEdited;
        ed._hooked = e.NewValue as FanCurve;
        if (ed._hooked is not null) ed._hooked.Changed += ed.OnCurveEdited;
        ed._drag = null;
        ed.Render();
        ed.RenderLive();
    }

    private void OnCurveEdited()
    {
        Render();
        RenderLive();
    }

    // ── 座標換算 ────────────────────────────────────────────────────────────

    private double PlotW => Math.Max(1, Root.ActualWidth - PadL - PadR);
    private double PlotH => Math.Max(1, Root.ActualHeight - PadT - PadB);
    private const double Span = FanCurve.TempMax - FanCurve.TempMin;

    private double X(double tempC) => PadL + (tempC - FanCurve.TempMin) / Span * PlotW;
    private double Y(double pct) => PadT + (1 - Math.Clamp(pct, 0, 100) / 100.0) * PlotH;
    private double TempAt(double x) => FanCurve.TempMin + (x - PadL) / PlotW * Span;
    private double PctAt(double y) => (1 - (y - PadT) / PlotH) * 100;

    // ── 繪製 ────────────────────────────────────────────────────────────────

    /// <summary>本控制項專屬的資源查找（會沿視覺樹往上找，容許外層覆寫）。</summary>
    /// <remarks>圖表語彙（格線、刻度文字、強調色）改走 <see cref="VizPalette"/>，
    /// 讓風扇曲線與走勢圖、時間軸圖的格線深淺一致；這裡只剩控制點的內外圈用得到。</remarks>
    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    /// <summary>重畫格線、刻度、曲線與控制點（僅在曲線或尺寸變動時呼叫）。</summary>
    private void Render()
    {
        if (Plot is null) return;
        Plot.Children.Clear();
        if (Root.ActualWidth < 60 || Root.ActualHeight < 60) return;

        var grid = VizPalette.Grid;
        var muted = VizPalette.Muted;
        Color ac = VizPalette.AccentColor;

        // 橫向格線與百分比刻度
        for (int p = 0; p <= 100; p += 25)
        {
            double y = Y(p);
            Plot.Children.Add(new Line
            {
                X1 = PadL, X2 = PadL + PlotW, Y1 = y, Y2 = y,
                Stroke = grid, StrokeThickness = 1, Opacity = p is 0 or 100 ? 0.9 : 0.45,
            });
            var tb = new TextBlock { Text = $"{p}", FontSize = 9.5, Foreground = muted };
            Canvas.SetLeft(tb, 6);
            Canvas.SetTop(tb, y - 7);
            Plot.Children.Add(tb);
        }

        // 縱向格線與溫度刻度（每 10 °C 一格、每 20 °C 標字）
        for (int t = (int)FanCurve.TempMin; t <= FanCurve.TempMax; t += 10)
        {
            double x = X(t);
            Plot.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = PadT, Y2 = PadT + PlotH,
                Stroke = grid, StrokeThickness = 1, Opacity = 0.35,
            });
            if (t % 20 != 0) continue;
            var tb = new TextBlock { Text = $"{t}°", FontSize = 9.5, Foreground = muted };
            Canvas.SetLeft(tb, x - 9);
            Canvas.SetTop(tb, PadT + PlotH + 4);
            Plot.Children.Add(tb);
        }

        if (Curve is null || Curve.Points.Count == 0) return;
        DrawCurve(Curve, ac);
    }

    /// <summary>畫出面積、折線與可拖動的控制點；兩端維持端點值（與 Evaluate 一致）。</summary>
    private void DrawCurve(FanCurve c, Color ac)
    {
        var pts = c.Points.OrderBy(p => p.TempC).ToList();
        double left = PadL, right = PadL + PlotW, bottom = PadT + PlotH;

        var poly = new PointCollection { new(left, Y(pts[0].Percent)) };
        foreach (var p in pts) poly.Add(new Point(X(p.TempC), Y(p.Percent)));
        poly.Add(new Point(right, Y(pts[^1].Percent)));

        var fig = new PathFigure { StartPoint = new Point(left, bottom), IsClosed = true };
        fig.Segments.Add(new PolyLineSegment(poly, false));
        fig.Segments.Add(new LineSegment(new Point(right, bottom), false));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);

        var fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, ac.R, ac.G, ac.B), 0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x0C, ac.R, ac.G, ac.B), 1));
        Plot.Children.Add(new Path { Data = geo, Fill = fill });

        Plot.Children.Add(new Polyline
        {
            Points = poly,
            Stroke = new SolidColorBrush(ac),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        });

        var ring = Res("PagePlaneBrush", Brushes.Black);
        foreach (var p in pts)
        {
            var dot = new Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(ac), Stroke = ring, StrokeThickness = 1.6,
                Cursor = Cursors.SizeAll, ToolTip = p.Label,
            };
            Canvas.SetLeft(dot, X(p.TempC) - 5);
            Canvas.SetTop(dot, Y(p.Percent) - 5);
            Plot.Children.Add(dot);
        }
    }

    /// <summary>即時溫度標記（虛線 + 圓點 + 讀值），與曲線分層以免每秒重建整張圖。</summary>
    private void RenderLive()
    {
        if (Live is null) return;
        Live.Children.Clear();
        if (Curve is null || Curve.Points.Count == 0) return;
        if (Root.ActualWidth < 60 || Root.ActualHeight < 60) return;

        double t = Temp;
        if (double.IsNaN(t)) return;

        double pct = double.IsNaN(Output) ? Curve.Evaluate(t) : Output;
        double x = X(Math.Clamp(t, FanCurve.TempMin, FanCurve.TempMax)), y = Y(pct);
        var ink = Res("PrimaryInkBrush", Brushes.White);

        Live.Children.Add(new Line
        {
            X1 = x, X2 = x, Y1 = PadT, Y2 = PadT + PlotH,
            Stroke = ink, StrokeThickness = 1, Opacity = 0.45,
            StrokeDashArray = [3, 3],
        });

        var dot = new Ellipse { Width = 8, Height = 8, Fill = ink, Opacity = 0.9 };
        Canvas.SetLeft(dot, x - 4);
        Canvas.SetTop(dot, y - 4);
        Live.Children.Add(dot);

        var tb = new TextBlock
        {
            Text = $"{t:0} °C ・ {pct:0} %", FontSize = 10, Foreground = ink, Opacity = 0.85,
        };
        Canvas.SetLeft(tb, Math.Min(x + 7, PadL + PlotW - 72));
        Canvas.SetTop(tb, PadT + 1);
        Live.Children.Add(tb);
    }

    // ── 互動 ────────────────────────────────────────────────────────────────

    private bool InPlot(Point m) =>
        m.X >= PadL - 6 && m.X <= PadL + PlotW + 6 && m.Y >= PadT - 6 && m.Y <= PadT + PlotH + 6;

    /// <summary>取滑鼠附近最近的控制點；超出命中半徑回傳 null。</summary>
    private FanCurvePoint? Hit(Point m)
    {
        if (Curve is null) return null;
        FanCurvePoint? best = null;
        double bd = HitRadius * HitRadius;
        foreach (var p in Curve.Points)
        {
            double dx = X(p.TempC) - m.X, dy = Y(p.Percent) - m.Y, d = dx * dx + dy * dy;
            if (d <= bd) { bd = d; best = p; }
        }
        return best;
    }

    private void Root_Down(object sender, MouseButtonEventArgs e)
    {
        if (Curve is null) return;
        var m = e.GetPosition(Root);
        var hit = Hit(m);

        // 空白處雙擊 → 新增控制點
        if (e.ClickCount >= 2 && hit is null && InPlot(m) && Curve.Points.Count < 12)
        {
            Curve.Points.Add(new FanCurvePoint(Math.Round(TempAt(m.X)), Math.Round(PctAt(m.Y))));
            Curve.Sort();
            Render();
            return;
        }

        if (hit is null) return;
        _drag = hit;
        Root.CaptureMouse();
    }

    private void Root_Move(object sender, MouseEventArgs e)
    {
        if (Curve is null) return;
        var m = e.GetPosition(Root);
        if (_drag is null)
        {
            Root.Cursor = Hit(m) is null ? Cursors.Arrow : Cursors.SizeAll;
            return;
        }

        // 夾在左右鄰點之間（各留 1 °C），保證控制點順序不反轉
        var sorted = Curve.Points.OrderBy(p => p.TempC).ToList();
        int i = sorted.IndexOf(_drag);
        double lo = i > 0 ? sorted[i - 1].TempC + 1 : FanCurve.TempMin;
        double hi = i >= 0 && i < sorted.Count - 1 ? sorted[i + 1].TempC - 1 : FanCurve.TempMax;
        if (hi < lo) hi = lo;

        _drag.TempC = Math.Clamp(Math.Round(TempAt(m.X)), lo, hi);
        _drag.Percent = Math.Round(PctAt(m.Y));
    }

    private void Root_Up(object sender, MouseButtonEventArgs e)
    {
        if (_drag is null) return;
        _drag = null;
        Root.ReleaseMouseCapture();
        Curve?.Sort();
        Render();
    }

    // 右鍵移除控制點（至少保留兩點，否則曲線無法內插）
    private void Root_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (Curve is null || Curve.Points.Count <= 2) return;
        if (Hit(e.GetPosition(Root)) is not { } p) return;
        Curve.Points.Remove(p);
        Render();
        e.Handled = true;
    }

    private void Root_Leave(object sender, MouseEventArgs e)
    {
        if (_drag is null) Root.Cursor = Cursors.Arrow;
    }
}
