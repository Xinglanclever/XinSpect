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
            RssiText = r.Connected ? $"{r.Rssi} dBm" : "—",
            ChannelText = r.Channel > 0 ? r.Channel.ToString() : "—",
            QualityText = r.Connected ? WifiSignalService.Interpret(r.Rssi) : "未連線",
        }).ToList();
        int connected = rows.Count(r => r.Connected);
        WifiStatus.Text = rows.Count == 0
            ? "未找到無線網路介面或 API 失敗"
            : connected == 0
                ? $"偵測到 {rows.Count} 個 Wi-Fi 介面，均未連線——連線後 RSSI／頻道／速率才可讀。"
                : $"已讀取 {rows.Count} 個 Wi-Fi 介面（{connected} 個連線中）。";
    }
}
