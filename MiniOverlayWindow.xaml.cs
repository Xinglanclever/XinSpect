using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace XinSpect;

/// <summary>
/// 迷你浮動監視器：無邊框、置頂、半透明、可拖曳的即時精簡面板。
/// 可由主視窗「迷你」鈕、系統匣選單或命令面板切換顯示／隱藏。
/// </summary>
/// <remarks>
/// 位置、不透明度、精簡模式與是否置頂都存進 <see cref="SettingsService"/>：
/// 這個視窗會被反覆隱藏再顯示，若每次重新貼齊右上角，使用者拖過去的位置就白拖了。
/// </remarks>
public partial class MiniOverlayWindow : Window
{
    private SettingsService? Cfg => (DataContext as MainViewModel)?.Settings;

    public MiniOverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Activated += (_, _) => ReassertTopmost();
        // 獨立視窗不在主視覺樹的「逐頁轉換」範圍：載入時轉一次、語言切換時即時跟進。
        LanguageService.Changed += OnLanguageChanged;
        Closed += (_, _) => LanguageService.Changed -= OnLanguageChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LanguageService.ConvertVisualTree(this, LanguageService.IsSimplified);
        Restore();
    }

    private void OnLanguageChanged() => LanguageService.ConvertVisualTree(this, LanguageService.IsSimplified);

    /// <summary>擺到上次的位置；沒有記錄（或記錄已落在畫面外）時貼齊工作區右上角。</summary>
    private void Restore()
    {
        var wa = SystemParameters.WorkArea;
        double? l = Cfg?.MiniLeft, t = Cfg?.MiniTop;

        // 標題列至少要留一截在畫面內，否則使用者再也拖不回來（例如拔掉了那台螢幕）
        bool usable = l is double x && t is double y
            && x > wa.Left - ActualWidth + 60 && x < wa.Right - 60
            && y > wa.Top - 4 && y < wa.Bottom - 40;

        if (usable)
        {
            Left = l!.Value;
            Top = t!.Value;
        }
        else
        {
            Left = wa.Right - ActualWidth - 24;
            Top = wa.Top + 24;
        }
    }

    private void Remember()
    {
        if (Cfg is null) return;
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;
        Cfg.MiniLeft = Left;
        Cfg.MiniTop = Top;
    }

    /// <summary>
    /// 拖曳整個面板。<see cref="Window.DragMove"/> 會一路阻塞到放開滑鼠，
    /// 所以緊接著記位置＝「拖完才存一次」，不會拖一格寫一次檔。
    /// </summary>
    private void Drag(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { /* 非拖曳狀態呼叫時忽略 */ }
        Remember();
    }

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        if (Cfg is null) return;
        Cfg.MiniCompact = !Cfg.MiniCompact;
    }

    /// <remarks>
    /// 只改設定、不直接寫 <see cref="Window.Topmost"/>：那會就地覆蓋掉 XAML 的
    /// OneWay 綁定，之後從設定頁改「釘選」就再也推不動這個視窗了。
    /// 設定屬性本身會發出變更通知，綁定同一輪就跟上。
    /// </remarks>
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (Cfg is null) return;
        Cfg.MiniTopmost = !Cfg.MiniTopmost;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>
    /// 開齒輪選單。ContextMenu 不在視窗視覺樹內，收不到 DataContext，
    /// 開啟時手動掛上，選單內的開關綁定才找得到 Settings。
    /// </summary>
    private void Gear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement btn) return;
        var menu = new System.Windows.Controls.ContextMenu { DataContext = DataContext };
        System.Windows.Controls.MenuItem AddItem(string label, string path)
        {
            var mi = new System.Windows.Controls.MenuItem
            {
                Header = label,
                IsCheckable = true,
                StaysOpenOnClick = true,
            };
            mi.SetBinding(System.Windows.Controls.MenuItem.IsCheckedProperty,
                new System.Windows.Data.Binding(path) { Mode = System.Windows.Data.BindingMode.TwoWay });
            menu.Items.Add(mi);
            return mi;
        }
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            // ContextMenu 不在視覺樹上，標題一律在產出點過 T。
            Header = LanguageService.T("顯示項目（FPS 三行讀「幀時間監測」頁的即時量測）"),
            IsEnabled = false,
        });
        AddItem("CPU", "Settings.MiniShowCpu");
        AddItem("GPU", "Settings.MiniShowGpu");
        AddItem(LanguageService.T("記憶體"), "Settings.MiniShowMem");
        AddItem(LanguageService.T("頻率"), "Settings.MiniShowClock");
        AddItem("FPS", "Settings.MiniShowFps");
        AddItem("1% Low", "Settings.MiniShowLow1");
        AddItem("0.1% Low", "Settings.MiniShowLow01");
        menu.PlacementTarget = btn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>切換顯示 / 隱藏（顯示時回到上次擺放的位置）。</summary>
    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        Show();
        Restore();
        Activate();
    }

    /// <summary>
    /// 全螢幕遊戲或系統動畫偶爾會把置頂吃掉；視窗每次被啟動時重申一次置頂意圖。
    /// 走綁定的 UpdateTarget 而非直接寫 Topmost，避免覆蓋掉 XAML 的 OneWay 綁定。
    /// </summary>
    private void ReassertTopmost()
    {
        Dispatcher.InvokeAsync(() =>
            BindingOperations.GetBindingExpression(this, Window.TopmostProperty)?.UpdateTarget());
    }
}
