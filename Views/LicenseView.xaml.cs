using System.Windows.Controls;

namespace XinSpect;

/// <summary>Windows 授權頁：唯讀查詢，第一次進頁自動讀一次。金鑰預設遮蔽。</summary>
public partial class LicenseView : UserControl
{
    public LicenseView()
    {
        InitializeComponent();
        Loaded += (_, _) => Vm?.License.EnsureLoaded();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void Refresh_Click(object sender, System.Windows.RoutedEventArgs e) => Vm?.License.Refresh();

    private void Reveal_Click(object sender, System.Windows.RoutedEventArgs e) => Vm?.License.ToggleReveal();
}
