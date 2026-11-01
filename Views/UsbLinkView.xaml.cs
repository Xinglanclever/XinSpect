using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>USB 鏈路真相頁：使用者按下「重新掃描」才去問集線器（唯讀 IOCTL）。</summary>
public partial class UsbLinkView : UserControl
{
    public UsbLinkView() => InitializeComponent();

    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm?.UsbLink.Refresh();

    private void TbUsb4Refresh_Click(object sender, RoutedEventArgs e)
        => _ = Task.Run(() =>
        {
            // TbUsb4Service.Read() 走 WMI（Thunderbolt／USB4 控制器類別）。整機 WMI 卡住時，
            // 原本這一行會把 UI 執行緒一起押在那裡；改成背景＋上界，回到 UI 才設 ItemsSource。
            var (rows, timedOut) = WmiGuard.Probe(TbUsb4Service.Read);
            Shell.BeginOnUi(() =>
            {
                TbUsb4Rows.ItemsSource = rows;
                TbUsb4Status.Text = rows is null
                    ? (timedOut ? "WMI 無回應，稍後再按「重新掃描」" : "讀取失敗")
                    : rows.Count == 0 ? "未找到 Thunderbolt 或 USB4 控制器" : $"已列舉 {rows.Count} 個控制器";
            });
        });
}