using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>一鍵裝機分頁：勾選常用軟體，透過 Windows 內建 winget 批次安裝。</summary>
public partial class SetupView : UserControl
{
    public SetupView() => InitializeComponent();

    private WingetService? Vm =>
        (DataContext as WingetService)
        ?? Shell.Vm?.Winget;

    private void SelectRec_Click(object sender, RoutedEventArgs e) => Vm?.SelectRecommended();

    private void Clear_Click(object sender, RoutedEventArgs e) => Vm?.ClearSelection();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.InstallSelectedAsync();
    }

    // 開啟 Microsoft Store 的「應用程式安裝程式」（winget）頁面
    private void GetWinget_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://apps.microsoft.com/detail/9nblggh4nns1") { UseShellExecute = true });
        }
        catch { /* 開啟商店頁面失敗不影響其餘功能 */ }
    }
}
