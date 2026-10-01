using System.Windows;
using System.Windows.Controls;
namespace XinSpect;
public partial class NetworkView : UserControl { public NetworkView() => InitializeComponent();
    private void WifiRefresh_Click(object sender, RoutedEventArgs e)
    {
        var rows = WifiSignalService.Read();
        WifiRows.ItemsSource = rows.Select(r => new
        {
            r.InterfaceName, r.Ssid, r.LinkSpeed, r.Auth,
            RssiText = $"{r.Rssi} dBm",
            QualityText = WifiSignalService.Interpret(r.Rssi),
        }).ToList();
        WifiStatus.Text = rows.Count == 0 ? "未找到無線網路介面或 API 失敗" : $"已讀取 {rows.Count} 個 Wi-Fi 介面";
    }
}
