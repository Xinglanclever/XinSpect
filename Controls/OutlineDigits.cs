using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 七段數字顯示器：關掉的段落不是消失，而是留下一道淡淡的輪廓（像真的 LED 面板熄燈那樣），
/// 所以數字跳動時位置不會飄，眼睛也看得出「這一格本來可以亮」。段落切換走 ~230 ms 的淡入淡出。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要自己畫而不是用字型：等寬字型的數字仍有字距與抗鋸齒差異，讀值每秒變動時整串會左右
/// 微抖；七段每一格寬度固定，而且熄燈的輪廓替「十位數暫時沒有」保住了版位。
/// </para>
/// <para>
/// 計時器只在段落亮度尚未到位時跑，到位就自己停——待機時這個控制項的成本是零。看不見（切到別頁、
/// 卡片收起）或使用者關掉 <see cref="Motion"/> 時直接跳到目標值，不補間。
/// </para>
/// </remarks>
public sealed class OutlineDigits : FrameworkElement
{
    // 段落位元（a 上、b 左上、c 右上、d 中、e 左下、f 右下、g 下）
    private const int A = 1, B = 2, C = 4, D = 8, E = 16, F = 32, G = 64;
    private const int SegCount = 7;

    /// <summary>每格除七段外多留一個位置給小數點／冒號的亮度。</summary>
    private const int SlotsPerCell = SegCount + 1;
    private const int DotSlot = SegCount;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlineDigits),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure
            | FrameworkPropertyMetadataOptions.AffectsRender, OnTextChanged));
    /// <summary>要顯示的字串。支援 <c>0-9</c>、<c>-</c>、<c>.</c>、<c>:</c> 與空白；其餘字元當空白。</summary>
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public static readonly DependencyProperty DigitHeightProperty = DependencyProperty.Register(
        nameof(DigitHeight), typeof(double), typeof(OutlineDigits),
        new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.AffectsMeasure
            | FrameworkPropertyMetadataOptions.AffectsRender, OnMetricsChanged));
    /// <summary>單一數字的高度（px）；寬度與段落粗細由此按比例推算。</summary>
    public double DigitHeight { get => (double)GetValue(DigitHeightProperty); set => SetValue(DigitHeightProperty, value); }

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(OutlineDigits),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>亮起段落的顏色；<c>null</c> 時取 <see cref="VizPalette"/> 的主文字色（跟著主題走）。</summary>
    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public static readonly DependencyProperty GhostOpacityProperty = DependencyProperty.Register(
        nameof(GhostOpacity), typeof(double), typeof(OutlineDigits),
        new FrameworkPropertyMetadata(0.13, FrameworkPropertyMetadataOptions.AffectsRender, OnMetricsChanged));
    /// <summary>熄燈段落的殘留亮度。太高會讓 8 以外的數字難認，太低就失去「位置固定」的作用。</summary>
    public double GhostOpacity { get => (double)GetValue(GhostOpacityProperty); set => SetValue(GhostOpacityProperty, value); }

    private StreamGeometry[]? _segs;      // 七段幾何（隨 DigitHeight 重建一次後凍結）
    private double _digitW, _thick, _height;
    private double[] _alpha = [];         // 目前亮度（cells × SlotsPerCell）
    private double[] _target = [];        // 目標亮度
    private char[] _cells = [];
    private DispatcherTimer? _timer;

    public OutlineDigits()
    {
        this.RepaintOnThemeChange();
        SnapsToDevicePixels = true;
        IsVisibleChanged += (_, _) => { if (IsVisible) Settle(); else Stop(); };
        Loaded += (_, _) => Motion.Changed += OnMotionChanged;
        Unloaded += (_, _) => { Motion.Changed -= OnMotionChanged; Stop(); };
    }

    private void OnMotionChanged() { if (!Motion.Enabled) Settle(); }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((OutlineDigits)d).Retarget();

    private static void OnMetricsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var o = (OutlineDigits)d;
        o._segs = null;
        o.Retarget();
    }

    /// <summary>重算目標亮度；格數變了就直接跳到位（版位變動時補間只會看起來像故障）。</summary>
    private void Retarget()
    {
        char[] cells = [.. (Text ?? "")];
        bool reshaped = cells.Length != _cells.Length;
        _cells = cells;
        _target = new double[cells.Length * SlotsPerCell];
        for (int i = 0; i < cells.Length; i++)
        {
            int mask = MaskOf(cells[i]);
            for (int s = 0; s < SegCount; s++)
                _target[i * SlotsPerCell + s] = (mask & (1 << s)) != 0 ? 1.0 : Math.Clamp(GhostOpacity, 0, 1);
            _target[i * SlotsPerCell + DotSlot] = cells[i] is '.' or ':' ? 1.0 : 0.0;
        }

        if (reshaped || !IsVisible || !Motion.Enabled) { Settle(); return; }
        Array.Resize(ref _alpha, _target.Length);
        Start();
    }

    /// <summary>跳到目標值並停下計時器。</summary>
    private void Settle()
    {
        Stop();
        _alpha = (double[])_target.Clone();
        InvalidateVisual();
    }

    private void Start()
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += OnTick;
        }
        _timer.Start();
    }

    private void Stop() => _timer?.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsVisible || !Motion.Enabled) { Settle(); return; }
        bool moving = false;
        for (int i = 0; i < _alpha.Length && i < _target.Length; i++)
        {
            double diff = _target[i] - _alpha[i];
            if (Math.Abs(diff) < 0.01) { _alpha[i] = _target[i]; continue; }
            _alpha[i] += diff * 0.45;
            moving = true;
        }
        if (!moving) Stop();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        EnsureGeometry();
        double w = 0;
        foreach (char ch in _cells) w += AdvanceOf(ch);
        return new Size(Math.Max(0, w), _height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        EnsureGeometry();
        if (_segs is null || _cells.Length == 0) return;
        Brush ink = Foreground ?? VizPalette.Ink;
        double x = 0;
        for (int i = 0; i < _cells.Length; i++)
        {
            char ch = _cells[i];
            double advance = AdvanceOf(ch);
            if (ch is '.' or ':')
            {
                DrawPunct(dc, ink, ch, x + advance / 2, Alpha(i, DotSlot));
            }
            else if (ch != ' ')
            {
                dc.PushTransform(new TranslateTransform(x, 0));
                for (int s = 0; s < SegCount; s++)
                {
                    double a = Alpha(i, s);
                    if (a <= 0.005) continue;
                    dc.PushOpacity(a);
                    dc.DrawGeometry(ink, null, _segs[s]);
                    dc.Pop();
                }
                dc.Pop();
            }
            x += advance;
        }
    }

    private double Alpha(int cell, int slot)
    {
        int i = cell * SlotsPerCell + slot;
        return i >= 0 && i < _alpha.Length ? Math.Clamp(_alpha[i], 0, 1) : 0;
    }

    private void DrawPunct(DrawingContext dc, Brush ink, char ch, double cx, double a)
    {
        if (a <= 0.005) return;
        double r = _thick / 2;
        dc.PushOpacity(a);
        if (ch == '.')
            dc.DrawEllipse(ink, null, new Point(cx, _height - r), r, r);
        else
        {
            dc.DrawEllipse(ink, null, new Point(cx, _height * 0.34), r, r);
            dc.DrawEllipse(ink, null, new Point(cx, _height * 0.72), r, r);
        }
        dc.Pop();
    }

    /// <summary>字元佔用寬度：標點窄、空白半格、數字與減號一整格（含右側字距）。</summary>
    private double AdvanceOf(char ch) => ch switch
    {
        '.' or ':' => _thick * 2.2,
        ' ' => _digitW * 0.45,
        _ => _digitW + _thick * 0.55,
    };

    private void EnsureGeometry()
    {
        double h = Math.Max(8, DigitHeight);
        if (_segs is not null && Math.Abs(h - _height) < 0.01) return;
        _height = h;
        _thick = h * 0.13;
        _digitW = h * 0.58;

        double t = _thick, gap = t * 0.35;
        double left = t / 2, right = _digitW - t / 2;
        double top = t / 2, mid = h / 2, bot = h - t / 2;
        _segs =
        [
            HBar(left + gap, right - gap, top, t),   // a
            VBar(left, top + gap, mid - gap, t),     // b
            VBar(right, top + gap, mid - gap, t),    // c
            HBar(left + gap, right - gap, mid, t),   // d
            VBar(left, mid + gap, bot - gap, t),     // e
            VBar(right, mid + gap, bot - gap, t),    // f
            HBar(left + gap, right - gap, bot, t),   // g
        ];
    }

    /// <summary>橫向段落：兩端收成尖角的六邊形，接縫處才不會出現直角撞直角的死角。</summary>
    private static StreamGeometry HBar(double xa, double xb, double y, double t)
    {
        double h = t / 2;
        return Poly(
            new Point(xa, y), new Point(xa + h, y - h), new Point(xb - h, y - h),
            new Point(xb, y), new Point(xb - h, y + h), new Point(xa + h, y + h));
    }

    private static StreamGeometry VBar(double x, double ya, double yb, double t)
    {
        double h = t / 2;
        return Poly(
            new Point(x, ya), new Point(x + h, ya + h), new Point(x + h, yb - h),
            new Point(x, yb), new Point(x - h, yb - h), new Point(x - h, ya + h));
    }

    private static StreamGeometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], true, true);
            for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i], false, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>字元 → 亮起的段落位元遮罩。未知字元回 0（整格熄燈，只留輪廓）。</summary>
    internal static int MaskOf(char ch) => ch switch
    {
        '0' => A | B | C | E | F | G,
        '1' => C | F,
        '2' => A | C | D | E | G,
        '3' => A | C | D | F | G,
        '4' => B | C | D | F,
        '5' => A | B | D | F | G,
        '6' => A | B | D | E | F | G,
        '7' => A | C | F,
        '8' => A | B | C | D | E | F | G,
        '9' => A | B | C | D | F | G,
        '-' => D,
        _ => 0,
    };
}
