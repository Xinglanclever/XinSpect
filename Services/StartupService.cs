using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 開機啟動項管理：列舉登錄 Run 機碼（HKLM／HKCU／32 位元）與啟動資料夾（使用者／公用）中的開機自啟項目，
/// 並以 Windows 內建、與工作管理員相同的 StartupApproved 機制啟用／停用——寫入可逆的核准旗標，
/// 不刪除原始項目，隨時可還原。修改 HKLM 或公用啟動需系統管理員權限。純本機、無第三方相依。
/// </summary>
public sealed class StartupEntry : INotifyPropertyChanged
{
    public string Name { get; init; } = "";          // 登錄值名或 .lnk 檔名
    public string Command { get; init; } = "";        // 指令列或 .lnk 路徑
    public string Location { get; init; } = "";       // 顯示用來源（繁中）
    public bool FromHklm { get; init; }               // 核准旗標寫於 HKLM（否則 HKCU）
    public string ApprovalSub { get; init; } = "";    // StartupApproved 子鍵：Run／Run32／StartupFolder
    public bool IsFolder { get; init; }               // 啟動資料夾項目
    public string ItemPath { get; init; } = "";       // 供「定位」用（exe 或 .lnk 路徑）

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
    public ObservableCollection<StartupEntry> Entries { get; } = new();

    private string _status = "";
    public string Status { get => _status; private set { _status = value; OnChanged(nameof(Status)); } }

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

    public void Scan()
    {
        Entries.Clear();
        try
        {
            ReadRunKey(Registry.CurrentUser, RunPath, "登錄 ・ 目前使用者", false, "Run", is32: false);
            ReadRunKey(Registry.LocalMachine, RunPath, "登錄 ・ 全部使用者", true, "Run", is32: false);
            ReadRunKey(Registry.LocalMachine, RunPath32, "登錄 ・ 全部使用者（32 位元）", true, "Run32", is32: true);

            ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                       "啟動資料夾 ・ 目前使用者", false);
            ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                       "啟動資料夾 ・ 全部使用者", true);

            int on = 0, off = 0;
            foreach (var e in Entries) { if (e.Enabled) on++; else off++; }
            Status = Entries.Count == 0
                ? "未偵測到任何開機啟動項目。"
                : $"共 {Entries.Count} 項　・　啟用中 {on}　・　已停用 {off}";
        }
        catch (Exception ex)
        {
            Status = "掃描開機啟動項失敗：" + ex.Message;
        }
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
            Entries.Add(new StartupEntry
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
            Entries.Add(new StartupEntry
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

    /// <summary>啟用／停用單一項目——寫入可逆的核准旗標，絕不刪除原始 Run 值或捷徑。</summary>
    public bool SetEnabled(StartupEntry e, bool enable)
    {
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
