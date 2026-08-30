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

    private void BiosMeRefresh_Click(object sender, RoutedEventArgs e) => Vm?.BiosMe.Refresh();

    /// <summary>
    /// 重開機進入 UEFI 設定：勾選風險確認之外，再做一次明確的對話框確認。
    /// 這個動作會立刻重開機，未存檔的工作會消失——不該只靠一個核取方塊就執行。
    /// </summary>
    private void RebootToFirmware_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var answer = MessageBox.Show(
            "即將立刻重新開機並直接進入主機板的 UEFI／BIOS 設定畫面。\n\n"
            + "・所有未存檔的工作都會遺失。\n"
            + "・此動作需要系統管理員權限，會彈出提升視窗。\n"
            + "・曦覽不會替你修改任何 BIOS 設定；設定一律在主機板自己的介面裡改。\n\n"
            + "確定要現在重開機嗎？",
            "確認重開機進入 UEFI 設定", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) Vm.BiosMe.RebootToFirmwareSetup();
    }

    private void OpenVendorBios_Click(object sender, RoutedEventArgs e) => Vm?.BiosMe.OpenVendorBiosPage();

    private void OpenMeTool_Click(object sender, RoutedEventArgs e) => Vm?.BiosMe.OpenIntelMeToolPage();
}
