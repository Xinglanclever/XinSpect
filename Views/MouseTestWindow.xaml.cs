using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace XinSpect;

/// <summary>
/// WPF 負責按鍵／滾輪／軌跡；回報到達間隔另由明確啟動的 WM_INPUT 取樣取得。
/// </summary>
public partial class MouseTestWindow : Window
{
    private static readonly Brush IdleFill = new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x29));
    private static readonly Brush ActiveFill = new SolidColorBrush(Color.FromRgb(0x39, 0x87, 0xE5));
    private static readonly string[] RawInputOperations =
        ["查詢既有 Raw Input 註冊", "讀取既有 Raw Input 註冊", "移除 Raw Input 註冊", "註冊 Raw Input 滑鼠", "讀取 WM_INPUT"];
    private sealed record RawDeviceItem(RawMouseDeviceSample Sample, string DisplayName);

    private int _left, _right, _mid, _sideFwd, _sideBack, _wheelUp, _wheelDown;
    private double _minIntervalMs = double.NaN;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<MouseButton, double> _lastDownMs = new();
    private RawMouseInputSession? _rawInput;
    private RawMouseSnapshot? _rawSnapshot;
    private string? _rawIdleStatus;
    private (double IntervalMs, MouseButton Button)? _chatter;
    private bool _updatingDevices;
    private readonly List<Ellipse> _trail = new();
    private const int TrailCap = 260;
    private const double ChatterMs = 70;   // 兩次同鍵按下間隔低於此值，視為疑似微動抖動

    public MouseTestWindow()
    {
        InitializeComponent();
        // 獨立視窗不在主視覺樹的逐頁轉換範圍：開啟時自己轉換一次（冪等，重複呼叫安全）。
        if (LanguageService.IsSimplified)
            LanguageService.ConvertVisualTree(this, true);
        LanguageService.Changed += OnLanguageChanged;
        Loaded += (_, _) =>
        {
            Focus(); Keyboard.Focus(this);
            if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is { } source)
            {
                _rawInput = new RawMouseInputSession(source);
                _rawInput.Updated += RefreshRawInput;
                RefreshRawInput();
            }
            else
            {
                StartSampleButton.IsEnabled = false;
                _rawIdleStatus = "視窗訊息來源不可用；無法取樣 Raw Input。";
                RenderRawIdle();
            }
        };
        Deactivated += (_, _) => _rawInput?.Stop();
        Closing += (_, _) => _rawInput?.Stop(); // 在 HWND 被摧毀前解除自己的註冊
        Closed += (_, _) =>
        {
            LanguageService.Changed -= OnLanguageChanged;
            if (_rawInput != null)
            {
                _rawInput.Stop();
                _rawInput.Updated -= RefreshRawInput;
                _rawInput.Dispose(); _rawInput = null;
            }
        };
    }

    private void Any_MouseDown(object sender, MouseButtonEventArgs e)
    {
        SetActive(e.ChangedButton, true);

        double now = _clock.Elapsed.TotalMilliseconds;
        if (_lastDownMs.TryGetValue(e.ChangedButton, out double prev))
        {
            double dt = now - prev;
            if (double.IsNaN(_minIntervalMs) || dt < _minIntervalMs) _minIntervalMs = dt;
            MinIntervalText.Text = $"{_minIntervalMs:0} ms";
            if (dt < ChatterMs)
            {
                _chatter = (dt, e.ChangedButton);
                RenderChatter();
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
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.C: ClearAll(); break;
            case Key.Space when e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase
                && e.OriginalSource is not ComboBox:
                if (_rawSnapshot?.IsRunning == true) _rawInput?.Stop(); else StartRawInput();
                e.Handled = true; break;
        }
    }

    private void ClearAll()
    {
        _left = _right = _mid = _sideFwd = _sideBack = _wheelUp = _wheelDown = 0;
        _minIntervalMs = double.NaN;
        _lastDownMs.Clear();
        ResetRawInput();
        foreach (var d in _trail) TrailCanvas.Children.Remove(d);
        _trail.Clear();
        LeftCount.Text = RightCount.Text = MidCount.Text = "0";
        SideCount.Text = WheelCount.Text = "0 / 0";
        MinIntervalText.Text = "—";
        _chatter = null;
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

    private void StartSample_Click(object sender, RoutedEventArgs e) => StartRawInput();
    private void StopSample_Click(object sender, RoutedEventArgs e) => _rawInput?.Stop();
    private void ResetSample_Click(object sender, RoutedEventArgs e) => ResetRawInput();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void StartRawInput()
    {
        if (_rawInput is null) return;
        RawMouseSampleStore.Shared.Clear("Raw Input 取樣進行中；尚未發佈結果。");
        _rawInput.Start();
    }

    private void ResetRawInput()
    {
        _rawInput?.Reset();
        _rawSnapshot = null;
        _updatingDevices = true; RawDeviceCombo.ItemsSource = null; _updatingDevices = false;
        _rawIdleStatus = "已重設；請按開始取樣。";
        RenderRawIdle();
        RawMouseSampleStore.Shared.Clear();
    }

    private void RenderRawIdle()
    {
        PollRateText.Text = "— Hz";
        RawDistributionText.Text = LanguageService.T("中位間隔 — · P95 — · 抖動 MAD —");
        RawSampleCountText.Text = LanguageService.T("有效間隔 0 筆 · 原始報告 0 筆");
        RawWindowText.Text = LanguageService.T("取樣窗 —");
        RawStatusText.Text = LanguageService.T(_rawIdleStatus);
    }

    private void RefreshRawInput()
    {
        if (_rawInput is null) return;
        var snapshot = _rawSnapshot = _rawInput.Snapshot();
        _rawIdleStatus = null;
        RenderRawDevices(snapshot);
    }

    private void RenderRawDevices(RawMouseSnapshot snapshot)
    {
        string? selected = (RawDeviceCombo.SelectedItem as RawDeviceItem)?.Sample.DeviceId;
        var items = snapshot.Devices.Select(d => new RawDeviceItem(d, LanguageService.T(d.DisplayName))).ToArray();
        _updatingDevices = true;
        RawDeviceCombo.ItemsSource = items;
        RawDeviceCombo.SelectedItem = items.FirstOrDefault(d => d.Sample.DeviceId == selected) ?? items.FirstOrDefault();
        _updatingDevices = false;
        StartSampleButton.IsEnabled = !snapshot.IsRunning;
        StopSampleButton.IsEnabled = snapshot.IsRunning;
        ShowSelectedRawDevice();
    }

    private void RawDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_updatingDevices) ShowSelectedRawDevice(); }

    private void ShowSelectedRawDevice()
    {
        if (_rawSnapshot is not { } snapshot) return;
        var device = (RawDeviceCombo.SelectedItem as RawDeviceItem)?.Sample;
        RawMouseSampleStore.Shared.Publish(snapshot, device?.DeviceId);
        var published = RawMouseSampleStore.Shared.Read(DateTimeOffset.UtcNow);
        RawStatusText.Text = TranslateRawStatus(snapshot.IsRunning ? snapshot.Status : published.Reason ?? snapshot.Status);
        RawWindowText.Text = FormatText("取樣窗 {0:N0} ms", snapshot.WindowMs) +
            (snapshot.StartedAtUtc == default ? "" : $" · UTC {snapshot.StartedAtUtc:HH:mm:ss.fff}–{snapshot.MeasuredAtUtc:HH:mm:ss.fff}");
        if (device is null)
        {
            PollRateText.Text = "— Hz";
            RawDistributionText.Text = LanguageService.T("中位間隔 — · P95 — · 抖動 MAD —");
            RawSampleCountText.Text = LanguageService.T("有效間隔 0 筆 · 原始報告 0 筆");
            return;
        }
        bool usable = device.Samples >= RawMouseSampling.MinimumIntervals && device.IdentityReadable
            && !device.Removed && !device.CapacityReached && device.InvalidTimestamps == 0
            && snapshot.Availability != FactAvailability.ReadError && snapshot.IgnoredDevices == 0;
        PollRateText.Text = usable ? $"{device.ObservedHz:N0} Hz · {LanguageService.T(MousePollRateFactsService.NearestRateLabel(device.ObservedHz))}" : "— Hz";
        RawDistributionText.Text = device.Samples == 0 ? LanguageService.T("中位間隔 — · P95 — · 抖動 MAD —")
            : FormatText("中位 {0:N1} µs · P95 {1:N1} µs · 抖動 MAD {2:N1} µs",
                device.MedianIntervalUs, device.P95IntervalUs, device.JitterMadUs);
        RawSampleCountText.Text = FormatText("有效間隔 {0:N0} 筆 · 原始報告 {1:N0} 筆\n" +
            "相對移動 {2:N0} · 停頓排除 {3:N0} · 其他報告排除 {4:N0}\n" +
            "非遞增時間 {5:N0} · 裝置超限至少 {6} · 讀取錯誤 {7}", device.Samples, device.Reports,
            device.MovementReports, device.IdleGaps, device.ExcludedReports, device.InvalidTimestamps,
            snapshot.IgnoredDevices, snapshot.ReadErrors);
    }

    private void OnLanguageChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(OnLanguageChanged);
            return;
        }
        if (_rawSnapshot is { } snapshot) RenderRawDevices(snapshot);
        else if (_rawIdleStatus != null) RenderRawIdle();
        RenderChatter();
    }

    private void RenderChatter()
    {
        if (_chatter is not { } chatter) return;
        ChatterText.Text = FormatText("偵測到 {0:0} ms 的極短點擊間隔（{1}），可能為微動開關抖動（單擊變雙擊）。",
            chatter.IntervalMs, LanguageService.T(ZhButton(chatter.Button)));
    }

    private static string TranslateRawStatus(string status)
    {
        // Win32 錯誤的 operation 是產品文字；錯誤碼及其他原始參數保持原值。
        const string failure = "失敗（Win32 ";
        foreach (string operation in RawInputOperations)
            if (status.StartsWith(operation + failure, StringComparison.Ordinal)
                && status.EndsWith("）。", StringComparison.Ordinal))
                return FormatText("{0}失敗（Win32 {1}）。", LanguageService.T(operation),
                    status[(operation.Length + failure.Length)..^2]);
        return LanguageService.T(status);
    }

    private static string FormatText(string template, params object[] values)
        => string.Format(CultureInfo.CurrentCulture, LanguageService.T(template), values);

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
