using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>PCIe 鏈路實況頁：使用者按下「重新掃描」才去讀 PCI 設定空間（唯讀）。</summary>
public partial class PcieLinkView : UserControl
{
    public PcieLinkView() => InitializeComponent();

    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm?.PcieLink.Refresh();
}
