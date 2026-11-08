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
            StatusText.Text = LanguageService.T($"已載入 {HostsPath} ・ {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            StatusText.Text = LanguageService.T("載入失敗：") + ex.Message;
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Load();

    private bool _saving;

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        _saving = true;
        try
        {
            StatusText.Text = LanguageService.T("儲存中…");
            string text = Editor.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            // I/O（備份 Copy＋寫 System32）移出 UI 執行緒（P4-6 同批）。
            // 備份與寫入之間的 TOCTOU 校驗由 patch-32 補上（本補丁保持原備份形狀）。
            string error = await Task.Run(() =>
            {
                try
                {
                    // P4-6（TOCTOU）：原版 Copy 與 WriteAllText 是兩次獨立開檔——中間 hosts 被
                    // 第三者（其他工具／群組政策）改寫或替換的話，備份的不是即將被覆蓋的內容。
                    // 改為單一獨佔開檔（FileShare.None）：從同一個 handle 讀出備份、寫回新內容，
                    // 「備份的＝被覆蓋的」成立；期間第三者的開檔請求會被檔案共享衝突擋下（可感知），
                    // 而不是默默互相覆蓋。
                    if (File.Exists(HostsPath))
                    {
                        string bak = Path.Combine(BackupDir, $"hosts-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                        using var fs = new FileStream(HostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        string original;
                        using (var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true))
                            original = reader.ReadToEnd();
                        fs.Seek(0, SeekOrigin.Begin);
                        using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true))
                        {
                            writer.Write(text);
                            writer.Flush();
                        }
                        fs.SetLength(fs.Position);   // 新內容較短時截掉殘尾
                        File.WriteAllText(bak, original, new System.Text.UTF8Encoding(false));
                    }
                    else
                    {
                        File.WriteAllText(HostsPath, text, new System.Text.UTF8Encoding(false));
                    }
                    return "";
                }
                catch (UnauthorizedAccessException)
                {
                    return LanguageService.T("無法寫入 hosts 檔：權限不足。\n請以系統管理員身分重新啟動曦覽。");
                }
                catch (Exception ex)
                {
                    return LanguageService.T("儲存失敗：") + ex.Message;
                }
            });
            // 零彈窗：原兩處 XMsg.Show 改為狀態列如實顯示
            StatusText.Text = error.Length == 0
                ? LanguageService.T($"已儲存並備份 ・ {DateTime.Now:HH:mm:ss}（如需生效可清除 DNS 快取）")
                : error;
        }
        finally { _saving = false; }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = new DirectoryInfo(BackupDir).GetFiles("hosts-*.txt");
            if (files.Length == 0)
            {
                XMsg.Show(LanguageService.T("尚無備份可還原。"), "Hosts 編輯器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var newest = files.OrderByDescending(f => f.LastWriteTime).First();
            Editor.Text = File.ReadAllText(newest.FullName);
            StatusText.Text = LanguageService.T($"已載入備份 {newest.Name}，請檢視後按「儲存」寫回。");
        }
        catch (Exception ex)
        {
            StatusText.Text = LanguageService.T("還原失敗：") + ex.Message;
        }
    }

    private async void Flush_Click(object sender, RoutedEventArgs e)
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
            if (p is not null)
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            StatusText.Text = LanguageService.T($"已清除 DNS 解析快取 ・ {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            StatusText.Text = LanguageService.T("清除 DNS 快取失敗：") + ex.Message;
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
