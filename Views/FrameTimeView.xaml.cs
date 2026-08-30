using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>幀時間監測頁：ETW 訂閱 DXGI Present；每秒叫一次 Tick 更新行程清單與統計。</summary>
public partial class FrameTimeView : UserControl
{
    private readonly DispatcherTimer _timer;

    public FrameTimeView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Vm?.FrameTime.Tick();
        _timer.Start();
    }

    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;

    private void Start_Click(object sender, RoutedEventArgs e) => Vm?.FrameTime.Start();
    private void Stop_Click(object sender, RoutedEventArgs e) => Vm?.FrameTime.Stop();

    private void ProcPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcPicker.SelectedItem is FrameProcessRow row && Vm is not null)
            Vm.FrameTime.SelectedPid = row.Pid.ToString();
    }
}
