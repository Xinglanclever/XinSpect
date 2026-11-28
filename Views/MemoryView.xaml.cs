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
        _truthTimer.Tick += (_, _) => { Vm?.MemoryTruth.Refresh(); RefreshController(); };
        Loaded += (_, _) => { Vm?.MemoryTruth.Refresh(); Vm?.LargePage.EnsureChecked(); RefreshController(); _truthTimer.Start(); };
        DataContextChanged += (_, _) => RefreshController();
        RefreshController();
        Unloaded += (_, _) => _truthTimer.Stop();
    }

    private void RefreshController()
    {
        var vm = Vm;
        var timingSnapshot = vm?.Timings.Controller;
        if (timingSnapshot is null || ReferenceEquals(timingSnapshot, MemoryControllerSnapshot.Empty))
            timingSnapshot = MemoryControllerService.FromTimings(vm?.Timings, null, null);
        ControllerPanel.DataContext = MemoryControllerService.Merge(vm?.Live?.ControllerSnapshot, timingSnapshot);
    }

    // Host.Content 延遲載入時 DataContext 由父容器繼承；仍以主視窗為後備。
    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;

    private async void ReinitializeAll_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        try { await vm.ReinitializeAllAsync(); }
        catch (Exception ex) { vm.StatusText = "重新擷取失敗：" + ex.Message; }
    }

    /// <summary>DRAM 流量量測：使用者按下才跑（會編程效能計數器並在結束後還原）。</summary>
    private void DramTrafficMeasure_Click(object sender, RoutedEventArgs e) => Vm?.DramTraffic.Measure();

    /// <summary>大頁量測：使用者按下才跑（會配置 256 MB 並在結束後釋放）。</summary>
    private void LargePageMeasure_Click(object sender, RoutedEventArgs e) => Vm?.LargePage.Measure();

    private void MemTestStart_Click(object sender, RoutedEventArgs e) => Vm?.MemTest.Start();
    private void MemTestStop_Click(object sender, RoutedEventArgs e) => Vm?.MemTest.Cancel();
}
