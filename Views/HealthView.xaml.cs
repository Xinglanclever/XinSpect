using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>健康總評：綜合評分、指標狀態燈與升級建議。</summary>
/// <remarks>
/// 升級建議在進入本頁時重算一次。它的輸入含歷史統計，跟著每秒心跳重算只會讓文字不停跳動
/// 而不會更準確；使用者要在同一頁看變化時，按「重新分析」即可。
/// </remarks>
public partial class HealthView : UserControl, IPageLifecycle
{
    public HealthView() => InitializeComponent();

    public void OnActivated()
    {
        Vm?.RefreshUpgrade();
        Vm?.Whea.Refresh();          // 進頁自動讀一次；WHEA 事件不是每秒資料，跟著頁面走即可
        Vm?.Reliability.Refresh();
        Vm?.Mca.Refresh();
        Vm?.TimerFoundation.Refresh();
    }

    public void OnDeactivated() { }

    private void RefreshUpgrade_Click(object sender, RoutedEventArgs e) => Vm?.RefreshUpgrade();

    private void WheaRefresh_Click(object sender, RoutedEventArgs e) => Vm?.Whea.Refresh();

    private void ReliabilityRefresh_Click(object sender, RoutedEventArgs e) => Vm?.Reliability.Refresh();

    private void McaRefresh_Click(object sender, RoutedEventArgs e) => Vm?.Mca.Refresh();

    // Host.Content 延遲載入時 DataContext 由父容器繼承；仍以主視窗為後備。
    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Application.Current?.MainWindow?.DataContext as MainViewModel;
}
