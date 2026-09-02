using System.Windows.Controls;

namespace XinSpect;

/// <summary>網卡進階屬性頁：唯讀 WMI 查詢，第一次進頁自動讀一次。</summary>
public partial class NetAdapterView : UserControl
{
    public NetAdapterView()
    {
        InitializeComponent();
        Loaded += (_, _) => Vm?.NetAdapter.EnsureLoaded();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void Refresh_Click(object sender, System.Windows.RoutedEventArgs e) => Vm?.NetAdapter.Refresh();
}
