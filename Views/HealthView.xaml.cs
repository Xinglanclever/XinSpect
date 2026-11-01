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
        // 磁碟清單要逐槽開裝置（\\.\X:）、TPM 要查 WMI 的 Win32_Tpm——整機磁碟或 WMI
        // 卡住時，這兩件事都會同步等到天荒地老。放在建構子裡，第一次點開「健康」頁就會把
        // 整個視窗凍住；先讓頁面出來，清單隨後補上。
        _ = Task.Run(LoadHeaderLists);
    }

    /// <summary>磁碟清單與 TPM／Secure Boot 列：背景讀，回 UI 執行緒才碰控制項。</summary>
    void LoadHeaderLists()
    {
        var volumes = DiskIo.Guarded(DiskSurfaceScanService.ListVolumes)
                      ?? new List<(string Path, string Label)>();
        var trust = ReadTrustRows();

        Shell.BeginOnUi(() =>
        {
            SurfaceDriveCombo.ItemsSource = volumes.Select(v => (v.Path, v.Label)).ToList();
            if (SurfaceDriveCombo.Items.Count > 0) SurfaceDriveCombo.SelectedIndex = 0;
            TrustRows.ItemsSource = trust;
        });
    }

    /// <summary>
    /// TPM／Secure Boot：WMI 沒有回應時照實說。原本的 <see cref="TpmSecureBootService.Read"/>
    /// 自己會把「讀不到」講成「未找到」，但那是查得到 TPM 命名空間的前提；整機 WMI 卡住時
    /// 根本走不到那裡，所以要由這裡把「無回應」與「真的沒有 TPM」分開。
    /// </summary>
    static List<TrustSecurityRow> ReadTrustRows()
    {
        var (rows, timedOut) = WmiGuard.Probe(TpmSecureBootService.Read);
        if (rows is not null) return rows;
        return [new TrustSecurityRow("TPM／Secure Boot", "—", timedOut ? "WMI 無回應" : "讀取失敗")];
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
        => _ = Task.Run(() =>
        {
            var (rows, timedOut) = WmiGuard.Probe(BluetoothBatteryService.Read);
            Shell.BeginOnUi(() =>
            {
                BluetoothRows.ItemsSource = rows;
                BluetoothStatus.Text = rows is null
                    ? (timedOut ? "WMI 無回應，稍後再試" : "讀取失敗")
                    : rows.Count == 0 ? "未找到藍牙裝置" : $"已列舉 {rows.Count} 個藍牙裝置";
            });
        });

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
        => _ = Task.Run(() =>
        {
            var rows = ReadTrustRows();
            Shell.BeginOnUi(() => TrustRows.ItemsSource = rows);
        });
}