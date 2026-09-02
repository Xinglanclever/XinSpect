using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;

namespace XinSpect;

/// <summary>
/// 主視窗（外殼）：依 <see cref="PageRegistry"/> 建構側邊欄、延遲實體化各頁、轉交頁面生命週期，
/// 並承載命令面板、系統匣與迷你浮動監視器。
/// </summary>
/// <remarks>
/// 2.0 相對 1.x 的三項結構改變：
/// (1) 側邊欄改為資料繫結註冊表，不再有「XAML 手寫項目 ↔ _views[] 索引」的平行對應；
/// (2) 檢視於首次進入才建立，啟動時不再一次 new 出全部分頁；
/// (3) 「哪些頁面顯示時該做昂貴工作」由 <see cref="PageDef.LiveGate"/> 宣告，外殼不再硬寫型別判斷。
/// </remarks>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly Dictionary<string, UserControl> _cache = new(StringComparer.OrdinalIgnoreCase);
    private UserControl? _current;
    private PageDef? _currentDef;
    private TrayService? _tray;
    private MiniOverlayWindow? _mini;
    private bool _ocRiskCleared;   // 本次執行是否已通過超頻風險兩階段確認
    private bool _initialized;     // Loaded 可能多次觸發（隱藏至系統匣後再顯示等）；確保只初始化一次

    public MainWindow()
    {
        ThemeService.Initialize();     // 解析 XAML 前套用已存主題/強調色，避免首格閃色
        InitializeComponent();
        DataContext = _vm;
        Motion.Attach(_vm.Settings);   // 動態效果總開關跟著設定走（關掉後所有繪圖控制項停下計時器）

        BuildNav();
        ApplyAccentGlow();

        // 感測引擎於背景載入完成後，重放當前頁的感測閘門（引擎晚到時閘門才有對象可套用）
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Live)) ApplyLiveGates();
        };

        // 主題／強調色變更（設定頁）後重新套用標題輝光
        ThemeService.Changed += ApplyAccentGlow;

        // 簡易模式切換後重建側邊欄（設定頁改的是同一份 SettingsService）
        _vm.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SettingsService.SimpleMode)) return;
            var keep = _currentDef;
            BuildNav();
            if (keep is not null) Nav.SelectedItem = keep;   // 別把使用者正在看的頁面切掉
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

    // ===== 導覽 =====

    // 以註冊表建立側邊欄項目來源，並依 Group 分組（順序即註冊順序）。
    private void BuildNav()
    {
        // 簡易模式下把進階頁從側邊欄收起來（命令面板照樣搜得到，只是不列在這裡）。
        // 目前頁若正好被收起來，保留它——把使用者正在看的東西抽掉比多一個項目更糟。
        bool simple = _vm.Settings.SimpleMode;
        var pages = PageRegistry.Pages
            .Where(p => !simple || !p.Advanced || ReferenceEquals(p, _currentDef))
            .ToList();

        var src = new CollectionViewSource { Source = pages };
        src.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PageDef.Group)));
        Nav.ItemsSource = src.View;
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not PageDef def) return;

        // 超頻類分頁：進入前的兩階段風險確認。本次執行僅需通過一次；選「今後不再顯示」則永久略過。
        if (def.RequiresRiskConsent && !_ocRiskCleared)
        {
            if (!ConfirmOverclockRisk())
            {
                // 使用者放棄進入：還原到先前分頁（若無則回第一頁）
                Nav.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : PageRegistry.Pages[0];
                return;
            }
            _ocRiskCleared = true;
        }

        ShowPage(def);
    }

    // 切頁：延遲建立檢視 → 轉交生命週期 → 重放感測閘門 → 播放進場動畫。
    private void ShowPage(PageDef def)
    {
        if (!_cache.TryGetValue(def.Key, out var view))
        {
            try
            {
                view = def.Factory();
                _cache[def.Key] = view;
            }
            catch (Exception ex)
            {
                // 單一頁面建構失敗（缺少執行階段元件等）不得讓整個外殼倒下
                _vm.StatusText = $"「{def.Title}」頁面載入失敗：{ex.Message}";
                return;
            }
        }

        if (!ReferenceEquals(_current, view))
        {
            (_current as IPageLifecycle)?.OnDeactivated();
            _current = view;
            _currentDef = def;
            Host.Content = view;
            (view as IPageLifecycle)?.OnActivated();
            PageTransition.PlayEnter(Host);   // 切頁淡入 + 輕微上滑
        }
        else
        {
            _currentDef = def;
        }

        ApplyLiveGates();
    }

    // 對所有頁面統一重放感測閘門：只有當前頁得到 true。
    // 宣告式且可重入，故感測引擎晚到時只要再呼叫一次即可，不需任何型別判斷。
    private void ApplyLiveGates()
    {
        var live = _vm.Live;
        if (live is null) return;
        foreach (var d in PageRegistry.Pages)
            d.LiveGate?.Invoke(live, ReferenceEquals(d, _currentDef));
    }

    /// <summary>切換至指定鍵值的分頁（命令面板與跨頁按鈕皆走此路）。</summary>
    public void NavigateToKey(string key)
    {
        var def = PageRegistry.Find(key);
        if (def is not null) Nav.SelectedItem = def;
    }

    /// <summary>切換至 AI 分頁（供設定頁 / 總覽的「開啟 AI 助手」按鈕呼叫）。</summary>
    public void NavigateToAi() => NavigateToKey("ai");

    /// <summary>切換至內建瀏覽器分頁並前往指定網址（供網速測試的節點連結等呼叫）。
    /// returnUtilityKey 非空時，瀏覽器會顯示「回到測速頁面」一類的返回鈕。</summary>
    public void NavigateToBrowser(string url, string? returnUtilityKey = null)
    {
        NavigateToKey("browser");
        if (_current is BrowserView b)
        {
            b.SetReturnUtility(returnUtilityKey);
            b.NavigateTo(url);   // 未就緒時 NavigateTo 會暫存待就緒補跳
        }
    }

    /// <summary>切換至「實用工具」並選定其中一個子工具（供命令面板深層跳轉）。</summary>
    public void NavigateToUtility(string toolKey)
    {
        NavigateToKey("utilities");
        if (_current is UtilitiesView u) u.SelectTool(toolKey);
    }

    /// <summary>切換至歷史回放，並把時間窗對到指定時刻（供事件時間軸點擊某筆事件呼叫）。</summary>
    public void NavigateToHistory(DateTime utc)
    {
        NavigateToKey("history");
        if (_current is HistoryView h) h.JumpTo(utc);
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

    // ===== 命令面板 =====

    private void Palette_Click(object sender, RoutedEventArgs e) => OpenPalette();

    private void OpenPalette() => Palette.Open(PaletteCatalog.Build(this, _vm));

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Palette.IsOpen)
        {
            if (Palette.HandleKey(e.Key)) e.Handled = true;
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        // Ctrl+K 為主，Ctrl+P 沿用編輯器慣例一併接受
        if (ctrl && (e.Key == Key.K || e.Key == Key.P))
        {
            OpenPalette();
            e.Handled = true;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e) => _vm.ExportReport();

    // ===== 迷你浮動監視器 + 系統匣 =====
    private void Mini_Click(object sender, RoutedEventArgs e) => ToggleMini();

    private void InitTray()
    {
        try
        {
            _tray = new TrayService();
            _tray.ShowMainRequested += RestoreToForeground;
            _tray.ToggleMiniRequested += ToggleMini;
            _tray.ExitRequested += () => Application.Current.Shutdown();
            _vm.Alerts.Balloon = (t, m) => _tray?.ShowBalloon(t, m);   // 硬體警示→系統匣氣泡
        }
        catch { /* 系統匣為附加功能，失敗不影響主程式 */ }
    }

    /// <summary>把主視窗帶回畫面：系統匣圖示與「使用者又點了一次曦覽」都走這條路。</summary>
    public void RestoreToForeground()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>切換迷你浮動監視器（供命令面板呼叫）。</summary>
    public void ToggleMini()
    {
        _mini ??= new MiniOverlayWindow { DataContext = _vm };
        _mini.Toggle();
    }

    // ===== 外觀 =====
    private void ApplyAccentGlow()
    {
        if (AccentGlow is not null) AccentGlow.Color = ThemeService.Accent.MainColor;
        ApplyTitleBar(ThemeService.Theme == AppTheme.Dark);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBar(ThemeService.Theme == AppTheme.Dark);
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeService.Changed -= ApplyAccentGlow;
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
