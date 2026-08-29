using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>主機板分頁：主機板 / BIOS / 晶片組 / LPCIO 深度規格（CPU-Z 報告 + WMI 廠商）。</summary>
public partial class MotherboardView : UserControl
{
    public MotherboardView() => InitializeComponent();

    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Application.Current?.MainWindow?.DataContext as MainViewModel;

    private void FirmwareRefresh_Click(object sender, RoutedEventArgs e) => Vm?.Firmware.Refresh();
}
