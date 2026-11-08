using System.Collections.Generic;
using System.IO;

namespace XinSpect;

/// <summary>
/// winget.exe 的絕對路徑解析（解析成功即釘扎）：本程式常態以系統管理員執行，而
/// <see cref="System.Diagnostics.Process.Start"/>（UseShellExecute=false）對「未限定路徑的執行檔名」
/// 走 CreateProcess 搜尋語意——<b>當前工作目錄優先於 PATH</b>。從可寫目錄啟動（或被植入同名的
/// winget.exe）時，裸檔名 "winget" 等於一條提權執行面。因此本類別的契約：
/// <b>只回解析成功的絕對路徑；解析不到回 null，呼叫端如實報「不可用」，絕不退回裸檔名</b>。
/// </summary>
/// <remarks>
/// <para>搜尋順序（全部絕對路徑、不含 CWD）：</para>
/// <para>1. <c>%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe</c>——App Installer 的每使用者執行別名（最常見）；</para>
/// <para>2. <c>%ProgramFiles%\WindowsApps\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\winget.exe</c>——套件實體安裝目錄
/// （提權或另一使用者脈絡下，LOCALAPPDATA 的別名可能不在）；</para>
/// <para>3. <c>%SystemRoot%\System32\winget.exe</c>——個別伺服器版曾把別名放這裡，查無亦無害；</para>
/// <para>4. 逐條掃 <c>PATH</c>——這是<b>本類別自己掃</b>（Path.Combine＋File.Exists），只吃「X:\…」形狀的絕對項，
/// 相對項（含「.」）一律跳過；CreateProcess 的隱式搜尋會把 CWD 排在最前，自己掃正是為了不給它這個機會。</para>
/// <para>執行別名是 0-byte 的 appexeclink reparse point，<see cref="File.Exists"/> 對它回 true，可直接交給 CreateProcess。</para>
/// </remarks>
public static class WingetLocator
{
    private static string? _pinned;

    /// <summary>解析並釘扎 winget.exe 的絕對路徑；找不到回 null（呼叫端如實報不可用，不退回裸檔名）。</summary>
    public static string? Resolve()
    {
        if (_pinned is not null)
        {
            if (File.Exists(_pinned)) return _pinned;
            _pinned = null;   // 釘扎的檔案消失了（App Installer 被移除）——重新解析，不假裝還在
        }
        foreach (string candidate in Candidates())
        {
            try
            {
                if (candidate.Length > 0 && File.Exists(candidate))
                {
                    _pinned = candidate;
                    return _pinned;
                }
            }
            catch { /* 個別候選路徑檢查失敗（離線磁碟、被撤銷的授權目錄等）略過 */ }
        }
        return null;
    }

    /// <summary>清掉釘扎（測試與診斷用）：下一次 Resolve 重新掃。</summary>
    public static void Reset() => _pinned = null;

    private static IEnumerable<string> Candidates()
    {
        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");

        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
            yield return Path.Combine(programFiles, "WindowsApps", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "winget.exe");

        string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrEmpty(systemRoot))
            systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(systemRoot))
            yield return Path.Combine(systemRoot, "System32", "winget.exe");

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) yield break;
        foreach (string dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // 只吃「X:\…」絕對項；相對項（含「.」）正是要拒絕的 CWD 搜尋面，不掃
            if (dir.Length >= 3 && dir[1] == ':' && (dir[2] == '\\' || dir[2] == '/'))
                yield return Path.Combine(dir, "winget.exe");
        }
    }
}
