using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 連接埠占用分頁：自持一個 PortUsageService（與主 ViewModel 無關），
/// 首次載入時抓取連線表，並提供搜尋／只顯示監聽／自動更新／結束占用行程。
/// </summary>
public partial class PortUsageView : UserControl
{
    private readonly PortUsageService _svc = new();
    private ICollectionView? _view;
    private readonly DispatcherTimer _timer;
    private bool _loaded;

    public PortUsageView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Reload();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _view = CollectionViewSource.GetDefaultView(_svc.Rows);
        _view.Filter = RowFilter;
        Grid.ItemsSource = _view;
        _svc.PropertyChanged += Svc_PropertyChanged;
        Reload();
    }

    private void Svc_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PortUsageService.Status))
            Dispatcher.Invoke(() => StatusText.Text = _svc.Status);
    }

    private void Reload()
    {
        _svc.Refresh();
        _view?.Refresh();
    }

    // 搜尋（埠／行程／位址）＋ 只顯示監聽埠。
    private bool RowFilter(object o)
    {
        if (o is not PortRow r) return false;
        if (ListenOnly.IsChecked == true && r.State != "監聽中") return false;
        string q = SearchBox.Text.Trim();
        if (q.Length == 0) return true;
        return r.LocalPort.ToString().Contains(q)
            || r.RemotePort.ToString().Contains(q)
            || r.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.LocalAddress.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.RemoteAddress.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Protocol.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Filter_Changed(object sender, RoutedEventArgs e) => _view?.Refresh();
    private void Filter_Changed(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefresh.IsChecked == true) _timer.Start();
        else _timer.Stop();
    }

    // 結束占用連接埠的行程；先確認，避免誤殺。
    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PortRow row }) return;
        if (row.Pid <= 0)
        {
            MessageBox.Show("此連線由系統核心持有，無法結束。", "連接埠占用",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ok = MessageBox.Show(
            $"確定要結束占用連接埠 {row.LocalPort} 的行程？\n\n行程：{row.ProcessText}（PID {row.Pid}）\n\n" +
            "將一併結束其子行程，未存檔的資料可能遺失。",
            "結束行程", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        var (success, message) = _svc.KillProcess(row.Pid);
        MessageBox.Show(message, "連接埠占用", MessageBoxButton.OK,
            success ? MessageBoxImage.Information : MessageBoxImage.Error);
        if (success) Reload();
    }
}
