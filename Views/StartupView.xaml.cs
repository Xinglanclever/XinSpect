using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 開機啟動項管理分頁：自持 StartupService，載入時掃描；可停用／啟用（可逆核准旗標）
/// 或定位項目所在位置（登錄項開啟 regedit、資料夾項於檔案總管中選取）。
/// </summary>
public partial class StartupView : UserControl
{
    private readonly StartupService _svc = new();
    private bool _loaded;

    public StartupView()
    {
        InitializeComponent();
        List.ItemsSource = _svc.Entries;
        _svc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_svc.Status))
                Dispatcher.Invoke(() => StatusText.Text = _svc.Status);
        };
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; _ = ScanAsync(); } };
    }

    /// <summary>排程工作那一段要開幾百個檔案，所以掃描是非同步的——期間畫面仍然可以操作。</summary>
    private async Task ScanAsync()
    {
        MsgText.Text = "掃描中…";
        await _svc.ScanAsync();
        MsgText.Text = "";
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void SysTasks_Click(object sender, RoutedEventArgs e)
    {
        _svc.ShowSystemTasks = SysTasks.IsChecked == true;
        MsgText.Text = "";
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is StartupEntry entry)
        {
            bool ok = _svc.SetEnabled(entry, !entry.Enabled);
            MsgText.Text = _svc.Status;
            if (!ok)
                MessageBox.Show(_svc.Status, "開機啟動項管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not StartupEntry entry) return;
        try
        {
            // 排程工作 → 開工作排程器。這種項目的「所在位置」是工作排程器裡的那條路徑，
            // 不是磁碟上的某個檔案，開檔案總管反而答錯問題。
            if (entry.IsTask)
            {
                Process.Start(new ProcessStartInfo("taskschd.msc") { UseShellExecute = true });
                MsgText.Text = $"已開啟工作排程器　・　此項位於 {entry.TaskPath}";
                return;
            }
            // 有可解析的實體路徑（捷徑或可執行檔）→ 於檔案總管中選取
            if (!string.IsNullOrEmpty(entry.ItemPath) && File.Exists(entry.ItemPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.ItemPath}\"")
                { UseShellExecute = true });
                MsgText.Text = "已於檔案總管中定位該項目。";
                return;
            }
            // 資料夾項但檔案已不存在 → 開啟其所在資料夾
            if (entry.IsFolder)
            {
                var dir = Path.GetDirectoryName(entry.Command);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                    MsgText.Text = "已開啟啟動資料夾。";
                    return;
                }
            }
            // 登錄項 → 開啟登錄編輯程式
            Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
            MsgText.Text = "已開啟登錄編輯程式（此項為登錄啟動項）。";
        }
        catch (Exception ex)
        {
            MessageBox.Show("定位失敗：" + ex.Message, "開機啟動項管理",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
