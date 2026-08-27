using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace XinSpect;

/// <summary>
/// 即時走勢圖（面積 + 折線），資料來源為 <see cref="MetricHistory"/>。
/// 視覺物件（格線 / 面積 / 折線 / 端點）僅建立一次，之後每次來源 Push 只更新幾何與座標，
/// 避免每秒重建整棵視覺樹造成的配置/佈局風暴（於多核心分頁尤為明顯）。
/// 未顯示（切到其他分頁）時完全略過重繪，重新顯示時再補畫最新快照。
/// </summary>
public partial class HistoryGraph : UserControl
{
    private MetricHistory? _hooked;

    // ── 保留的視覺物件（建立一次，之後就地更新）──
    private bool _built;
    private readonly Line[] _hGrid = new Line[3];
    private readonly Line[] _vGrid = new Line[5];
    private Path _area = null!;
    private PathFigure _areaFig = null!;
    private LineSegment _areaStart = null!;   // 底邊 → 首點
    private PolyLineSegment _areaBody = null!; // 沿資料折線
    private LineSegment _areaEnd = null!;     // 末點 → 底邊
    private LinearGradientBrush _areaFill = null!;
    private Polyline _line = null!;
    private SolidColorBrush _lineBrush = null!;
    private Ellipse _dot = null!;
    private SolidColorBrush _dotBrush = null!;
    private TranslateTransform _dotXf = null!;

    private double _lastW, _lastH;
    private Color _lastCol = Colors.Transparent;

    public HistoryGraph()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
        // 切回本分頁（重新可見）時補畫最新快照；切走時 OnData 會自動略過
        IsVisibleChanged += (_, _) => { if (IsVisible) Redraw(); };
    }

    public static readonly DependencyProperty HistoryProperty =
        DependencyProperty.Register(nameof(History), typeof(MetricHistory), typeof(HistoryGraph),
            new PropertyMetadata(null, OnHistoryChanged));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(HistoryGraph),
            new PropertyMetadata(null, (d, _) => ((HistoryGraph)d).Redraw()));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(HistoryGraph),
            new PropertyMetadata("", (d, e) => ((HistoryGraph)d).CaptionText.Text = (string)e.NewValue));

    public static readonly DependencyProperty ShowValueProperty =
        DependencyProperty.Register(nameof(ShowValue), typeof(bool), typeof(HistoryGraph),
            new PropertyMetadata(true, (d, _) => ((HistoryGraph)d).Redraw()));

    public MetricHistory? History { get => (MetricHistory?)GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public bool ShowValue { get => (bool)GetValue(ShowValueProperty); set => SetValue(ShowValueProperty, value); }

    private static void OnHistoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (HistoryGraph)d;
        if (g._hooked is not null) g._hooked.Updated -= g.OnData;
        g._hooked = e.NewValue as MetricHistory;
        if (g._hooked is not null) g._hooked.Updated += g.OnData;
        g.Redraw();
    }

    private void OnData()
    {
        // 未顯示時完全略過（切到其他分頁的圖不需重畫）
        if (!IsVisible) return;
        // Updated 可能來自背景執行緒（實際多在 UI 執行緒），保險起見排入 Dispatcher
        if (Dispatcher.CheckAccess()) Redraw();
        else Dispatcher.BeginInvoke(OnData);
    }

    private Color StrokeColor()
    {
        if (Stroke is SolidColorBrush b) return b.Color;
        return Color.FromRgb(0x39, 0x87, 0xe5);
    }

    /// <summary>建立一次性的視覺物件並依 z 序加入畫布（格線 → 面積 → 折線 → 端點）。</summary>
    private void EnsureBuilt()
    {
        if (_built) return;

        var gridBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        for (int i = 0; i < _hGrid.Length; i++)
        {
            _hGrid[i] = new Line { Stroke = gridBrush, StrokeThickness = 1 };
            GridLines.Children.Add(_hGrid[i]);
        }
        for (int i = 0; i < _vGrid.Length; i++)
        {
            _vGrid[i] = new Line { Stroke = gridBrush, StrokeThickness = 1 };
            GridLines.Children.Add(_vGrid[i]);
        }

        _areaStart = new LineSegment { IsStroked = false };
        _areaBody = new PolyLineSegment { IsStroked = false };
        _areaEnd = new LineSegment { IsStroked = false };
        _areaFig = new PathFigure { IsClosed = true };
        _areaFig.Segments.Add(_areaStart);
        _areaFig.Segments.Add(_areaBody);
        _areaFig.Segments.Add(_areaEnd);
        var areaGeom = new PathGeometry();
        areaGeom.Figures.Add(_areaFig);
        _areaFill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        _areaFill.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        _areaFill.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        _area = new Path { Data = areaGeom, Fill = _areaFill };
        Plot.Children.Add(_area);

        _lineBrush = new SolidColorBrush();
        _line = new Polyline
        {
            Stroke = _lineBrush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Plot.Children.Add(_line);

        _dotBrush = new SolidColorBrush();
        _dotXf = new TranslateTransform();
        _dot = new Ellipse { Width = 6, Height = 6, Fill = _dotBrush, RenderTransform = _dotXf };
        Plot.Children.Add(_dot);

        _built = true;
        _lastW = _lastH = 0;                  // 迫使下次重新定位格線
        _lastCol = Colors.Transparent;        // 迫使下次重新上色
    }

    private void Redraw()
    {
        if (Plot is null) return;
        EnsureBuilt();

        double w = ActualWidth, h = ActualHeight;
        bool tooSmall = w < 8 || h < 8;
        // 太小或未顯示：把資料層藏起來（避免殘影），但保留物件
        _area.Visibility = _line.Visibility = _dot.Visibility =
            tooSmall ? Visibility.Collapsed : Visibility.Visible;
        if (tooSmall) return;

        // 尺寸變動時才重新定位格線
        if (w != _lastW || h != _lastH)
        {
            for (int i = 0; i < _hGrid.Length; i++)
            {
                double y = h * (i + 1) / 4.0;
                var ln = _hGrid[i]; ln.X1 = 0; ln.X2 = w; ln.Y1 = y; ln.Y2 = y;
            }
            for (int i = 0; i < _vGrid.Length; i++)
            {
                double x = w * (i + 1) / 6.0;
                var ln = _vGrid[i]; ln.X1 = x; ln.X2 = x; ln.Y1 = 0; ln.Y2 = h;
            }
            _lastW = w; _lastH = h;
        }

        // 顏色變動時才重建筆刷/漸層色
        var col = StrokeColor();
        if (col != _lastCol)
        {
            _lineBrush.Color = col;
            _dotBrush.Color = col;
            _areaFill.GradientStops[0].Color = Color.FromArgb(0x66, col.R, col.G, col.B);
            _areaFill.GradientStops[1].Color = Color.FromArgb(0x08, col.R, col.G, col.B);
            _lastCol = col;
        }

        ValueText.Text = ShowValue ? (History?.CurrentText ?? "") : "";

        var data = History?.Snapshot();
        if (data is null || data.Length < 2)
        {
            _area.Visibility = _line.Visibility = _dot.Visibility = Visibility.Collapsed;
            return;
        }

        double max = History!.FixedMax ?? 0;
        if (max <= 0)
        {
            foreach (var v in data) if (v > max) max = v;
            max *= 1.18;
        }
        if (max <= 0) max = 1;

        const double padTop = 22, padBottom = 4;
        double plotH = Math.Max(1, h - padTop - padBottom);
        double n = data.Length;

        double X(int i) => w * i / (n - 1);
        double Y(double v) => padTop + plotH - Math.Clamp(v / max, 0, 1) * plotH;

        var pts = new PointCollection(data.Length);
        for (int i = 0; i < data.Length; i++) pts.Add(new Point(X(i), Y(data[i])));

        _line.Points = pts;

        // 面積：底邊(首) → 首點 → 折線 → 末點 → 底邊(末)，閉合回起點
        _areaFig.StartPoint = new Point(X(0), h);
        _areaStart.Point = pts[0];
        _areaBody.Points = pts;
        _areaEnd.Point = new Point(X(data.Length - 1), h);

        var lastPt = pts[^1];
        _dotXf.X = lastPt.X - 3;
        _dotXf.Y = lastPt.Y - 3;
    }
}
