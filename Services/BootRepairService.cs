using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 系統引導修復服務：執行 SFC、DISM、CHKDSK、BootRec、BCDEdit 等標準修復命令，
/// 將即時輸出追加至 <see cref="Log"/>。
/// </summary>
/// <remarks>
/// <b>其中三個命令會修改系統檔案：</b>
/// <list type="bullet">
/// <item><c>sfc /scannow</c>——掃描並<b>替換</b>損壞的系統檔案。</item>
/// <item><c>DISM /RestoreHealth</c>——連絡 Windows Update 並<b>替換</b>元件庫中損壞的項目。</item>
/// <item><c>bootrec /rebuildbcd</c>——<b>重建</b>開機設定資料存放區。</item>
/// </list>
/// 其餘（<c>chkdsk /scan</c>、<c>bcdedit /enum</c>）是唯讀查詢。全部使用 Windows 自帶修復機制，不碰韌體。
/// </remarks>
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

    /// <summary>bootrec /scanos + /rebuildbcd（僅 UEFI 環境有意義）。
    /// P3-20：rebuildbcd 會重寫 BCD 存放區——動手前先 `bcdedit /export` 做一份位元組級
    /// 備份；備份不成功就不動手（沒有後路的開機設定修復不該發生）。備份路徑進 Log，
    /// 還原方式：`bcdedit /import <該檔>`（提權命令列）。</summary>
    public async Task RunBootRecAsync()
    {
        AppendLog("═══ BootRec 引導重建 ═══");
        AppendLog("[bootrec /scanos]");
        await RunCommandAsync("bootrec", "/scanos");

        string backup = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XinSpect", "bcd-backups", $"BCD-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        string bcdedit = Path.Combine(Environment.SystemDirectory, "bcdedit.exe");
        AppendLog($"[bcdedit /export \"{backup}\"]（P3-20：重建前備份現有 BCD 存放區）");
        int exportCode = await RunCommandAsync(bcdedit, $"/export \"{backup}\"");
        if (exportCode != 0)
        {
            AppendLog($"[中止] BCD 備份未成功（結束代碼 {exportCode}）——不執行 rebuildbcd。" +
                      "請先確認以系統管理員執行、或手動備份後再跑一次。");
            return;
        }
        AppendLog($"[bootrec /rebuildbcd]（BCD 已備份至 {backup}；如需還原：bcdedit /import \"{backup}\"）");
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

    private async Task<int> RunCommandAsync(string fileName, string arguments)
    {
        if (IsRunning)
        {
            AppendLog("[等待] 上一個命令仍在執行…");
            return -1;   // 未執行（防重入讓位）；呼叫端皆丟棄回傳值，唯 BootRec 的備份門會核對
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
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            AppendLog($"[失敗] {ex.Message}");
            return -1;
        }
        finally
        {
            IsRunning = false;
        }
    }
}
