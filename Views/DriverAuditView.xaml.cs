using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>驅動程式稽核頁：使用者按下「重新掃描」才去查 WMI 驅動存放區（唯讀）。</summary>
public partial class DriverAuditView : UserControl
{
    private DriverStoreWindow? _driverStoreWindow;

    public DriverAuditView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    private void OpenDriverStore_Click(object sender, RoutedEventArgs e)
    {
        if (_driverStoreWindow is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new DriverStoreWindow { Owner = Window.GetWindow(this) };
        window.DataContext ??= DataContext ?? Vm;
        window.Loaded += (_, _) => LanguageService.ConvertVisualTree(window, LanguageService.IsSimplified);
        window.Closed += (_, _) => _driverStoreWindow = null;
        _driverStoreWindow = window;
        window.Show();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm?.DriverAudit.Refresh();
}
