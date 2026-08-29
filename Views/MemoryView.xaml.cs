using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>記憶體分頁：顯示 CPU-Z 等級的時序、通道、實體模組與 SPD 詳細資訊，並提供圖樣檢測。
/// 資料由 MainViewModel（DataContext 繼承自主視窗）提供，本身僅載入版面。</summary>
public partial class MemoryView : UserControl
{
    public MemoryView() => InitializeComponent();

    // Host.Content 延遲載入時 DataContext 由父容器繼承；仍以主視窗為後備。
    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Application.Current?.MainWindow?.DataContext as MainViewModel;

    private void MemTestStart_Click(object sender, RoutedEventArgs e) => Vm?.MemTest.Start();
    private void MemTestStop_Click(object sender, RoutedEventArgs e) => Vm?.MemTest.Cancel();
}
