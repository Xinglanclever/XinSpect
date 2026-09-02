using System.Windows.Controls;

namespace XinSpect;

/// <summary>睡眠與喚醒診斷頁：五條 powercfg 唯讀查詢，第一次進頁自動跑一次。</summary>
public partial class SleepDiagnosticsView : UserControl
{
    public SleepDiagnosticsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Vm?.Sleep.EnsureLoaded();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void Refresh_Click(object sender, System.Windows.RoutedEventArgs e) => Vm?.Sleep.Refresh();
}
