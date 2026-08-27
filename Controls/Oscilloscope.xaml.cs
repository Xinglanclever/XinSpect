using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace XinSpect;

/// <summary>
/// 示波器風格即時波形（CRT 綠磷光）：資料來源為 <see cref="MetricHistory"/>。
/// 深色底 + 綠色分格線 + 發光掃描線 + 尾端光點；視覺物件僅建立一次，之後就地更新幾何。
/// 未顯示時略過重繪；重新顯示時補畫最新快照。與 HistoryGraph 同一套省負載策略。
/// </summary>
public partial class Oscilloscope : UserControl
{
    private MetricHistory? _hooked;

    private bool _built;
    private readonly Line[] _hGrid = new Line[5];
    private readonly Line[] _vGrid = new Line[9];
    private Polyline _trace = null!;
    private SolidColorBrush _traceBrush = null!;
    private Ellipse _beam = null!;
    private TranslateTransform _beamXf = null!;

    private double _lastW, _lastH;
    private Color _lastCol = Colors.Transparent;

    public Oscilloscope()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
        IsVisibleChanged += (_, _) => { if (IsVisible) Redraw(); };
    }

    public static readonly DependencyProperty HistoryProperty =
        DependencyProperty.Register(nameof(History), typeof(MetricHistory), typeof(Oscilloscope),
            new PropertyMetadata(null, OnHistoryChanged));

    public static readonly DependencyProperty TraceBrushProperty =
        DependencyProperty.Register(nameof(TraceBrush), typeof(Brush), typeof(Oscilloscope),
            new PropertyMetadata(null, (d, _) => ((Oscilloscope)d).Redraw()));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(Oscilloscope),
            new PropertyMetadata("", (d, e) => { if (((Oscilloscope)d).CaptionText is { } t) t.Text = (string)e.NewValue; }));

    public MetricHistory? History { get => (MetricHistory?)GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public Brush? TraceBrush { get => (Brush?)GetValue(TraceBrushProperty); set => SetValue(TraceBrushProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    private static void OnHistoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (Oscilloscope)d;
        if (g._hooked is not null) g._hooked.Updated -= g.OnData;
        g._hooked = e.NewValue as MetricHistory;
        if (g._hooked is not null) g._hooked.Updated += g.OnData;
        g.Redraw();
    }

    private void OnData()
    {
        if (!IsVisible) return;
        if (Dispatcher.CheckAccess()) Redraw();
        else Dispatcher.BeginInvoke(OnData);
    }

    private Color TraceColor() => TraceBrush is SolidColorBrush b ? b.Color : Color.FromRgb(0x35, 0xE0, 0x6A);

    private void EnsureBuilt()
    {
        if (_built) return;

        var gridBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x3D, 0xE0, 0x8A));
        for (int i = 0; i < _hGrid.Length; i++) { _hGrid[i] = new Line { Stroke = gridBrush, StrokeThickness = 1 }; GridLines.Children.Add(_hGrid[i]); }
        for (int i = 0; i < _vGrid.Length; i++) { _vGrid[i] = new Line { Stroke = gridBrush, StrokeThickness = 1 }; GridLines.Children.Add(_vGrid[i]); }

        _traceBrush = new SolidColorBrush();
        _trace = new Polyline
        {
            Stroke = _traceBrush,
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9, Color = Color.FromRgb(0x35, 0xE0, 0x6A) },
        };
        Plot.Children.Add(_trace);

        _beamXf = new TranslateTransform();
        _beam = new Ellipse { Width = 6, Height = 6, Fill = Brushes.White, RenderTransform = _beamXf, Opacity = 0.9 };
        Plot.Children.Add(_beam);

        _built = true;
        _lastW = _lastH = 0;
        _lastCol = Colors.Transparent;
    }

    private void Redraw()
    {
        if (Plot is null) return;
        EnsureBuilt();

        double w = ActualWidth, h = ActualHeight;
        bool tooSmall = w < 8 || h < 8;
        _trace.Visibility = _beam.Visibility = tooSmall ? Visibility.Collapsed : Visibility.Visible;
        if (tooSmall) return;

        if (w != _lastW || h != _lastH)
        {
            for (int i = 0; i < _hGrid.Length; i++) { double y = h * (i + 1) / (_hGrid.Length + 1); var l = _hGrid[i]; l.X1 = 0; l.X2 = w; l.Y1 = y; l.Y2 = y; }
            for (int i = 0; i < _vGrid.Length; i++) { double x = w * (i + 1) / (_vGrid.Length + 1); var l = _vGrid[i]; l.X1 = x; l.X2 = x; l.Y1 = 0; l.Y2 = h; }
            _lastW = w; _lastH = h;
        }

        var col = TraceColor();
        if (col != _lastCol)
        {
            _traceBrush.Color = col;
            if (_trace.Effect is DropShadowEffect g) g.Color = col;
            _lastCol = col;
        }

        ValueText.Text = History?.CurrentText ?? "";

        var data = History?.Snapshot();
        if (data is null || data.Length < 2) { _trace.Visibility = _beam.Visibility = Visibility.Collapsed; return; }

        double max = History!.FixedMax ?? 0, min = 0;
        if (max <= 0)
        {
            max = double.MinValue;
            foreach (var v in data) { if (v > max) max = v; if (v < min) min = v; }
            if (max <= min) max = min + 1;
            double span = max - min;
            max += span * 0.12; min -= span * 0.12;          // 上下留白，波形不貼邊
        }
        double range = max - min; if (range <= 0) range = 1;

        const double pad = 20;
        double plotH = Math.Max(1, h - pad - 6);
        double n = data.Length;
        double X(int i) => w * i / (n - 1);
        double Y(double v) => pad + plotH - Math.Clamp((v - min) / range, 0, 1) * plotH;

        var pts = new PointCollection(data.Length);
        for (int i = 0; i < data.Length; i++) pts.Add(new Point(X(i), Y(data[i])));
        _trace.Points = pts;

        var last = pts[^1];
        _beamXf.X = last.X - 3;
        _beamXf.Y = last.Y - 3;
    }
}
