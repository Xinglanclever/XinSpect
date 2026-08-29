using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

public partial class CpuView : UserControl
{
    public CpuView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ===== 核心到核心延遲 =====
    private void CoreLatencyStart_Click(object sender, RoutedEventArgs e) => Vm?.CoreLatency.Start();
    private void CoreLatencyStop_Click(object sender, RoutedEventArgs e) => Vm?.CoreLatency.Cancel();

    // ===== 黏滯節流位元 =====
    private void StickyRead_Click(object sender, RoutedEventArgs e) => Vm?.ThermalSticky.Refresh();

    // ===== 安全緩解狀態 =====
    private void SecurityRead_Click(object sender, RoutedEventArgs e) => Vm?.CpuSecurity.Refresh();
}
