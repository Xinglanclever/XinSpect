using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 開機啟動項管理：列舉登錄 Run 機碼（HKLM／HKCU／32 位元）、啟動資料夾（使用者／公用）
/// 與<b>登入／開機觸發的排程工作</b>，並以 Windows 內建的可逆機制啟用／停用。
/// 登錄與資料夾項目寫 StartupApproved 核准旗標（與工作管理員等效）——不刪除原始項目，隨時可還原；
/// 排程工作走 <c>schtasks /change /enable|/disable</c>，同樣只切換啟用狀態、不刪工作。
/// 修改 HKLM、公用啟動或系統排程工作需系統管理員權限。純本機、無第三方相依。
/// </summary>
public sealed class StartupEntry : INotifyPropertyChanged
{
    public string Name { get; init; } = "";          // 登錄值名、.lnk 檔名或排程工作名
    public string Command { get; init; } = "";        // 指令列、.lnk 路徑或工作的執行動作
    public string Location { get; init; } = "";       // 顯示用來源（繁中）
    public bool FromHklm { get; init; }               // 核准旗標寫於 HKLM（否則 HKCU）
    public string ApprovalSub { get; init; } = "";    // StartupApproved 子鍵：Run／Run32／StartupFolder
    public bool IsFolder { get; init; }               // 啟動資料夾項目
    public string ItemPath { get; init; } = "";       // 供「定位」用（exe 或 .lnk 路徑）

    /// <summary>排程工作項目：停用走 schtasks，不是核准旗標。</summary>
    public bool IsTask { get; init; }

    /// <summary>排程工作的完整路徑（<c>\Foo\Bar</c>），供 schtasks 使用。</summary>
    public string TaskPath { get; init; } = "";

    /// <summary>Windows 自己的排程工作（<c>\Microsoft\…</c>）；預設不列出，避免把 OS 維護工作當成使用者裝的東西。</summary>
    public bool SystemTask { get; init; }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; OnChanged(nameof(Enabled)); OnChanged(nameof(StateText)); } }
    }

    public string StateText => Enabled ? "啟用中" : "已停用";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class StartupService : INotifyPropertyChanged
{
    /// <summary>掃到的全部項目（含被篩選條件隱藏的）。</summary>
    private readonly List<StartupEntry> _all = [];

    /// <summary>目前顯示的項目。</summary>
    public ObservableCollection<StartupEntry> Entries { get; } = new();

    private string _status = "";
    public string Status { get => _status; private set { _status = value; OnChanged(nameof(Status)); } }

    private bool _showSystemTasks;
    /// <summary>是否連 Windows 自己的排程工作一起列出。預設關——那幾十件是 OS 維護工作，不是使用者裝的東西。</summary>
    public bool ShowSystemTasks
    {
        get => _showSystemTasks;
        set { if (_showSystemTasks != value) { _showSystemTasks = value; OnChanged(nameof(ShowSystemTasks)); ApplyFilter(); } }
    }

    private bool _scanning;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    // ── 啟用／停用旗標編碼（與工作管理員相同）──
    //  啟用：02 00 00 00  00 00 00 00 00 00 00 00
    //  停用：03 00 00 00  ＋ 停用當下的 8 位元組 FILETIME
    //  判讀規則：首位元組最低位為 1 → 已停用（03、07…）；為 0 → 啟用中（02、06…）。
    private static readonly byte[] EnabledFlag = { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static byte[] DisabledFlag()
    {
        var b = new byte[12];
        b[0] = 3;
        BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(b, 4);
        return b;
    }
    private static bool IsDisabled(byte[]? flag) => flag is { Length: > 0 } && (flag[0] & 1) != 0;

    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunPath32 = @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovalBase = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>
    /// 掃描全部來源。登錄與啟動資料夾很快，就地讀；排程工作要開幾百個 XML 檔，丟到背景執行緒，
    /// 這樣按下「重新掃描」時畫面不會凍住。
    /// </summary>
    public async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            _all.Clear();
            Entries.Clear();
            Status = "掃描中…";

            ReadRunKey(Registry.CurrentUser, RunPath, "登錄 ・ 目前使用者", false, "Run", is32: false);
            ReadRunKey(Registry.LocalMachine, RunPath, "登錄 ・ 全部使用者", true, "Run", is32: false);
            ReadRunKey(Registry.LocalMachine, RunPath32, "登錄 ・ 全部使用者（32 位元）", true, "Run32", is32: true);

            ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                       "啟動資料夾 ・ 目前使用者", false);
            ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                       "啟動資料夾 ・ 全部使用者", true);

            _all.AddRange(await Task.Run(CollectScheduledTasks));
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Status = "掃描開機啟動項失敗：" + ex.Message;
        }
        finally
        {
            _scanning = false;
        }
    }

    /// <summary>同步呼叫端的入口（不等結果）。</summary>
    public void Scan() => _ = ScanAsync();

    private void ApplyFilter()
    {
        Entries.Clear();
        foreach (var e in _all)
        {
            if (e.SystemTask && !_showSystemTasks) continue;
            Entries.Add(e);
        }

        int on = 0, off = 0, task = 0;
        foreach (var e in Entries) { if (e.Enabled) on++; else off++; if (e.IsTask) task++; }
        int hidden = _all.Count - Entries.Count;

        Status = _all.Count == 0
            ? "未偵測到任何開機啟動項目。"
            : $"共 {Entries.Count} 項（其中排程工作 {task} 件）　・　啟用中 {on}　・　已停用 {off}"
              + (hidden > 0 ? $"　・　已隱藏 Windows 內建排程工作 {hidden} 件" : "");
    }

    private void ReadRunKey(RegistryKey hive, string path, string label, bool fromHklm, string approvalSub, bool is32)
    {
        using var run = hive.OpenSubKey(path);
        if (run == null) return;
        using var approval = OpenApproval(fromHklm, approvalSub, writable: false);
        foreach (var name in run.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;
            var cmd = Convert.ToString(run.GetValue(name)) ?? "";
            var flag = approval?.GetValue(name) as byte[];
            _all.Add(new StartupEntry
            {
                Name = name,
                Command = cmd,
                Location = label,
                FromHklm = fromHklm,
                ApprovalSub = approvalSub,
                IsFolder = false,
                ItemPath = ExtractExe(cmd),
                Enabled = !IsDisabled(flag),
            });
        }
    }

    private void ReadFolder(string dir, string label, bool fromHklm)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        using var approval = OpenApproval(fromHklm, "StartupFolder", writable: false);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var fn = Path.GetFileName(file);
            // 略過 desktop.ini 等非捷徑雜項
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".ini") continue;
            var flag = approval?.GetValue(fn) as byte[];
            _all.Add(new StartupEntry
            {
                Name = fn,
                Command = file,
                Location = label,
                FromHklm = fromHklm,
                ApprovalSub = "StartupFolder",
                IsFolder = true,
                ItemPath = file,
                Enabled = !IsDisabled(flag),
            });
        }
    }

    private static RegistryKey? OpenApproval(bool fromHklm, string sub, bool writable)
    {
        var root = fromHklm ? Registry.LocalMachine : Registry.CurrentUser;
        var full = ApprovalBase + "\\" + sub;
        return writable
            ? root.CreateSubKey(full, writable: true)
            : root.OpenSubKey(full, writable: false);
    }

    // ── 排程工作 ──────────────────────────────────────────────────────────
    //  工作管理員的「啟動」分頁和 Run 機碼都看不到排程工作，但很多常駐更新程式正是靠
    //  登入觸發的排程工作起來的。這裡直接讀 System32\Tasks 底下的工作定義 XML（唯讀），
    //  不走 COM 也不 shell out，掃描本身完全不改任何東西。

    private static List<StartupEntry> CollectScheduledTasks()
    {
        var list = new List<StartupEntry>();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
        if (Directory.Exists(root)) WalkTasks(root, root, list);
        return list;
    }

    private static void WalkTasks(string root, string dir, List<StartupEntry> list)
    {
        string[] files, dirs;
        // 部分工作資料夾的 ACL 不給讀（例如某些 OS 元件專用的）；跳過那一層就好，不要讓整份清單掛掉。
        try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
        catch (Exception ex) { Diag.Swallow("列舉排程工作資料夾 " + dir, ex, "略過這個資料夾"); return; }

        foreach (var file in files)
        {
            string xml;
            try { xml = File.ReadAllText(file); }   // 工作定義是 UTF-16 帶 BOM，ReadAllText 會自己認出來
            catch (Exception ex) { Diag.Swallow("讀取排程工作 " + file, ex, "略過這一件"); continue; }

            string taskPath = "\\" + Path.GetRelativePath(root, file).Replace('/', '\\');
            if (ScheduledTaskStartup.Parse(xml, taskPath) is not { } info) continue;

            list.Add(new StartupEntry
            {
                Name = info.Name,
                Command = info.Command,
                Location = "排程工作 ・ " + info.TriggerText + (info.SystemBuiltIn ? "（Windows 內建）" : ""),
                IsTask = true,
                TaskPath = info.TaskPath,
                SystemTask = info.SystemBuiltIn,
                ItemPath = ExtractExe(info.Command),
                Enabled = info.Enabled,
            });
        }

        foreach (var sub in dirs) WalkTasks(root, sub, list);
    }

    /// <summary>啟用／停用單一項目——寫入可逆的核准旗標，絕不刪除原始 Run 值或捷徑。</summary>
    public bool SetEnabled(StartupEntry e, bool enable)
    {
        if (e.IsTask) return SetTaskEnabled(e, enable);
        try
        {
            using var approval = OpenApproval(e.FromHklm, e.ApprovalSub, writable: true);
            if (approval == null)
            {
                Status = "無法開啟核准機碼（可能需要系統管理員權限）。";
                return false;
            }
            approval.SetValue(e.Name, enable ? EnabledFlag : DisabledFlag(), RegistryValueKind.Binary);
            e.Enabled = enable;
            Status = $"已{(enable ? "啟用" : "停用")}「{e.Name}」（可隨時還原）。";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Status = $"權限不足，無法變更「{e.Name}」。此項位於 {(e.FromHklm ? "全部使用者（HKLM）" : "目前使用者")}，請以系統管理員身分執行。";
            return false;
        }
        catch (Exception ex)
        {
            Status = $"變更「{e.Name}」失敗：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 啟用／停用排程工作，走 Windows 自己的 <c>schtasks /change</c>——只切換啟用狀態，不刪除工作，
    /// 隨時可以再切回來（在工作排程器裡也看得到同樣的結果）。
    /// </summary>
    /// <remarks>
    /// 刻意不解讀 schtasks 的輸出：它以主控台的 OEM 字碼頁（繁中環境是 cp950）輸出，
    /// 讀回來很容易變成亂碼。判斷成敗只看結束代碼，訊息由這裡自己用繁中寫。
    /// </remarks>
    private bool SetTaskEnabled(StartupEntry e, bool enable)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/change");
            psi.ArgumentList.Add("/tn");
            psi.ArgumentList.Add(e.TaskPath);
            psi.ArgumentList.Add(enable ? "/enable" : "/disable");

            using var p = Process.Start(psi);
            if (p is null)
            {
                Status = "無法啟動 schtasks.exe。";
                return false;
            }
            // 先把兩條管線讀空再等結束，否則輸出多一點就會互相卡住
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                Status = $"變更排程工作「{e.Name}」逾時。";
                return false;
            }
            if (p.ExitCode != 0)
            {
                Status = $"無法{(enable ? "啟用" : "停用")}排程工作「{e.Name}」（schtasks 結束代碼 {p.ExitCode}）。"
                       + "受系統保護的工作，或需要系統管理員權限的工作會被拒絕。";
                return false;
            }
            e.Enabled = enable;
            Status = $"已{(enable ? "啟用" : "停用")}排程工作「{e.Name}」（可隨時還原）。";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"變更排程工作「{e.Name}」失敗：{ex.Message}";
            return false;
        }
    }

    /// <summary>從指令列取出可執行檔路徑（處理前導引號與參數）。</summary>
    private static string ExtractExe(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return "";
        cmd = cmd.Trim();
        try
        {
            if (cmd[0] == '"')
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 1) return Environment.ExpandEnvironmentVariables(cmd.Substring(1, end - 1));
            }
            int sp = cmd.IndexOf(' ');
            var head = sp > 0 ? cmd.Substring(0, sp) : cmd;
            head = Environment.ExpandEnvironmentVariables(head);
            if (File.Exists(head)) return head;
            // 帶參數且未加引號時，逐段嘗試（處理路徑含空白的常見情形）
            if (sp > 0)
            {
                var expanded = Environment.ExpandEnvironmentVariables(cmd);
                if (File.Exists(expanded)) return expanded;
            }
            return head;
        }
        catch { return cmd; }
    }
}
