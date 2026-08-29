using System.Windows;
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
        Loaded += (_, _) => Restore();
    }

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

    /// <summary>切換顯示 / 隱藏（顯示時回到上次擺放的位置）。</summary>
    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        Show();
        Restore();
        Activate();
    }
}
