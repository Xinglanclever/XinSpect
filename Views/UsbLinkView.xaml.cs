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
    {
        var rows = TbUsb4Service.Read();
        TbUsb4Rows.ItemsSource = rows;
        TbUsb4Status.Text = rows.Count == 0 ? "未找到 Thunderbolt 或 USB4 控制器" : $"已列舉 {rows.Count} 個控制器";
    }
}