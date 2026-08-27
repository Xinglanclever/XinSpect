using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace XinSpect;

/// <summary>主視窗：承載導覽與各分頁檢視，並管理檢視模型生命週期。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly UserControl[] _views;
    private TrayService? _tray;
    private MiniOverlayWindow? _mini;
    private bool _ocRiskCleared;   // 本次執行是否已通過超頻風險兩階段確認
    private bool _initialized;      // Loaded 可能多次觸發（隱藏至系統匣後再顯示等）；確保只初始化一次

    public MainWindow()
    {
        ThemeService.Initialize();     // 解析 XAML 前套用已存主題/強調色，避免首格閃色
        InitializeComponent();
        DataContext = _vm;

        _views = new UserControl[]
        {
            new OverviewView(),
            new AiView(),
            new CpuView(),
            new MemoryView(),
            new MotherboardView(),
            new GpuView(),
            new StorageView(),
            new NetworkView(),
            new SensorsView(),
            new BenchView(),
            new OverclockView(),
            new GpuOcView(),
            new FanControlView(),
            new HealthView(),
            new ToolboxView(),
            new RankingView(),
            new SetupView(),
            new BrowserView(),
            new TerminalView(),
            new SettingsView(),
            new AboutView(),
        };

        ApplyAccentGlow();

        // Live 於背景載入完成後，套用目前分頁對應的感測器總表可見狀態
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Live)) ApplySensorsVisibility();
        };

        Loaded += (_, _) =>
        {
            if (_initialized) return;   // 防止重複初始化（重跑計時器與感測器引擎）
            _initialized = true;
            _vm.Initialize();
            Nav.SelectedIndex = 0;
            InitTray();
        };
    }

    // 感測器總表為每秒最重的一段格式化工作；僅在該分頁顯示時才更新。
    // 系統風扇即時值同理：僅在「系統風扇控制」分頁顯示時才每秒讀取。
    private void ApplySensorsVisibility()
    {
        int i = Nav.SelectedIndex;
        bool valid = i >= 0 && i < _views.Length;
        if (_vm.Live is not null)
        {
            _vm.Live.DetailedSensorsVisible = valid && _views[i] is SensorsView;
            _vm.Live.FanControlsVisible = valid && _views[i] is FanControlView;
        }
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = Nav.SelectedIndex;

        // 超頻分頁：進入前的兩階段風險確認。本次執行僅需通過一次；選「今後不再顯示」則永久略過。
        if (i >= 0 && i < _views.Length && _views[i] is OverclockView && !_ocRiskCleared)
        {
            if (!ConfirmOverclockRisk())
            {
                // 使用者放棄進入：還原到先前分頁（若無則回總覽）
                int back = e.RemovedItems.Count > 0 ? Nav.Items.IndexOf(e.RemovedItems[0]) : 0;
                Nav.SelectedIndex = back >= 0 ? back : 0;
                return;
            }
            _ocRiskCleared = true;
        }

        if (i >= 0 && i < _views.Length)
        {
            Host.Content = _views[i];
            PageTransition.PlayEnter(Host);   // 切頁淡入 + 輕微上滑
        }
        ApplySensorsVisibility();
    }

    // 顯示超頻風險兩階段對話框；使用者確認進入回傳 true。選「今後不再顯示」時持久化旗標。
    private bool ConfirmOverclockRisk()
    {
        var settings = OcSettings.Load();
        if (settings.DontShowRisk) return true;   // 先前已選「今後不再顯示」，直接放行

        var dlg = new OcRiskWindow { Owner = this };
        dlg.ShowDialog();
        if (dlg.Proceed && dlg.DontShowAgain)
        {
            settings.DontShowRisk = true;
            try { settings.Save(); } catch { /* 旗標持久化失敗不影響本次進入 */ }
        }
        return dlg.Proceed;
    }

    private void Export_Click(object sender, RoutedEventArgs e) => _vm.ExportReport();

    /// <summary>切換至獨立的 AI 評價分頁（供設定頁 / 總覽的「開啟 AI 助手」按鈕呼叫）。</summary>
    public void NavigateToAi()
    {
        int i = Array.FindIndex(_views, v => v is AiView);
        if (i >= 0) Nav.SelectedIndex = i;
    }

    // ===== 迷你浮動監視器 + 系統匣 =====
    private void Mini_Click(object sender, RoutedEventArgs e) => ToggleMini();

    private void InitTray()
    {
        try
        {
            _tray = new TrayService();
            _tray.ShowMainRequested += ShowMainFromTray;
            _tray.ToggleMiniRequested += ToggleMini;
            _tray.ExitRequested += () => Application.Current.Shutdown();
            _vm.Alerts.Balloon = (t, m) => _tray?.ShowBalloon(t, m);   // 硬體警示→系統匣氣泡
        }
        catch { /* 系統匣為附加功能，失敗不影響主程式 */ }
    }

    private void ShowMainFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ToggleMini()
    {
        _mini ??= new MiniOverlayWindow { DataContext = _vm };
        _mini.Toggle();
    }

    // ===== 固定外觀（強調色輝光一次套用；主題固定深色） =====
    private void ApplyAccentGlow()
    {
        if (AccentGlow is not null) AccentGlow.Color = ThemeService.Accent.MainColor;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBar(ThemeService.Theme == AppTheme.Dark);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Stop();
        _tray?.Dispose();
        if (_mini is not null) { _mini.Close(); _mini = null; }
        _vm.Live?.Dispose();
        base.OnClosed(e);
    }

    // ===== 標題列深/淺色（Windows 10 1809+ / 11） =====
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyTitleBar(bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int useDark = dark ? 1 : 0;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（較新版本）；19 = 舊版 build 的相同屬性
            if (DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
        }
        catch { /* 非關鍵功能，失敗則沿用系統預設標題列 */ }
    }
}
