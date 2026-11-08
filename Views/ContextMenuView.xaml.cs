using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 右鍵選單管理分頁：自持 ContextMenuService，載入時掃描；可切換靜態指令的顯示、
/// 或複製登錄路徑並開啟 regedit 定位。COM 處理常式唯讀。
/// </summary>
public partial class ContextMenuView : UserControl
{
    private readonly ContextMenuService _svc = new();
    private bool _loaded;

    public ContextMenuView()
    {
        InitializeComponent();
        List.ItemsSource = _svc.Entries;
        _svc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_svc.Status))
                Dispatcher.Invoke(() => StatusText.Text = _svc.Status);
        };
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; Scan(); } };
    }

    private bool _scanning;

    private async void Scan()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            MsgText.Text = LanguageService.T("掃描中…");   // await 之後這行才真的有機會被畫出來
            var list = await Task.Run(_svc.Collect);       // 登錄檔全掃移出 UI 執行緒
            _svc.Apply(list);                              // 集合異動回 UI 執行緒
            MsgText.Text = "";
        }
        catch (Exception ex)
        {
            MsgText.Text = LanguageService.T("掃描失敗：") + ex.Message;   // 零彈窗：如實進頁面文字
        }
        finally { _scanning = false; }
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => Scan();

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ContextMenuEntry entry)
        {
            var (ok, msg) = _svc.SetEnabled(entry, !entry.Enabled);
            MsgText.Text = msg;
            if (!ok)
                XMsg.Show(msg, "右鍵選單管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ContextMenuEntry entry) return;
        try
        {
            try { Clipboard.SetText(entry.RegistryPath); } catch { /* 剪貼簿偶爾被占用，非致命 */ }
            Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
            MsgText.Text = LanguageService.T("已開啟登錄編輯程式，完整路徑已複製到剪貼簿，貼到位址列即可定位。");
        }
        catch (Exception ex)
        {
            XMsg.Show(LanguageService.T("開啟登錄編輯程式失敗：") + ex.Message, "右鍵選單管理",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
