using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 系統引導修復服務：執行 SFC、DISM、CHKDSK、BootRec、BCDEdit 等標準修復命令，
/// 將即時輸出追加至 <see cref="Log"/>。全部唯讀查詢或 Windows 自帶修復機制，不碰韌體。
/// </summary>
public sealed class BootRepairService : ObservableObject
{
    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }

    /// <summary>是否以系統管理員執行（多數修復命令需要提升權限）。</summary>
    public bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    public ObservableCollection<string> Log { get; } = new();

    private Dispatcher? _dispatcher;
    internal void SetDispatcher(Dispatcher d) => _dispatcher = d;

    private void AppendLog(string line)
    {
        if (_dispatcher is not null && !_dispatcher.CheckAccess())
            _dispatcher.BeginInvoke(() => Log.Add(line));
        else
            Log.Add(line);
    }

    // ── 公開操作 ────────────────────────────────────────────────────

    /// <summary>sfc /scannow</summary>
    public async Task RunSfcAsync()
    {
        AppendLog("═══ SFC 系統檔案檢查 ═══");
        await RunCommandAsync("sfc", "/scannow");
    }

    /// <summary>DISM /Online /Cleanup-Image /RestoreHealth</summary>
    public async Task RunDismAsync()
    {
        AppendLog("═══ DISM 映像修復 ═══");
        await RunCommandAsync("DISM", "/Online /Cleanup-Image /RestoreHealth");
    }

    /// <summary>chkdsk &lt;系統磁碟&gt; /scan（唯讀掃描，不修改磁碟）</summary>
    public async Task RunChkdskAsync()
    {
        AppendLog("═══ CHKDSK 唯讀掃描 ═══");
        // 系統磁碟不一定是 C:（本機就搬過本體所在磁碟）；掃錯顆等於白跑一趟。
        string sys = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        await RunCommandAsync("chkdsk", $"{sys} /scan");
    }

    /// <summary>bootrec /scanos + /rebuildbcd（僅 UEFI 環境有意義）</summary>
    public async Task RunBootRecAsync()
    {
        AppendLog("═══ BootRec 引導重建 ═══");
        AppendLog("[bootrec /scanos]");
        await RunCommandAsync("bootrec", "/scanos");
        AppendLog("[bootrec /rebuildbcd]");
        await RunCommandAsync("bootrec", "/rebuildbcd");
    }

    /// <summary>bcdedit /enum 顯示目前引導組態</summary>
    public async Task RepairBcdAsync()
    {
        AppendLog("═══ BCDEdit 引導組態 ═══");
        await RunCommandAsync("bcdedit", "/enum");
    }

    /// <summary>依序執行 SFC → DISM → CHKDSK</summary>
    public async Task RunAllAsync()
    {
        AppendLog("═══ 一鍵全修開始（SFC → DISM → CHKDSK）═══");
        await RunSfcAsync();
        await RunDismAsync();
        await RunChkdskAsync();
        AppendLog("═══ 一鍵全修完成 ═══");
    }

    public void ClearLog() => Log.Clear();

    // ── 內部 ────────────────────────────────────────────────────

    private async Task RunCommandAsync(string fileName, string arguments)
    {
        if (IsRunning)
        {
            AppendLog("[等待] 上一個命令仍在執行…");
            return;
        }
        IsRunning = true;
        AppendLog($"> {fileName} {arguments}");

        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) AppendLog(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppendLog("[ERR] " + e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();
            AppendLog($"[結束] 結束代碼 {process.ExitCode}");
        }
        catch (Exception ex)
        {
            AppendLog($"[失敗] {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }
}
