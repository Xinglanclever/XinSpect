using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace XinSpect;

/// <summary>
/// 歷史回放用的時間軸圖：可縮放平移的多指標疊圖。
/// </summary>
/// <remarks>
/// 與即時走勢圖（<see cref="HistoryGraph"/>）分工不同：那一支追求每秒重畫的極省成本，
/// 這一支只在互動或重新查詢時重畫，換取多序列、事件標記與十字準線讀值。
/// <para>座標軸：只選一項指標時直接標該指標的實際值；多項疊圖時標「佔滿刻度的百分比」，
/// 各指標的滿刻度值列於圖例，避免不同單位硬湊同一軸而誤讀。</para>
/// <para>捲動滾輪縮放（以游標為錨點）、左鍵拖曳平移、雙擊還原；縮放與平移本身不改狀態，
/// 而是回報事件給檢視模型調整時間窗，重新查詢後再呼叫 <see cref="Render"/>。</para>
/// </remarks>
public partial class TimelineChart : UserControl
{
    private const double PadLeft = 52, PadTop = 12, PadBottom = 24, PadRight = 10;

    // 版面用色一律取主題資源的「同一顆」筆刷：ThemeService 換色是就地改 .Color，
    // 所以這裡畫出去的線與文字會自己跟著深淺主題變，不必訂閱事件重畫。
    private static Brush GridLine => VizPalette.Grid;
    private static Brush Hairline => VizPalette.Hairline;
    private static Brush Muted => VizPalette.Muted;
    private static Brush Ink => VizPalette.Ink;
    private static Brush Surface => VizPalette.Card;

    // 指標序列色是資料識別，不隨主題改：換了主題還是同一條「CPU 溫度」。
    private readonly Brush[] _metricBrush = new Brush[HistoryMetrics.Count];
    private readonly double[] _scale = new double[HistoryMetrics.Count];

    private HistorySeries _drawn = HistorySeries.Empty;   // 桶化後實際繪製的資料（供讀值對齊）
    private bool _dragging;
    private double _dragX;

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public TimelineChart()
    {
        InitializeComponent();
        for (int m = 0; m < HistoryMetrics.Count; m++)
        {
            var c = (Color)ColorConverter.ConvertFromString(HistoryMetrics.Colors[m])!;
            _metricBrush[m] = Freeze(c);
        }
        SizeChanged += (_, _) => Render();
        MouseLeave += (_, _) => Overlay.Children.Clear();
        this.OnThemeChange(Render);   // 格線／刻度取自佈景色，換主題後重畫一次
    }

    // ── 輸入（由檢視設定後呼叫 Render）────────────────────────────────────

    /// <summary>查詢結果。</summary>
    public HistorySeries Series { get; set; } = HistorySeries.Empty;

    /// <summary>各指標是否顯示（長度為 <see cref="HistoryMetrics.Count"/>）。</summary>
    public bool[] Active { get; set; } = new bool[HistoryMetrics.Count];

    /// <summary>時間軸上要標記的事件。</summary>
    public IReadOnlyList<TimelineEvent> Markers { get; set; } = [];

    public DateTime FromUtc { get; set; } = DateTime.UtcNow.AddHours(-1);
    public DateTime ToUtc { get; set; } = DateTime.UtcNow;

    /// <summary>縮放請求：倍率（&lt;1 放大）與錨點（0–1，游標於時間窗中的位置）。</summary>
    public event Action<double, double>? ZoomRequested;
    /// <summary>平移請求：以時間窗寬度為單位的位移（正值往未來）。</summary>
    public event Action<double>? PanRequested;
    /// <summary>雙擊還原。</summary>
    public event Action? ResetRequested;

    // ── 幾何 ──────────────────────────────────────────────────────────────

    private double PlotW => Math.Max(1, ActualWidth - PadLeft - PadRight);
    private double PlotH => Math.Max(1, ActualHeight - PadTop - PadBottom);

    private double XOf(DateTime utc)
    {
        double span = Math.Max(1, (ToUtc - FromUtc).Ticks);
        return PadLeft + PlotW * ((utc - FromUtc).Ticks / span);
    }

    private DateTime TimeAt(double x)
    {
        double f = Math.Clamp((x - PadLeft) / PlotW, 0, 1);
        return FromUtc + TimeSpan.FromTicks((long)((ToUtc - FromUtc).Ticks * f));
    }

    private double YOf(double value, int metric)
    {
        double max = _scale[metric] <= 0 ? 1 : _scale[metric];
        return PadTop + PlotH - Math.Clamp(value / max, 0, 1) * PlotH;
    }

    private int ActiveCount()
    {
        int n = 0;
        for (int m = 0; m < HistoryMetrics.Count; m++) if (Active[m]) n++;
        return n;
    }

    // ── 繪製 ──────────────────────────────────────────────────────────────

    /// <summary>依目前的 <see cref="Series"/>／<see cref="Active"/>／時間窗重畫整張圖。</summary>
    public void Render()
    {
        Grid_.Children.Clear();
        Plot.Children.Clear();
        Overlay.Children.Clear();
        if (ActualWidth < 60 || ActualHeight < 40) return;

        ComputeScales();
        DrawGrid();
        if (Series.Count == 0)
        {
            _drawn = HistorySeries.Empty;
            Grid_.Children.Add(Label("此區間沒有歷史資料", PadLeft + PlotW / 2 - 60, PadTop + PlotH / 2 - 10, Muted, 12.5));
            return;
        }
        _drawn = Series.Downsample((int)Math.Max(2, PlotW));
        DrawSeries();
        DrawMarkers();
    }

    // 各指標的滿刻度：百分比類固定 100，其餘取區間最大值再留 12% 抬頭空間。
    private void ComputeScales()
    {
        for (int m = 0; m < HistoryMetrics.Count; m++)
        {
            if (HistoryMetrics.FixedMax[m] is double fixedMax) { _scale[m] = fixedMax; continue; }
            double max = 0;
            for (int i = 0; i < Series.Count; i++) if (Series.H(i, m) > max) max = Series.H(i, m);
            _scale[m] = max <= 0 ? 1 : max * 1.12;
        }
    }

    /// <summary>指標的滿刻度文字，供圖例標明「這條線的頂端等於多少」。</summary>
    public string ScaleText(int metric)
        => $"{_scale[metric].ToString(_scale[metric] >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture)} {HistoryMetrics.Units[metric]}";

    private void DrawGrid()
    {
        double w = PlotW, h = PlotH;

        // 水平格線與縱軸標籤：只選一項時標實際值，多項疊圖時標百分比
        int only = -1;
        if (ActiveCount() == 1)
            for (int m = 0; m < HistoryMetrics.Count; m++) if (Active[m]) only = m;

        for (int i = 0; i <= 4; i++)
        {
            double y = PadTop + h * i / 4;
            Grid_.Children.Add(HLine(PadLeft, PadLeft + w, y));
            double frac = 1 - i / 4.0;
            string text = only >= 0
                ? (_scale[only] * frac).ToString(_scale[only] >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture)
                  + " " + HistoryMetrics.Units[only]
                : $"{frac * 100:0} %";
            Grid_.Children.Add(Label(text, 4, y - 8, Muted, 10.5, PadLeft - 10));
        }

        // 時間軸：五個刻度，格式依區間長度自動選擇
        var span = ToUtc - FromUtc;
        string fmt = span.TotalMinutes <= 5 ? "HH:mm:ss"
                   : span.TotalHours <= 12 ? "HH:mm"
                   : span.TotalDays <= 3 ? "MM-dd HH:mm"
                   : "MM-dd";
        for (int i = 0; i <= 4; i++)
        {
            double x = PadLeft + w * i / 4;
            Grid_.Children.Add(VLine(x, PadTop, PadTop + h, GridLine, null));
            var t = (FromUtc + TimeSpan.FromTicks(span.Ticks * i / 4)).ToLocalTime();
            double lx = Math.Min(Math.Max(x - 26, 0), ActualWidth - 56);
            Grid_.Children.Add(Label(t.ToString(fmt), lx, PadTop + h + 5, Muted, 10.5, 56, true));
        }
    }

    private void DrawSeries()
    {
        var s = _drawn;
        for (int m = 0; m < HistoryMetrics.Count; m++)
        {
            if (!Active[m]) continue;
            if (!s.SecondLevel) Plot.Children.Add(Envelope(s, m));
            Plot.Children.Add(Line(s, m));
        }
    }

    // 分鐘級資料的每分鐘極值帶：淡淡一層，讓「平均看似平穩但其實有尖峰」無所遁形
    private Polygon Envelope(HistorySeries s, int m)
    {
        var pts = new PointCollection();
        for (int i = 0; i < s.Count; i++) pts.Add(new Point(XOf(s.Times[i]), YOf(s.H(i, m), m)));
        for (int i = s.Count - 1; i >= 0; i--) pts.Add(new Point(XOf(s.Times[i]), YOf(s.L(i, m), m)));
        return new Polygon { Points = pts, Fill = _metricBrush[m], Opacity = 0.16 };
    }

    private Polyline Line(HistorySeries s, int m)
    {
        var pts = new PointCollection();
        for (int i = 0; i < s.Count; i++) pts.Add(new Point(XOf(s.Times[i]), YOf(s.A(i, m), m)));
        return new Polyline
        {
            Points = pts, Stroke = _metricBrush[m], StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
        };
    }

    private void DrawMarkers()
    {
        foreach (var e in Markers)
        {
            double x = XOf(e.TimeUtc);
            if (x < PadLeft - 1 || x > PadLeft + PlotW + 1) continue;
            var brush = SeverityBrush(e.Severity);
            Plot.Children.Add(VLine(x, PadTop, PadTop + PlotH, brush, [3.0, 3.0]));
            var tri = new Polygon
            {
                Points = [new(x - 4, PadTop), new(x + 4, PadTop), new(x, PadTop + 6)],
                Fill = brush,
            };
            Plot.Children.Add(tri);
        }
    }

    // 事件標記與狀態徽章共用同一套四階狀態色，換淺色主題時一起換成加深的那一組
    private static Brush SeverityBrush(Severity s) => SeverityToBrushConverter.Brush(s);

    // ── 繪圖原件 ──────────────────────────────────────────────────────────

    private static Line HLine(double x1, double x2, double y)
        => new() { X1 = x1, X2 = x2, Y1 = y, Y2 = y, Stroke = GridLine, StrokeThickness = 1 };

    private static Line VLine(double x, double y1, double y2, Brush stroke, double[]? dash)
    {
        var l = new Line { X1 = x, X2 = x, Y1 = y1, Y2 = y2, Stroke = stroke, StrokeThickness = 1 };
        if (dash is not null) l.StrokeDashArray = new DoubleCollection(dash);
        return l;
    }

    private static TextBlock Label(
        string text, double x, double y, Brush brush, double size, double width = 0, bool center = false)
    {
        var t = new TextBlock
        {
            Text = text, Foreground = brush, FontSize = size,
            FontFamily = new FontFamily("Microsoft JhengHei UI, Segoe UI"),
        };
        if (width > 0)
        {
            t.Width = width;
            t.TextAlignment = center ? TextAlignment.Center : TextAlignment.Right;
        }
        Canvas.SetLeft(t, x);
        Canvas.SetTop(t, y);
        return t;
    }

    // ── 互動 ──────────────────────────────────────────────────────────────

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Delta == 0) return;
        double anchor = Math.Clamp((e.GetPosition(this).X - PadLeft) / PlotW, 0, 1);
        ZoomRequested?.Invoke(e.Delta > 0 ? 1 / 1.35 : 1.35, anchor);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount >= 2) { ResetRequested?.Invoke(); return; }
        _dragging = true;
        _dragX = e.GetPosition(this).X;
        CaptureMouse();
        Cursor = Cursors.ScrollWE;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_dragging)
        {
            double dx = p.X - _dragX;
            if (Math.Abs(dx) >= 1)
            {
                _dragX = p.X;
                PanRequested?.Invoke(-dx / PlotW);      // 往左拖 → 看更晚的時間
            }
            return;
        }
        DrawCrosshair(p.X);
    }

    // 十字準線＋讀值卡：對齊到最接近的取樣點，並附上 6 像素內的事件標題。
    private void DrawCrosshair(double x)
    {
        Overlay.Children.Clear();
        if (_drawn.Count == 0 || x < PadLeft || x > PadLeft + PlotW) return;

        int best = 0;
        double bestDx = double.MaxValue;
        for (int i = 0; i < _drawn.Count; i++)
        {
            double dx = Math.Abs(XOf(_drawn.Times[i]) - x);
            if (dx < bestDx) { bestDx = dx; best = i; }
        }
        double px = XOf(_drawn.Times[best]);
        Overlay.Children.Add(VLine(px, PadTop, PadTop + PlotH, Muted, [2.0, 2.0]));

        var card = ReadoutCard(best, px);
        Overlay.Children.Add(card);
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = px + 12;
        if (left + card.DesiredSize.Width > ActualWidth - 4) left = px - 12 - card.DesiredSize.Width;
        Canvas.SetLeft(card, Math.Max(2, left));
        Canvas.SetTop(card, PadTop + 4);
    }

    private Border ReadoutCard(int index, double px)
    {
        var panel = new StackPanel();
        var t = _drawn.Times[index].ToLocalTime();
        panel.Children.Add(new TextBlock
        {
            Text = t.ToString("yyyy-MM-dd HH:mm:ss"), Foreground = Ink, FontSize = 11.5,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3),
        });

        for (int m = 0; m < HistoryMetrics.Count; m++)
        {
            if (!Active[m]) continue;
            string val = _drawn.SecondLevel
                ? $"{_drawn.A(index, m):0.#}"
                : $"{_drawn.A(index, m):0.#}（{_drawn.L(index, m):0.#}–{_drawn.H(index, m):0.#}）";
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Rectangle
            {
                Width = 8, Height = 8, Fill = _metricBrush[m], RadiusX = 2, RadiusY = 2,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{HistoryMetrics.Titles[m]}　{val} {HistoryMetrics.Units[m]}",
                Foreground = Muted, FontSize = 11,
            });
            panel.Children.Add(row);
        }

        foreach (var e in Markers)
        {
            if (Math.Abs(XOf(e.TimeUtc) - px) > 6) continue;
            panel.Children.Add(new TextBlock
            {
                Text = $"◆ {e.Title}", Foreground = SeverityBrush(e.Severity), FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0), MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        return new Border
        {
            Background = Surface, BorderBrush = Hairline, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 7, 9, 7),
            Child = panel, Opacity = 0.97,
        };
    }

    /// <summary>游標所在的時間（供檢視顯示，或未來的區間選取用）。</summary>
    public DateTime TimeUnder(Point p) => TimeAt(p.X);








}
