using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 菜鳥儀表板：簡易模式首頁，把「這台電腦是什麼、健不健康、能做什麼」濃縮成一頁。
/// 所有資料繫結到 MainViewModel 的既有屬性，不建立新的服務或硬體存取。
/// </summary>
public partial class BeginnerDashboardView : UserControl
{
    public BeginnerDashboardView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    private void Cleanup_Click(object sender, RoutedEventArgs e)
        => Shell.Main?.NavigateToUtility("cleanup");

    private void MemClean_Click(object sender, RoutedEventArgs e)
        => Shell.Main?.NavigateToUtility("memclean");

    private void Export_Click(object sender, RoutedEventArgs e)
        => Vm?.ExportReport();

    private void Troubleshoot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string scenario) return;
        var main = Shell.Main;
        if (main is null) return;

        string target = scenario switch
        {
            "slow" => "bottleneck",
            "game" => "bottleneck",
            "bsod" => "bsod",
            "fan" => "fan",
            "boot" => "sleep",
            "net" => "network",
            _ => "health",
        };

        // 故障排查需要進階頁，先暫時切到進階模式
        if (Vm is { } vm && vm.Settings.SimpleMode)
            vm.Settings.SimpleMode = false;

        if (target is "bsod" or "fan" or "sleep")
            main.NavigateToUtility(target);
        else
            main.NavigateToKey(target);
    }
}
