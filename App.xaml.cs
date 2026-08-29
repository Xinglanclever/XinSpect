using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 應用程式進入點。除了 WPF 樣板外，這裡負責兩件事：<b>單一執行個體防護</b>，以及
/// <b>攔住所有沒人接的例外</b>——把它落地成紀錄並讓使用者知道，而不是讓視窗無聲消失或帶著壞掉的狀態繼續跑。
/// </summary>
public partial class App : Application
{
    /// <summary>最多主動彈出幾次提示；之後只默默寫紀錄，避免同一個壞掉的迴圈把畫面淹掉。</summary>
    private const int MaxDialogs = 3;

    private int _dialogs;
    /// <summary>具名信號須跟著行程活著，GC 前被收走等於沒防護；存欄位維持引用。</summary>
    private EventWaitHandle? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 單一執行個體：開兩份會重複開 Ring0 驅動、競寫同一份歷史與設定、系統匣出現兩顆圖示。
        // 已有實例時把它帶到前景（縮在系統匣時由使用者自圖示開啟），然後結束自己。
        _singleInstance = new EventWaitHandle(true, EventResetMode.AutoReset,
            @"Local\XinSpect.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            BringExistingToFront();
            MessageBox.Show(
                "曦覽已在執行中，僅能開啟一份。\n若主視窗不在畫面上，請由系統匣圖示開啟。",
                "曦覽 XinSpect", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // UI 執行緒（多半可續跑）
        DispatcherUnhandledException += OnDispatcherException;
        // 任何執行緒的致命例外（行程即將結束，只能留下紀錄）
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        // 沒人 await 的 Task 例外（.NET 預設會靜默丟棄——正是最容易漏掉問題的地方）
        TaskScheduler.UnobservedTaskException += OnTaskException;

        base.OnStartup(e);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    // 把既有實例的主視窗帶到前景；縮到系統匣（視窗隱藏）時帶不到，僅靠提示文字引導。
    private static void BringExistingToFront()
    {
        try
        {
            var other = Process.GetProcessesByName("XinSpect")
                .FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
            if (other is null) return;
            ShowWindowAsync(other.MainWindowHandle, 9);   // SW_RESTORE
            SetForegroundWindow(other.MainWindowHandle);
        }
        catch { /* 帶不到前景也無妨，僅靠提示 */ }
    }

    private void OnDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        bool saved = CrashLog.Write("UI 執行緒", e.Exception);

        // 記憶體耗盡之類的狀況續跑只會更糟：留下紀錄後照實崩潰，不假裝還能用。
        if (e.Exception is OutOfMemoryException) return;

        e.Handled = true;
        Notify("剛才有一項操作發生未預期的錯誤", e.Exception, saved);
    }

    private void OnDomainException(object? sender, UnhandledExceptionEventArgs e)
        => CrashLog.Write("背景執行緒（致命）", e.ExceptionObject as Exception);

    private void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLog.Write("背景工作", e.Exception);
        e.SetObserved();      // 標記已觀察，避免行程被終止
    }

    // 提示文字刻意寫明「哪一項壞了、紀錄在哪、有沒有存下來」，讓使用者能回報而不是只看到一句「發生錯誤」。
    private void Notify(string headline, Exception ex, bool saved)
    {
        if (_dialogs++ >= MaxDialogs) return;
        try
        {
            string tail = saved
                ? $"詳細紀錄已寫入：\n{CrashLog.FilePath}"
                : "（這次無法寫入紀錄檔，可能是權限或磁碟空間不足）";
            MessageBox.Show(
                $"{headline}：\n\n{ex.GetType().Name}：{ex.Message}\n\n"
                + "程式會繼續執行，但這一項的結果可能不完整。\n" + tail,
                $"{AppInfo.Name} — 未預期的錯誤",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* 連提示都彈不出來時就只剩紀錄檔了 */ }
    }
}
