using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>記憶體分頁：顯示 CPU-Z 等級的時序、通道、實體模組與 SPD 詳細資訊，並提供圖樣檢測。
/// 資料由 MainViewModel（DataContext 繼承自主視窗）提供，本身僅載入版面。</summary>
public partial class MemoryView : UserControl
{
    // 認可數值會隨時間變動，每兩秒重讀一次（GetPerformanceInfo 極輕量）。
    private readonly DispatcherTimer _truthTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MemoryView()
    {
        InitializeComponent();
        _truthTimer.Tick += (_, _) => Vm?.MemoryTruth.Refresh();
        Loaded += (_, _) => { Vm?.MemoryTruth.Refresh(); _truthTimer.Start(); };
        Unloaded += (_, _) => _truthTimer.Stop();
    }

    // Host.Content 延遲載入時 DataContext 由父容器繼承；仍以主視窗為後備。
    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;

    private void MemTestStart_Click(object sender, RoutedEventArgs e) => Vm?.MemTest.Start();
    private void MemTestStop_Click(object sender, RoutedEventArgs e) => Vm?.MemTest.Cancel();
}
