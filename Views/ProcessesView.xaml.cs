using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>行程分頁：完整行程清單（可搜尋/排序）、右鍵功能表與「結束工作」（終止行程）。</summary>
public partial class ProcessesView : UserControl
{
    public ProcessesView() => InitializeComponent();

    // 由觸發控制項（列上的按鈕或右鍵功能表項）取出其繫結的 ProcRow 與行程服務。
    private static bool TryResolve(object sender, out ProcRow row, out ProcessService proc)
    {
        row = null!; proc = null!;
        if (sender is not FrameworkElement { DataContext: ProcRow r }) return false;
        if (Application.Current.MainWindow?.DataContext is not MainViewModel { Proc: { } p }) return false;
        row = r; proc = p; return true;
    }

    // 結束工作：終止行程屬破壞性操作，先以對話框確認，失敗則回報原因。
    private void EndRow(ProcRow row, ProcessService proc)
    {
        var confirm = MessageBox.Show(
            $"確定要結束行程「{row.Name}」（PID {row.Pid}）？\n未儲存的資料將會遺失，若為系統關鍵行程可能導致系統不穩定。",
            "結束工作", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var err = proc.EndTask(row.Pid);
        if (err is not null)
            MessageBox.Show($"無法結束行程「{row.Name}」：{err}", "結束工作",
                            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void EndTask_Click(object sender, RoutedEventArgs e)
    {
        if (TryResolve(sender, out var row, out var proc)) EndRow(row, proc);
    }

    private void CtxEnd_Click(object sender, RoutedEventArgs e)
    {
        if (TryResolve(sender, out var row, out var proc)) EndRow(row, proc);
    }

    // 開啟檔案位置：按需解析主模組路徑（存取受限的系統／他人行程會取不到），成功則於檔案總管選取該檔。
    private void CtxOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolve(sender, out var row, out var proc)) return;
        var path = proc.PathOf(row.Pid);
        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show($"無法取得行程「{row.Name}」的檔案位置（可能為系統行程或存取受限）。",
                            "開啟檔案位置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show($"開啟檔案位置失敗：{ex.Message}", "開啟檔案位置",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CtxCopyPid_Click(object sender, RoutedEventArgs e)
    {
        if (TryResolve(sender, out var row, out _)) TrySetClipboard(row.PidText);
    }

    private void CtxCopyName_Click(object sender, RoutedEventArgs e)
    {
        if (TryResolve(sender, out var row, out _)) TrySetClipboard(row.Name);
    }

    // 線上搜尋：以行程名稱＋「process」關鍵字開啟預設瀏覽器查詢（便於辨識不明行程）。
    private void CtxSearch_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolve(sender, out var row, out _)) return;
        var q = Uri.EscapeDataString($"{row.Name} process");
        try { Process.Start(new ProcessStartInfo($"https://www.bing.com/search?q={q}") { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show($"開啟瀏覽器失敗：{ex.Message}", "線上搜尋",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { /* 剪貼簿偶發被其他程式占用，靜默略過 */ }
    }
}
