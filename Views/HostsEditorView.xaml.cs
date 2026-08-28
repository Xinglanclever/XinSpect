using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// Hosts 編輯器：讀寫 %SystemRoot%\System32\drivers\etc\hosts。
/// 儲存前先把現有內容備份到 %LOCALAPPDATA%\XinSpect\hosts-backups，
/// 「還原上一版」載入最新備份供檢視後再存；「清除 DNS 快取」呼叫 ipconfig /flushdns。
/// 純本機、無第三方相依；寫入 System32 需系統管理員權限（本程式資訊清單已要求提權）。
/// </summary>
public partial class HostsEditorView : UserControl
{
    private static readonly string HostsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    private static string BackupDir
    {
        get
        {
            string d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XinSpect", "hosts-backups");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private bool _loaded;

    public HostsEditorView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; Load(); } };
    }

    private void Load()
    {
        try
        {
            Editor.Text = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
            StatusText.Text = $"已載入 {HostsPath} ・ {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "載入失敗：" + ex.Message;
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Load();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 先備份現有內容（若存在）
            if (File.Exists(HostsPath))
            {
                string bak = Path.Combine(BackupDir, $"hosts-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.Copy(HostsPath, bak, overwrite: true);
            }
            // hosts 慣例為無 BOM 之 ASCII/UTF-8；統一寫成 \r\n 換行
            string text = Editor.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            File.WriteAllText(HostsPath, text, new System.Text.UTF8Encoding(false));
            StatusText.Text = $"已儲存並備份 ・ {DateTime.Now:HH:mm:ss}（如需生效可清除 DNS 快取）";
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("無法寫入 hosts 檔：權限不足。\n請以系統管理員身分重新啟動曦覽。",
                "Hosts 編輯器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show("儲存失敗：" + ex.Message, "Hosts 編輯器",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = new DirectoryInfo(BackupDir).GetFiles("hosts-*.txt");
            if (files.Length == 0)
            {
                MessageBox.Show("尚無備份可還原。", "Hosts 編輯器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var newest = files.OrderByDescending(f => f.LastWriteTime).First();
            Editor.Text = File.ReadAllText(newest.FullName);
            StatusText.Text = $"已載入備份 {newest.Name}，請檢視後按「儲存」寫回。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "還原失敗：" + ex.Message;
        }
    }

    private void Flush_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            StatusText.Text = $"已清除 DNS 解析快取 ・ {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "清除 DNS 快取失敗：" + ex.Message;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe",
                $"/select,\"{HostsPath}\"") { UseShellExecute = true });
        }
        catch { /* 忽略：資料夾開啟非關鍵 */ }
    }
}
