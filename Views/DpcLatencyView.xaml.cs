using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>DPC／ISR 延遲頁：ETW 核心追蹤，量測時長由單選鈕決定。</summary>
public partial class DpcLatencyView : UserControl
{
    public DpcLatencyView() => InitializeComponent();

    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;

    private void Dur_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && int.TryParse(rb.Tag as string, out int sec) && Vm is not null)
            Vm.DpcLatency.DurationSec = sec;
    }

    private void Start_Click(object sender, RoutedEventArgs e) => Vm?.DpcLatency.Start();
    private void Stop_Click(object sender, RoutedEventArgs e) => Vm?.DpcLatency.Stop();
}
