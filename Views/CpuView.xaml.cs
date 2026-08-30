using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace XinSpect;

public partial class CpuView : UserControl
{
    private readonly DispatcherTimer _rdtTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public CpuView()
    {
        InitializeComponent();
        // RDT 的讀值由背景執行緒每秒更新到快照，須在 UI 執行緒發佈成繫結列。
        _rdtTimer.Tick += (_, _) => Vm?.Rdt.Tick();
        Loaded += (_, _) => { if (Vm?.Rdt.IsRunning == true) _rdtTimer.Start(); };
        Unloaded += (_, _) => _rdtTimer.Stop();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ===== 核心到核心延遲 =====
    private void CoreLatencyStart_Click(object sender, RoutedEventArgs e) => Vm?.CoreLatency.Start();
    private void CoreLatencyStop_Click(object sender, RoutedEventArgs e) => Vm?.CoreLatency.Cancel();

    // ===== Intel RDT 監測 =====
    private void RdtStart_Click(object sender, RoutedEventArgs e)
    {
        Vm?.Rdt.Start();
        if (Vm?.Rdt.IsRunning == true) _rdtTimer.Start();
    }

    private void RdtStop_Click(object sender, RoutedEventArgs e)
    {
        _rdtTimer.Stop();
        Vm?.Rdt.Stop();
    }

    // ===== 頻率真相 =====
    private void FreqTruthRead_Click(object sender, RoutedEventArgs e) => Vm?.FreqTruth.Start();

    // ===== 黏滯節流位元 =====
    private void StickyRead_Click(object sender, RoutedEventArgs e) => Vm?.ThermalSticky.Refresh();

    // ===== 安全緩解狀態 =====
    private void SecurityRead_Click(object sender, RoutedEventArgs e) => Vm?.CpuSecurity.Refresh();
}
