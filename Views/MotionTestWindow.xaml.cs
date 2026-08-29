using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace XinSpect;

/// <summary>動態與幀時間檢測：以移動條讓肉眼判讀拖影／過衝，同時實測每幀的呈現間隔。</summary>
/// <remarks>
/// 零外部相依，也不查任何規格表。
/// <para>
/// 幀時間來自合成器每幀回報的時間戳（<see cref="RenderingEventArgs.RenderingTime"/>），
/// 因此量到的是<b>本程式實際被畫出來的節奏</b>——不是顯示器面板的規格，也不是驅動程式宣稱的更新率。
/// 推估更新率取中位數而非平均：少數超長幀會把平均拉高，用平均當基準會低估、也會漏算長幀。
/// </para>
/// <para>
/// 位置一律由時間算出（<c>x = 時鐘 × 速度</c>），不是每幀加一個固定位移；
/// 否則掉幀時移動速度會跟著變慢，拖影的判讀就失去共同基準。
/// </para>
/// </remarks>
public partial class MotionTestWindow : Window
{
    public MotionTestWindow()
    {
        InitializeComponent();
        _bars = new FrameworkElement[] { Bar0, Bar1, Bar2 };
    }

    private readonly FrameworkElement[] _bars;

    /// <summary>基準速度階梯（px/s）；三條移動條依序為 1×、2×、4×。</summary>
    private static readonly double[] Steps = { 240, 480, 960, 1440 };
    private int _step = 1;

    private static readonly (string Name, Color Color)[] Backdrops =
    {
        ("中灰 50%", Color.FromRgb(0x80, 0x80, 0x80)),
        ("黑", Colors.Black),
        ("白", Colors.White),
        ("深灰 20%", Color.FromRgb(0x33, 0x33, 0x33)),
    };
    private int _backdrop;

    private bool _paused;
    private TimeSpan _prev;
    private bool _havePrev;
    private double _clock;                          // 動畫時鐘（秒）：暫停時不前進，統計照量

    private const int WarmUp = 20;                  // 開窗前幾幀的抖動不代表穩態
    private const double GapMs = 500;               // 超過此值視為中斷（視窗切換、系統忙碌），計次但不入統計
    private int _seen, _counted, _gaps;
    private double _sumMs, _maxMs, _wallMs, _nextUiMs;
    private readonly int[] _hist = new int[2001];   // 0～200 ms，每 0.1 ms 一格：用來取中位數

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Focus();                                    // 全螢幕視窗要自己抓焦點，否則按鍵進不來
        Apply();
        CompositionTarget.Rendering += OnRendering;
    }

    private void Window_Closed(object sender, EventArgs e) => CompositionTarget.Rendering -= OnRendering;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left: _step = Math.Max(0, _step - 1); Apply(); break;
            case Key.Right: _step = Math.Min(Steps.Length - 1, _step + 1); Apply(); break;
            case Key.Up: _backdrop = (_backdrop + 1) % Backdrops.Length; Apply(); break;
            case Key.Down: _backdrop = (_backdrop + Backdrops.Length - 1) % Backdrops.Length; Apply(); break;
            case Key.Space: _paused = !_paused; Apply(); break;
            case Key.R: ResetStats(); break;
        }
    }

    // ── 每幀 ──────────────────────────────────────────────────

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs r) return;

        var now = r.RenderingTime;
        if (_havePrev && now == _prev) return;      // 同一輪合成可能回呼多次，重複的時間戳不算一幀
        if (!_havePrev) { _prev = now; _havePrev = true; return; }

        double ms = (now - _prev).TotalMilliseconds;
        _prev = now;

        if (!_paused) _clock += ms / 1000.0;
        Move();
        Account(ms);
    }

    private void Move()
    {
        double width = Root.ActualWidth, bar = Bar0.ActualWidth;
        if (width <= 0 || bar <= 0) return;

        double span = width + bar;                  // 走完整個畫面再整條退場，才不會半途瞬移
        for (int i = 0; i < _bars.Length; i++)
            Canvas.SetLeft(_bars[i], _clock * Steps[_step] * (1 << i) % span - bar);
    }

    private void Account(double ms)
    {
        if (++_seen <= WarmUp) return;
        if (ms > GapMs) { _gaps++; return; }

        _counted++;
        _sumMs += ms;
        if (ms > _maxMs) _maxMs = ms;
        _hist[Math.Clamp((int)(ms * 10), 0, _hist.Length - 1)]++;

        // 讀值每 250 ms 更新一次：每幀重排一次文字，量測本身就會拖慢被量的東西
        _wallMs += ms;
        if (_wallMs < _nextUiMs) return;
        _nextUiMs = _wallMs + 250;
        ShowStats();
    }

    // ── 統計 ──────────────────────────────────────────────────

    /// <summary>回傳幀間隔中位數（ms）與長幀次數（≥1.5 倍中位數，約等於少畫了一幀）。</summary>
    private (double Median, int Long) Distribution()
    {
        if (_counted == 0) return (0, 0);

        int half = _counted / 2, run = 0, mid = 0;
        for (int i = 0; i < _hist.Length; i++)
        {
            run += _hist[i];
            if (run > half) { mid = i; break; }
        }
        if (mid <= 0) return (0, 0);                // 中位數落在最低一格：數字沒有意義，不硬掰

        int longFrames = 0;
        for (int i = Math.Min((int)(mid * 1.5), _hist.Length - 1); i < _hist.Length; i++) longFrames += _hist[i];
        return (mid / 10.0, longFrames);
    }

    private void ShowStats()
    {
        var (median, longFrames) = Distribution();
        bool has = _counted > 0;

        AvgText.Text = has ? $"{_sumMs / _counted:0.00} ms" : "—";
        RateText.Text = median > 0 ? $"{1000 / median:0.0} Hz" : "—";
        MaxText.Text = has ? $"{_maxMs:0.00} ms" : "—";
        LongText.Text = has ? $"{longFrames}（{longFrames * 100.0 / _counted:0.0}%）" : "—";
        GapText.Text = _gaps.ToString();
        FrameText.Text = _counted.ToString();
    }

    private void ResetStats()
    {
        Array.Clear(_hist);
        _seen = _counted = _gaps = 0;
        _sumMs = _maxMs = _wallMs = _nextUiMs = 0;
        ShowStats();
    }

    /// <summary>套用速度／背景／暫停狀態，並重設統計——換條件後把舊數字留著會混淆判讀。</summary>
    private void Apply()
    {
        Root.Background = new SolidColorBrush(Backdrops[_backdrop].Color);
        double s = Steps[_step];
        SpeedText.Text = $"移動速度　{s:0} / {s * 2:0} / {s * 4:0} px/s　　背景：{Backdrops[_backdrop].Name}"
                       + (_paused ? "　　（已暫停移動，仍持續計幀）" : "");
        ResetStats();
    }
}
