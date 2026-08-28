using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 大檔／重複檔掃描分頁：自持 DiskScanService。選資料夾後於背景遞迴掃描、可取消，
/// 列出最大檔與（可選）逐位元組雜湊確認的重複檔；每筆可於檔案總管定位。
/// </summary>
public partial class DiskScanView : UserControl
{
    private readonly DiskScanService _svc = new();
    private CancellationTokenSource? _cts;

    public DiskScanView()
    {
        InitializeComponent();
        _svc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_svc.Status))
                Dispatcher.Invoke(() => StatusText.Text = _svc.Status);
        };
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "選擇要掃描的資料夾" };
        if (!string.IsNullOrEmpty(PathBox.Text) && Directory.Exists(PathBox.Text))
            dlg.InitialDirectory = PathBox.Text;
        if (dlg.ShowDialog() == true)
            PathBox.Text = dlg.FolderName;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        // 掃描進行中 → 此鈕作為取消
        if (_cts != null)
        {
            _cts.Cancel();
            return;
        }

        var root = PathBox.Text.Trim();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            StatusText.Text = "請先選擇有效的資料夾。";
            return;
        }

        _cts = new CancellationTokenSource();
        ScanBtn.Content = "取消";
        PickBtn.IsEnabled = false;
        DupCheck.IsEnabled = false;
        LargeList.ItemsSource = null;
        LargeEmpty.Visibility = Visibility.Collapsed;
        DupCard.Visibility = Visibility.Collapsed;
        ProgressText.Text = "掃描中…";

        bool findDup = DupCheck.IsChecked == true;
        var progress = new Progress<int>(n => ProgressText.Text = $"已掃描 {n:N0} 個檔案…");

        try
        {
            var result = await _svc.ScanAsync(root, findDup, progress, _cts.Token);

            LargeList.ItemsSource = result.LargeFiles;
            LargeEmpty.Visibility = result.LargeFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (findDup)
            {
                DupList.ItemsSource = result.Duplicates;
                DupCard.Visibility = Visibility.Visible;
                DupHeader.Text = result.Duplicates.Count == 0
                    ? "重複檔案：未發現內容完全相同的重複檔。"
                    : $"重複檔案：{result.Duplicates.Count} 組 ・ 合計可回收 {DiskScanService.Human(result.WastedTotal)}";
            }

            ProgressText.Text = $"完成 ・ 共 {result.TotalCount:N0} 個檔案 ・ 總計 {DiskScanService.Human(result.TotalSize)}"
                + (findDup ? $" ・ 重複可回收 {DiskScanService.Human(result.WastedTotal)}" : "");
            _svc.SetStatus(ProgressText.Text);
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "已取消掃描。";
        }
        catch (Exception ex)
        {
            ProgressText.Text = "掃描失敗：" + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            ScanBtn.Content = "開始掃描";
            PickBtn.IsEnabled = true;
            DupCheck.IsEnabled = true;
        }
    }

    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ScanFile f) return;
        try
        {
            if (File.Exists(f.FullPath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{f.FullPath}\"")
                { UseShellExecute = true });
            else if (Directory.Exists(f.Dir))
                Process.Start(new ProcessStartInfo(f.Dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("定位失敗：" + ex.Message, "大檔／重複檔掃描",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
