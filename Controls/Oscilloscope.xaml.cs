using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace XinSpect;

/// <summary>
/// 示波器風格即時波形（CRT 磷光）：資料來源為 <see cref="MetricHistory"/>。
/// 暗底 + 分格線 + 發光掃描線 + 尾端光點；視覺物件僅建立一次，之後就地更新幾何。
/// 未顯示時略過重繪；重新顯示時補畫最新快照。與 HistoryGraph 同一套省負載策略。
/// </summary>
/// <remarks>
/// 整支示波器只吃<b>一個</b>顏色輸入：<see cref="TraceBrush"/>。面板底色、描邊、分格線、
/// 光暈、掃描光點、抬頭文字全部由它推導，所以呼叫端把通道色設成琥珀色時，不會出現
/// 「琥珀波形壓在綠色格線上」這種前後不一致（改版前正是如此）。
/// <para>沒指定 <see cref="TraceBrush"/> 時跟著目前強調色走——迷你懸浮視窗就是這樣換色的。
/// 面板本身刻意不跟深淺主題翻白：磷光管的可讀性來自暗底亮跡。</para>
/// </remarks>
public partial class Oscilloscope : UserControl
{
    private MetricHistory? _hooked;

    private bool _built;
    private readonly Line[] _hGrid = new Line[5];
    private readonly Line[] _vGrid = new Line[9];
    private Polyline _trace = null!;
    private SolidColorBrush _traceBrush = null!;
    private SolidColorBrush _gridBrush = null!;
    private SolidColorBrush _plateBrush = null!;
    private SolidColorBrush _edgeBrush = null!;
    private SolidColorBrush _beamBrush = null!;
    private SolidColorBrush _captionBrush = null!;
    private SolidColorBrush _valueBrush = null!;
    private Ellipse _beam = null!;
    private TranslateTransform _beamXf = null!;

    private double _lastW, _lastH;
    private Color _lastCol = Colors.Transparent;

    public Oscilloscope()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) =>
        {
            ThemeService.Changed -= OnThemeChanged;   // 重複 Loaded 不重覆訂閱
            ThemeService.Changed += OnThemeChanged;
            Redraw();
        };
        Unloaded += (_, _) => ThemeService.Changed -= OnThemeChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible) Redraw(); };
    }

    /// <summary>換強調色時重推整組顏色（未指定 TraceBrush 的場合）。守衛要先清掉才會重算。</summary>
    private void OnThemeChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnThemeChanged); return; }
        _lastCol = Colors.Transparent;
        Redraw();
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

    /// <summary>波形色：未指定 <see cref="TraceBrush"/> 時跟著目前強調色走。</summary>
    private Color TraceColor() => TraceBrush is SolidColorBrush b ? b.Color : VizPalette.AccentColor;

    private void EnsureBuilt()
    {
        if (_built) return;

        _gridBrush = new SolidColorBrush();
        for (int i = 0; i < _hGrid.Length; i++) { _hGrid[i] = new Line { Stroke = _gridBrush, StrokeThickness = 1 }; GridLines.Children.Add(_hGrid[i]); }
        for (int i = 0; i < _vGrid.Length; i++) { _vGrid[i] = new Line { Stroke = _gridBrush, StrokeThickness = 1 }; GridLines.Children.Add(_vGrid[i]); }

        _traceBrush = new SolidColorBrush();
        _trace = new Polyline
        {
            Stroke = _traceBrush,
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 },
        };
        Plot.Children.Add(_trace);

        _beamBrush = new SolidColorBrush();
        _beamXf = new TranslateTransform();
        _beam = new Ellipse { Width = 6, Height = 6, Fill = _beamBrush, RenderTransform = _beamXf, Opacity = 0.9 };
        Plot.Children.Add(_beam);

        _plateBrush = new SolidColorBrush();
        _edgeBrush = new SolidColorBrush();
        Plate.Background = _plateBrush;
        Plate.BorderBrush = _edgeBrush;

        _captionBrush = new SolidColorBrush();
        _valueBrush = new SolidColorBrush();
        CaptionText.Foreground = _captionBrush;
        ValueText.Foreground = _valueBrush;

        _built = true;
        _lastW = _lastH = 0;
        _lastCol = Colors.Transparent;
    }

    private void Redraw()
    {
        if (Plot is null) return;
        EnsureBuilt();

        // 顏色先推導：面板底色與抬頭文字跟尺寸無關，太小而提早退出時也該是對的
        var col = TraceColor();
        if (col != _lastCol)
        {
            var black = Colors.Black;
            _traceBrush.Color = col;
            if (_trace.Effect is DropShadowEffect g) g.Color = col;
            _gridBrush.Color = Color.FromArgb(0x22, col.R, col.G, col.B);
            _plateBrush.Color = VizPalette.Blend(col, black, 0.92);        // 近黑的管面，僅帶一點通道色
            _edgeBrush.Color = VizPalette.Blend(col, black, 0.78);
            _beamBrush.Color = VizPalette.Blend(col, Colors.White, 0.75);  // 掃描光點：偏白但仍屬同一色系
            _captionBrush.Color = VizPalette.Blend(col, Colors.White, 0.30);
            _valueBrush.Color = VizPalette.Blend(col, Colors.White, 0.50);
            _lastCol = col;
        }

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

        ValueText.Text = History?.CurrentText ?? "";

        var data = History?.Snapshot();
        // 與 HistoryGraph 同一原則：完全沒讀到的項目不畫波形，避免 0 值直線被當成量測結果
        if (data is null || data.Length < 2 || History?.HasData != true)
        { _trace.Visibility = _beam.Visibility = Visibility.Collapsed; return; }

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
