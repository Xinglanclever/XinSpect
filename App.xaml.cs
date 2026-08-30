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

    /// <summary>第二份實例等既有實例把視窗叫回來的時限；逾時就退回提示文字。</summary>
    private const int ActivateTimeoutMs = 3000;

    private int _dialogs;
    /// <summary>具名信號須跟著行程活著，GC 前被收走等於沒防護；存欄位維持引用。</summary>
    private EventWaitHandle? _singleInstance;
    /// <summary>「請把視窗叫回來」：由第二份實例設定，既有實例的接聽執行緒等它。</summary>
    private EventWaitHandle? _activateRequest;
    /// <summary>「叫回來了」：既有實例顯示視窗後設定，第二份實例據此決定要不要彈提示。</summary>
    private EventWaitHandle? _activateDone;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 單一執行個體：開兩份會重複開 Ring0 驅動、競寫同一份歷史與設定、系統匣出現兩顆圖示。
        // 但「不准開第二份」不等於「什麼都不做」——使用者再點一次圖示，要的是視窗回來。
        _singleInstance = new EventWaitHandle(true, EventResetMode.AutoReset,
            @"Local\XinSpect.SingleInstance", out bool createdNew);
        _activateRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\XinSpect.Activate");
        _activateDone = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\XinSpect.Activated");

        if (!createdNew)
        {
            RequestExistingToShow();
            Shutdown();
            return;
        }

        StartActivationListener();

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

    /// <summary>
    /// 既有實例側：背景等「請把視窗叫回來」的信號，收到就把主視窗顯示出來並回報完成。
    /// </summary>
    /// <remarks>
    /// 完成信號刻意在 UI 執行緒<b>真的顯示完視窗之後</b>才設定，不是收到請求就先回報。
    /// 因為第二份實例拿這個信號當「可以安靜退場了」的依據——若這裡提前回報，UI 執行緒卡住時
    /// 使用者會什麼都沒看到；照實回報，對方逾時就會退回提示文字。
    ///
    /// 請求信號是 AutoReset，被讀掉就沒有第二次。而「啟動途中就被再點一次」正是主視窗還沒建好的時刻，
    /// 若當下直接放棄，這次請求就白白消失，對方只能乾等到逾時看提示。故在這裡自行重試到視窗出現為止。
    /// </remarks>
    private void StartActivationListener()
    {
        var t = new Thread(() =>
        {
            var request = _activateRequest;
            if (request is null) return;
            while (true)
            {
                try
                {
                    request.WaitOne();
                    // 上限 2 秒，仍在對方 3 秒逾時之內；等不到就讓對方走提示文字那條路
                    for (int i = 0; i < 20; i++)
                    {
                        if (TryShowMainWindow())
                        {
                            try { _activateDone?.Set(); } catch { /* 對方已放棄等待 */ }
                            break;
                        }
                        Thread.Sleep(100);
                    }
                }
                catch (Exception ex) { Diag.Swallow("等待喚回主視窗的信號", ex, "本次不再接聽喚回請求"); return; }
            }
        })
        { IsBackground = true, Name = "XinActivate" };
        t.Start();
    }

    /// <summary>在 UI 執行緒上把主視窗顯示出來；視窗還沒建好回 false（由呼叫端決定要不要再等）。</summary>
    /// <remarks>用同步 <c>Invoke</c> 而非 <c>BeginOnUi</c>，因為呼叫端要知道「到底顯示成功了沒有」
    /// 才能決定回報完成或繼續重試。UI 執行緒不會反過來等這個執行緒，沒有互鎖風險。</remarks>
    private static bool TryShowMainWindow()
    {
        var d = Shell.Dispatcher;
        if (d is null) return false;
        try
        {
            return d.Invoke(() =>
            {
                if (Shell.Main is not { } w) return false;
                w.RestoreToForeground();
                return true;
            });
        }
        catch (Exception ex)
        {
            Diag.Swallow("喚回主視窗", ex, "本次喚回未完成，第二份實例會改以提示文字引導");
            return false;
        }
    }

    /// <summary>
    /// 第二份實例側：請既有實例把視窗叫回來。成功就安靜退場，不成功才說明狀況。
    /// </summary>
    /// <remarks>
    /// 兩段是必要的：既有實例自己 <c>Show()</c> 後仍可能搶不到前景（要求前景的是它、
    /// 而剛被使用者點起來的是我們這個行程），所以等它回報顯示完成後，再由這邊
    /// <see cref="SetForegroundWindow"/> 補上最後一步——此時它的
    /// <c>MainWindowHandle</c> 才不是 0，先前縮在系統匣時帶不動就是卡在這裡。
    /// </remarks>
    private void RequestExistingToShow()
    {
        bool shown = false;
        try
        {
            _activateDone?.Reset();          // 清掉上一次殘留的回報，免得誤判成本次成功
            _activateRequest?.Set();
            shown = _activateDone?.WaitOne(ActivateTimeoutMs) == true;
        }
        catch (Exception ex) { Diag.Swallow("請既有實例顯示主視窗", ex, "本次改以提示文字引導"); }

        BringExistingToFront();
        if (shown) return;                   // 視窗已回到畫面，再多一個對話框只是噪音

        MessageBox.Show(
            "曦覽已在執行中，僅能開啟一份。\n若主視窗不在畫面上，請由系統匣圖示開啟。",
            "曦覽 XinSpect", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // 把既有實例的主視窗帶到前景。視窗隱藏（縮在系統匣）時 MainWindowHandle 為 0，
    // 故先由 RequestExistingToShow 請它自己顯示出來，這裡才帶得動。
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
