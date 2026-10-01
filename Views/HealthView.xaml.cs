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
    public HealthView()
    {
        InitializeComponent();
        SurfaceDriveCombo.ItemsSource = DiskSurfaceScanService.ListVolumes().Select(v => (v.Path, v.Label)).ToList();
        if (SurfaceDriveCombo.Items.Count > 0) SurfaceDriveCombo.SelectedIndex = 0;
        TrustRows.ItemsSource = TpmSecureBootService.Read();
    }

    public void OnActivated()
    {
        Vm?.RefreshUpgrade();
        Vm?.Whea.Refresh();          // 進頁自動讀一次；WHEA 事件不是每秒資料，跟著頁面走即可
        Vm?.Reliability.Refresh();
        Vm?.BootBreakdown.EnsureLoaded();   // 開機紀錄不會變，進頁讀一次就夠
        if (Vm is { } vm) vm.MachineAge.Update(vm.System);   // 兩個日期零特權，進頁算一次
        Vm?.Mca.Refresh();
        Vm?.TimerFoundation.Refresh();
        Vm?.PlatformTrust.Refresh();
        Vm?.PowerPolicy.Refresh();
    }

    public void OnDeactivated() { }

    private void RefreshUpgrade_Click(object sender, RoutedEventArgs e) => Vm?.RefreshUpgrade();

    private void WheaRefresh_Click(object sender, RoutedEventArgs e) => Vm?.Whea.Refresh();

    private void ReliabilityRefresh_Click(object sender, RoutedEventArgs e) => Vm?.Reliability.Refresh();

    /// <summary>磁碟通電時數：需要系統管理員身分，所以由使用者按下才讀。</summary>
    private void MachineAgeReadDisks_Click(object sender, RoutedEventArgs e) => Vm?.MachineAge.ReadDisks();

    /// <summary>開機耗時分解：唯讀讀取事件記錄。</summary>
    private void BootBreakdownRefresh_Click(object sender, RoutedEventArgs e) => Vm?.BootBreakdown.Refresh();

    private void McaRefresh_Click(object sender, RoutedEventArgs e) => Vm?.Mca.Refresh();

    private void PlatformTrustRefresh_Click(object sender, RoutedEventArgs e) => Vm?.PlatformTrust.Refresh();

    private void PowerPolicyRefresh_Click(object sender, RoutedEventArgs e) => Vm?.PowerPolicy.Refresh();

    // Host.Content 延遲載入時 DataContext 由父容器繼承；仍以主視窗為後備。
    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;
    private void BluetoothRefresh_Click(object sender, RoutedEventArgs e)
    {
        var rows = BluetoothBatteryService.Read();
        BluetoothRows.ItemsSource = rows;
        BluetoothStatus.Text = rows.Count == 0 ? "未找到藍牙裝置" : $"已列舉 {rows.Count} 個藍牙裝置";
    }

    private async void SurfaceScan_Click(object sender, RoutedEventArgs e)
    {
        if (SurfaceDriveCombo.SelectedItem is not (string path, string label)) return;
        SurfaceScanButton.IsEnabled = false;
        SurfaceProgress.Visibility = Visibility.Visible;
        SurfaceProgress.Value = 0;
        SurfaceStatus.Text = $"正在掃描 {label}…";
        SurfaceResults.ItemsSource = null;
        try
        {
            var progress = new Progress<int>(v => SurfaceProgress.Value = v);
            var result = await Task.Run(() => DiskSurfaceScanService.Scan(path, progress: progress));
            SurfaceProgress.Visibility = Visibility.Collapsed;
            if (result is null)
            {
                SurfaceStatus.Text = "無法開啟磁碟。邏輯卷需要讀取權限；實體磁碟需系統管理員。";
                return;
            }
            SurfaceStatus.Text = $"{result.DrivePath}・{result.TotalBlocks} 區塊×{result.BlockSize / 1024} KB・"
                + $"{result.OkCount} OK・{result.SlowCount} 慢・{result.ErrorCount} 錯誤・{result.ElapsedSec:F1} 秒";
            SurfaceResults.ItemsSource = new[]
            {
                new { Label = "結果", Ok = $"{result.OkCount}", Slow = $"{result.SlowCount}", Error = $"{result.ErrorCount}", Time = $"{result.ElapsedSec:F1}s" },
            };
        }
        catch (OperationCanceledException) { SurfaceStatus.Text = "已取消。"; }
        catch (Exception ex) { SurfaceStatus.Text = $"掃描失敗：{ex.Message}"; }
        finally { SurfaceScanButton.IsEnabled = true; SurfaceProgress.Visibility = Visibility.Collapsed; }
    }
    private void TrustRefresh_Click(object sender, RoutedEventArgs e)
        => TrustRows.ItemsSource = TpmSecureBootService.Read();
}