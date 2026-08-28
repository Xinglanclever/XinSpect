using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// GPU 燒機測試分頁：自持 <see cref="GpuStressService"/>。以第三方 FurMark 施加負載，
/// 配 XinSpect 原生即時監測與智能熱保護。僅在本頁顯示時輪詢感測器（省資源）。
/// </summary>
public partial class GpuStressView : UserControl
{
    private readonly GpuStressService _svc = new();

    public GpuStressView()
    {
        InitializeComponent();
        DataContext = _svc;
    }

    // 本頁顯示時啟動監測輪詢，離開時停止（燒機進行中則由服務自行維持）
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) _svc.StartMonitor();
        else _svc.StopMonitor();
    }

    private async void Install_Click(object sender, RoutedEventArgs e) => await _svc.InstallAsync();

    private void Redetect_Click(object sender, RoutedEventArgs e) => _svc.Redetect();

    private void Official_Click(object sender, RoutedEventArgs e) => _svc.OpenOfficialPage();

    private void Gui_Click(object sender, RoutedEventArgs e) => _svc.OpenGui();

    private void Native_Click(object sender, RoutedEventArgs e) => _svc.UseNativeResolution();

    private async void Smart_Click(object sender, RoutedEventArgs e) => await _svc.SmartStartAsync();

    private void Dur_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string s && int.TryParse(s, out int m))
            _svc.DurationMinutes = m;
    }

    private void Manual_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "指定 FurMark 執行檔",
            Filter = "FurMark 執行檔|furmark.exe;FurMark_GUI.exe|所有執行檔 (*.exe)|*.exe",
        };
        if (!string.IsNullOrEmpty(_svc.FurMarkPath) && File.Exists(_svc.FurMarkPath))
            dlg.InitialDirectory = Path.GetDirectoryName(_svc.FurMarkPath);
        if (dlg.ShowDialog() == true)
            _svc.SetManualPath(dlg.FileName);
    }
}
