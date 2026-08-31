using System.Diagnostics;
using System.Text;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 內建終端機：以重導向標準輸入/輸出的方式承載一個「真實」的常駐 Shell 行程
/// （命令提示字元 cmd 或 PowerShell），使用者輸入的每一行都真正送入該行程執行，
/// stdout／stderr 原樣串流回顯——不模擬、不偽造輸出。行程沿用本 App 的權限
/// （若以系統管理員身分啟動，終端機亦具管理員權限）。關閉分頁或結束程式即終止該行程。
/// </summary>
public sealed class TerminalService : ObservableObject, IDisposable
{
    private const int MaxChars = 240_000;   // 輸出緩衝上限，超過即自前段截斷（避免無限成長）

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly StringBuilder _buf = new();
    private readonly object _bufLock = new();
    private Process? _proc;
    private DispatcherTimer? _flushTimer;   // 沖刷節流（見 ScheduleFlush）

    // ── 可觀察狀態 ──────────────────────────────────────────────────────────────
    private string _output = "";
    public string Output { get => _output; private set => SetProperty(ref _output, value); }

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(NotRunning)); } }
    public bool NotRunning => !_running;

    private bool _usePowerShell;
    /// <summary>true＝PowerShell；false＝命令提示字元（cmd）。切換後需重啟行程才生效。</summary>
    public bool UsePowerShell { get => _usePowerShell; set => SetProperty(ref _usePowerShell, value); }

    public string ShellName => _usePowerShell ? "PowerShell" : "命令提示字元 (cmd)";

    // ── 指令歷史（供輸入框上/下鍵回溯）─────────────────────────────────────────────
    private readonly List<string> _history = new();
    public IReadOnlyList<string> History => _history;

    /// <summary>啟動（或切換 Shell 後重啟）常駐行程。已在執行則先終止舊行程。</summary>
    public void Start()
    {
        Stop();
        try
        {
            var psi = _usePowerShell
                ? new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NoExit -Command -")
                : new ProcessStartInfo("cmd.exe", "/Q /K");   // /Q 關閉命令回顯、/K 保持常駐

            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc = proc;
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) Append(e.Data + "\n"); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Append(e.Data + "\n"); };
            // 僅在「結束的正是目前這個行程」時才標記為未執行，避免舊行程結束事件（重啟時）
            // 誤把剛啟動的新行程翻回未執行狀態。
            proc.Exited += (_, _) => _ui.BeginInvoke(() => { if (ReferenceEquals(_proc, proc)) IsRunning = false; });

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            IsRunning = true;

            Append($"── {ShellName} 已啟動（{(IsElevated() ? "系統管理員" : "一般使用者")}權限）──\n");
            if (!_usePowerShell)
                proc.StandardInput.WriteLine("chcp 65001 >nul");   // 讓 cmd 以 UTF-8 輸出，保留中文
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Append($"[終端機啟動失敗] {ex.Message}\n");
        }
    }

    /// <summary>送出一行指令到常駐 Shell；自行回顯提示字元＋指令以維持可讀性。</summary>
    public void Send(string command)
    {
        if (command is null) return;
        if (!IsRunning || _proc is null) { Append("[終端機未執行，請先啟動]\n"); return; }

        if (command.Length > 0 && (_history.Count == 0 || _history[^1] != command))
            _history.Add(command);

        Append($"{(_usePowerShell ? "PS> " : ">")} {command}\n");
        try { _proc.StandardInput.WriteLine(command); _proc.StandardInput.Flush(); }
        catch (Exception ex) { Append($"[送出失敗] {ex.Message}\n"); }
    }

    /// <summary>送出 Ctrl+C 對應的中止（結束目前行程並重啟，換得可用的 Shell）。</summary>
    public void Interrupt()
    {
        if (!IsRunning) return;
        Append("[已中止目前行程並重啟終端機]\n");
        Start();
    }

    public void Clear()
    {
        lock (_bufLock) _buf.Clear();
        Output = "";
    }

    public void Stop()
    {
        _flushTimer?.Stop();
        _flushTimer = null;
        var p = _proc;
        _proc = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
        IsRunning = false;
    }

    private void Append(string text)
    {
        lock (_bufLock)
        {
            _buf.Append(text);
            if (_buf.Length > MaxChars) _buf.Remove(0, _buf.Length - MaxChars);
        }
        // 輸出可能來自執行緒池的 *DataReceived 回呼，統一切回 UI 執行緒排程沖刷
        if (_ui.CheckAccess()) ScheduleFlush();
        else _ui.BeginInvoke(ScheduleFlush);
    }

    // 沖刷節流：每收到一行輸出就把整個緩衝（上限 24 萬字元）重建一次字串並讓 TextBox 全文重繪，
    // 遇上 dir /s 這類大量輸出的指令會每秒配置數百次。改為 80ms 合併沖刷一次：
    // 首行顯示延遲 80ms（肉眼無感），高吞吐時的配置與重繪開銷只剩原先的零頭。
    private void ScheduleFlush()
    {
        if (_flushTimer is not null) return;   // 已有排程在等，直接累積進緩衝
        _flushTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(80), DispatcherPriority.Background,
            (_, _) => { _flushTimer?.Stop(); _flushTimer = null; Flush(); }, _ui);
    }

    private void Flush()
    {
        string s; lock (_bufLock) s = _buf.ToString();
        Output = s;
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public void Dispose() => Stop();
}
