using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace XinSpect;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        // 進頁面就重新評估一次網路狀態：使用者可能是離線時開的程式、後來才接上網路。
        Loaded += (_, _) => Vm?.Feedback.Refresh();
    }

    // 頁面內容由父容器延遲載入，DataContext 為繼承而來；仍以主視窗為後備。
    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    // ── 留言建議 ────────────────────────────────────────────────────────────

    private void FeedbackRefresh_Click(object sender, RoutedEventArgs e) => Vm?.Feedback.Refresh();

    private async void SendFeedback_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.Feedback.SendAsync(AppInfo.Version);
    }

    // 以系統預設瀏覽器開啟外部連結（YouTube 頻道）
    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* 無可用瀏覽器時靜默略過 */ }
        e.Handled = true;
    }

    // 已開啟的徽章一覽視窗（避免重複開啟；已開則帶到前景）
    private IconGalleryWindow? _gallery;

    private void OpenGallery_Click(object sender, RoutedEventArgs e)
    {
        if (_gallery is { IsLoaded: true })
        {
            if (_gallery.WindowState == WindowState.Minimized)
                _gallery.WindowState = WindowState.Normal;
            _gallery.Activate();
            return;
        }
        _gallery = new IconGalleryWindow { Owner = Window.GetWindow(this) };
        _gallery.Closed += (_, _) => _gallery = null;
        _gallery.Show();
    }
}
