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
}
