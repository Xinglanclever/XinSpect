using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 逐核液柱：一核一管，液面高度＝該核當下使用率，液體顏色＝該核溫度（沿用
/// <see cref="HeatConverter"/> 的同一套色階，所以和熱區圖對得上）。管壁上另有一條會慢慢沉下來的
/// 尖峰線，記住最近幾秒衝到過的高點——瞬間的滿載不會在每秒取樣裡留下痕跡，這條線會。
/// </summary>
/// <remarks>
/// <para>
/// 三個「動」都各自對應一個真的量：液面高度是使用率、顏色是溫度、氣泡只在高負載時出現。
/// 沒有純裝飾的動畫；沒有讀值的核心（拿不到溫度）畫成灰管，不用強調色假裝涼。
/// </para>
/// <para>
/// 成本控制：波紋計時器只在控制項可見且 <see cref="Motion"/> 允許時跑；關掉動態效果後仍然
/// 正確顯示讀值（改由資料變更事件觸發重畫），只是液面直接跳到位、沒有波紋與氣泡。
/// </para>
/// </remarks>
public sealed class CoreColumns : FrameworkElement
{
    /// <summary>負載超過這個百分比才冒氣泡（＝「這顆核心正在被榨」的視覺門檻）。</summary>
    private const double BubbleLoad = 80;

    /// <summary>尖峰線每幀往下沉的百分點，約 3 秒歸零。</summary>
    private const double PeakDecay = 0.9;

    public static readonly DependencyProperty CoresProperty = DependencyProperty.Register(
        nameof(Cores), typeof(IEnumerable), typeof(CoreColumns),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnCoresChanged));
    /// <summary><see cref="CoreRow"/> 集合。</summary>
    public IEnumerable? Cores { get => (IEnumerable?)GetValue(CoresProperty); set => SetValue(CoresProperty, value); }

    private CoreRow[] _rows = [];
    private double[] _shown = [];   // 平滑後的液面（%）
    private double[] _peak = [];     // 尖峰線（%）
    private readonly List<Bubble> _bubbles = [];
    private DispatcherTimer? _timer;
    private double _phase;
    private readonly Random _rng = new();

    private record struct Bubble(int Col, double Y, double R, double Speed);

    public CoreColumns()
    {
        MinHeight = 150;
        ClipToBounds = true;
        IsVisibleChanged += (_, _) => Sync();
        Loaded += (_, _) => { Motion.Changed += Sync; Sync(); };
        Unloaded += (_, _) => { Motion.Changed -= Sync; Unhook(Cores); _timer?.Stop(); };
    }

    private static void OnCoresChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (CoreColumns)d;
        c.Unhook(e.OldValue as IEnumerable);
        c.Hook(e.NewValue as IEnumerable);
        c.Rebuild();
    }

    // ── 資料訂閱：關掉動態效果後也要能更新讀值，所以不能只靠繪圖計時器 ──────────

    private void Hook(IEnumerable? src)
    {
        if (src is INotifyCollectionChanged cc) cc.CollectionChanged += OnCollectionChanged;
        foreach (object? o in src ?? Array.Empty<object>())
            if (o is INotifyPropertyChanged p) p.PropertyChanged += OnRowChanged;
    }

    private void Unhook(IEnumerable? src)
    {
        if (src is INotifyCollectionChanged cc) cc.CollectionChanged -= OnCollectionChanged;
        foreach (object? o in src ?? Array.Empty<object>())
            if (o is INotifyPropertyChanged p) p.PropertyChanged -= OnRowChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (object? o in e.OldItems ?? Array.Empty<object>())
            if (o is INotifyPropertyChanged p) p.PropertyChanged -= OnRowChanged;
        foreach (object? o in e.NewItems ?? Array.Empty<object>())
            if (o is INotifyPropertyChanged p) p.PropertyChanged += OnRowChanged;
        Rebuild();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_timer?.IsEnabled == true) return;   // 波紋計時器在跑時它自己會重畫
        Snap();
    }

    /// <summary>核心數變動：重配陣列並直接跳到當下讀值。</summary>
    private void Rebuild()
    {
        _rows = [.. (Cores ?? Array.Empty<object>()).OfType<CoreRow>()];
        _shown = new double[_rows.Length];
        _peak = new double[_rows.Length];
        _bubbles.Clear();
        Snap();
        Sync();
    }

    private void Snap()
    {
        for (int i = 0; i < _rows.Length; i++)
        {
            _shown[i] = Math.Clamp(_rows[i].LoadPercent, 0, 100);
            _peak[i] = Math.Max(_peak[i], _shown[i]);
        }
        InvalidateVisual();
    }

    /// <summary>依「看得見 × 允許動畫 × 有資料」決定波紋計時器要不要跑。</summary>
    private void Sync()
    {
        bool want = IsVisible && Motion.Enabled && _rows.Length > 0;
        if (want)
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
                _timer.Tick += OnTick;
            }
            _timer.Start();
        }
        else
        {
            _timer?.Stop();
            _bubbles.Clear();
            Snap();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsVisible || !Motion.Enabled) { Sync(); return; }
        _phase += 0.09;
        for (int i = 0; i < _rows.Length; i++)
        {
            double target = Math.Clamp(_rows[i].LoadPercent, 0, 100);
            _shown[i] += (target - _shown[i]) * 0.22;
            _peak[i] = Math.Max(target, _peak[i] - PeakDecay);

            if (_shown[i] > BubbleLoad && _bubbles.Count < _rows.Length * 3 && _rng.NextDouble() < 0.06)
                _bubbles.Add(new Bubble(i, 0, 1.2 + _rng.NextDouble() * 1.6, 0.4 + _rng.NextDouble() * 0.7));
        }

        for (int i = _bubbles.Count - 1; i >= 0; i--)
        {
            var b = _bubbles[i];
            b.Y += b.Speed;                        // 由液面往下起算，往上浮＝Y 增加
            double depth = ActualHeight * _shown[b.Col] / 100.0;
            if (b.Y >= depth - 2) _bubbles.RemoveAt(i);
            else _bubbles[i] = b;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        int n = _rows.Length;
        if (n == 0 || ActualWidth < 8 || ActualHeight < 24) return;

        const double LabelBand = 15;                       // 底部核心編號
        double h = Math.Max(10, ActualHeight - LabelBand);
        double slot = ActualWidth / n;
        double gap = Math.Min(4, slot * 0.22);
        double w = Math.Max(2, slot - gap);
        double radius = Math.Min(3, w / 3);

        var tube = VizPalette.Grid;
        var hair = VizPalette.Hairline;
        var muted = VizPalette.Muted;
        var wallPen = w >= 5 ? new Pen(hair, 1) : null;
        var typeface = new Typeface("Microsoft JhengHei UI");
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool labels = w >= 13;

        for (int i = 0; i < n; i++)
        {
            double x = i * slot + gap / 2;
            var bounds = new Rect(x, 0, w, h);

            // 空管：低透明度的格線色，讓「這格可以到多高」看得出來
            dc.PushOpacity(0.55);
            dc.DrawRoundedRectangle(tube, wallPen, bounds, radius, radius);
            dc.Pop();

            double pct = Math.Clamp(_shown[i], 0, 100);
            double fillH = h * pct / 100.0;
            Color? heat = HeatConverter.ColorFor(_rows[i].TempC);

            if (fillH >= 1)
            {
                var liquid = new SolidColorBrush(heat ?? VizPalette.Muted.Color);
                liquid.Freeze();
                var body = new Rect(x, h - fillH, w, fillH);
                dc.PushClip(new RectangleGeometry(bounds, radius, radius));
                dc.DrawRectangle(liquid, null, body);
                DrawMeniscus(dc, liquid, x, w, h - fillH, i);
                DrawBubbles(dc, i, x, w, h, fillH);
                dc.Pop();
            }

            // 尖峰線：最近數秒的高點，之後慢慢沉下來
            if (_peak[i] > pct + 1.5)
            {
                double py = h - h * Math.Clamp(_peak[i], 0, 100) / 100.0;
                var peakPen = new Pen(heat is Color c ? Frozen(c) : muted, 1);
                dc.PushOpacity(0.85);
                dc.DrawLine(peakPen, new Point(x, py), new Point(x + w, py));
                dc.Pop();
            }

            if (labels)
                dc.DrawText(new FormattedText($"{i}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, 9, muted, dip),
                    new Point(x + w / 2 - 3, h + 2));
        }

        if (!labels)
            dc.DrawText(new FormattedText($"{n} 核（管子太窄，省略編號；由左至右為核心 0 起算）",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 9, muted, dip),
                new Point(0, h + 2));
    }

    /// <summary>液面：一道低振幅正弦，只在動畫開著時起伏；關掉時是一條平線。</summary>
    private void DrawMeniscus(DrawingContext dc, Brush liquid, double x, double w, double top, int col)
    {
        double amp = _timer?.IsEnabled == true ? Math.Min(2.5, w / 6) : 0;
        if (amp <= 0.1) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x, top), true, true);
            int steps = Math.Max(3, (int)(w / 2));
            for (int s = 0; s <= steps; s++)
            {
                double t = s / (double)steps;
                double y = top - amp * Math.Sin(_phase * 2 + col * 0.7 + t * Math.PI * 2);
                c.LineTo(new Point(x + t * w, y), false, false);
            }
            c.LineTo(new Point(x + w, top + amp + 1), false, false);
            c.LineTo(new Point(x, top + amp + 1), false, false);
        }
        g.Freeze();
        dc.DrawGeometry(liquid, null, g);
    }

    private void DrawBubbles(DrawingContext dc, int col, double x, double w, double h, double fillH)
    {
        if (_bubbles.Count == 0) return;
        var white = VizPalette.Ink;
        foreach (var b in _bubbles)
        {
            if (b.Col != col) continue;
            double cy = h - b.Y;
            if (cy < h - fillH || cy > h) continue;
            dc.PushOpacity(0.30);
            dc.DrawEllipse(white, null, new Point(x + w * 0.5 + Math.Sin(b.Y * 0.2) * w * 0.18, cy), b.R, b.R);
            dc.Pop();
        }
    }

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
