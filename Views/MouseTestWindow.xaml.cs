using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace XinSpect;

/// <summary>
/// 滑鼠檢測：全螢幕測試左／右／中／側鍵觸發、滾輪上下、移動軌跡，並估計回報率與偵測「單擊變雙擊」抖動。
/// 純 WPF、零相依；所有數據取自實際輸入事件，不模擬。Esc 離開、C 清除計數。
/// </summary>
public partial class MouseTestWindow : Window
{
    private static readonly Brush IdleFill = new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x29));
    private static readonly Brush ActiveFill = new SolidColorBrush(Color.FromRgb(0x39, 0x87, 0xE5));

    private int _left, _right, _mid, _sideFwd, _sideBack, _wheelUp, _wheelDown;
    private double _minIntervalMs = double.NaN;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<MouseButton, double> _lastDownMs = new();
    private readonly Queue<double> _moveStamps = new();
    private readonly List<Ellipse> _trail = new();
    private const int TrailCap = 260;
    private const double ChatterMs = 70;   // 兩次同鍵按下間隔低於此值，視為疑似微動抖動

    public MouseTestWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { Focus(); Keyboard.Focus(this); };
    }

    private void Any_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var (shape, isDown) = (Resolve(e.ChangedButton), true);
        SetActive(e.ChangedButton, isDown);

        double now = _clock.Elapsed.TotalMilliseconds;
        if (_lastDownMs.TryGetValue(e.ChangedButton, out double prev))
        {
            double dt = now - prev;
            if (double.IsNaN(_minIntervalMs) || dt < _minIntervalMs) _minIntervalMs = dt;
            MinIntervalText.Text = $"{_minIntervalMs:0} ms";
            if (dt < ChatterMs)
            {
                ChatterText.Text = $"偵測到 {dt:0} ms 的極短點擊間隔（{ZhButton(e.ChangedButton)}），可能為微動開關抖動（單擊變雙擊）。";
                ChatterBox.Visibility = Visibility.Visible;
            }
        }
        _lastDownMs[e.ChangedButton] = now;

        switch (e.ChangedButton)
        {
            case MouseButton.Left: LeftCount.Text = (++_left).ToString(); break;
            case MouseButton.Right: RightCount.Text = (++_right).ToString(); break;
            case MouseButton.Middle: MidCount.Text = (++_mid).ToString(); break;
            case MouseButton.XButton1: _sideBack++; SideCount.Text = $"{_sideFwd} / {_sideBack}"; break;
            case MouseButton.XButton2: _sideFwd++; SideCount.Text = $"{_sideFwd} / {_sideBack}"; break;
        }
    }

    private void Any_MouseUp(object sender, MouseButtonEventArgs e) => SetActive(e.ChangedButton, false);

    private void Win_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0) _wheelUp++; else if (e.Delta < 0) _wheelDown++;
        WheelCount.Text = $"{_wheelUp} / {_wheelDown}";
    }

    private void Win_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(TrailCanvas);
        CoordText.Text = $"{p.X:0}, {p.Y:0}";

        // 軌跡：加入一個小圓點，超過上限即移除最舊
        var dot = new Ellipse { Width = 6, Height = 6, Fill = ActiveFill, Opacity = 0.55 };
        Canvas.SetLeft(dot, p.X - 3);
        Canvas.SetTop(dot, p.Y - 3);
        TrailCanvas.Children.Add(dot);
        _trail.Add(dot);
        if (_trail.Count > TrailCap)
        {
            TrailCanvas.Children.Remove(_trail[0]);
            _trail.RemoveAt(0);
        }

        // 回報率估計：以最近數個移動事件的平均間隔換算 Hz（僅供參考）
        double now = _clock.Elapsed.TotalMilliseconds;
        _moveStamps.Enqueue(now);
        while (_moveStamps.Count > 24) _moveStamps.Dequeue();
        if (_moveStamps.Count >= 6)
        {
            double span = now - _moveStamps.Peek();
            double avg = span / (_moveStamps.Count - 1);
            if (avg > 0) PollRateText.Text = $"≈ {1000.0 / avg:0} Hz";
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.C: ClearAll(); break;
        }
    }

    private void ClearAll()
    {
        _left = _right = _mid = _sideFwd = _sideBack = _wheelUp = _wheelDown = 0;
        _minIntervalMs = double.NaN;
        _lastDownMs.Clear();
        _moveStamps.Clear();
        foreach (var d in _trail) TrailCanvas.Children.Remove(d);
        _trail.Clear();
        LeftCount.Text = RightCount.Text = MidCount.Text = "0";
        SideCount.Text = WheelCount.Text = "0 / 0";
        MinIntervalText.Text = PollRateText.Text = "—";
        ChatterBox.Visibility = Visibility.Collapsed;
    }

    // 依鍵別把對應示意圖區塊亮起／熄滅。
    private void SetActive(MouseButton b, bool on)
    {
        var brush = on ? ActiveFill : IdleFill;
        switch (b)
        {
            case MouseButton.Left: LeftBtn.Fill = brush; break;
            case MouseButton.Right: RightBtn.Fill = brush; break;
            case MouseButton.Middle: MidBtn.Background = brush; break;
            case MouseButton.XButton1: SideBackBtn.Background = brush; break;
            case MouseButton.XButton2: SideFwdBtn.Background = brush; break;
        }
    }

    private System.Windows.Shapes.Shape? Resolve(MouseButton b) => null;   // 佔位：實際上色在 SetActive

    private static string ZhButton(MouseButton b) => b switch
    {
        MouseButton.Left => "左鍵",
        MouseButton.Right => "右鍵",
        MouseButton.Middle => "中鍵",
        MouseButton.XButton1 => "側鍵（後退）",
        MouseButton.XButton2 => "側鍵（前進）",
        _ => "按鍵",
    };
}
