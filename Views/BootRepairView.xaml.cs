using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>系統引導修復頁：按鈕觸發各修復命令，紀錄顯示於下方 ListBox。</summary>
public partial class BootRepairView : UserControl
{
    private readonly BootRepairService _svc = new();

    public BootRepairView()
    {
        InitializeComponent();
        _svc.SetDispatcher(Dispatcher);
        LogList.ItemsSource = _svc.Log;
        _svc.Log.CollectionChanged += (_, _) =>
        {
            if (_svc.Log.Count > 0)
                LogList.ScrollIntoView(_svc.Log[^1]);
        };
        Loaded += (_, _) =>
        {
            if (!_svc.IsAdmin)
                AdminWarning.Visibility = Visibility.Visible;
        };
    }

    private async void Sfc_Click(object sender, RoutedEventArgs e) => await _svc.RunSfcAsync();
    private async void Dism_Click(object sender, RoutedEventArgs e) => await _svc.RunDismAsync();
    private async void Chkdsk_Click(object sender, RoutedEventArgs e) => await _svc.RunChkdskAsync();
    private async void BootRec_Click(object sender, RoutedEventArgs e) => await _svc.RunBootRecAsync();
    private async void Bcd_Click(object sender, RoutedEventArgs e) => await _svc.RepairBcdAsync();
    private async void All_Click(object sender, RoutedEventArgs e) => await _svc.RunAllAsync();
    private void Clear_Click(object sender, RoutedEventArgs e) => _svc.ClearLog();
}
