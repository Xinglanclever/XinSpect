using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// PCIe 鏈路實況頁。第一次進頁即自動掃一次（唯讀，一個位元都不寫）；之後可按「重新掃描」重讀。
/// </summary>
public partial class PcieLinkView : UserControl
{
    public PcieLinkView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Vm?.PcieLink.EnsureLoaded();
            Vm?.ResizableBar.EnsureLoaded();
        };
    }

    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm?.PcieLink.Refresh();

    private void RefreshBar_Click(object sender, RoutedEventArgs e) => Vm?.ResizableBar.Refresh();
}
