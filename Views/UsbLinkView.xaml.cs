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
}
