using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 右鍵選單管理：列舉並管理檔案總管右鍵選單中的「靜態指令」。
/// 讀取 HKEY_CLASSES_ROOT 下各情境（所有檔案 *、資料夾、資料夾空白處、磁碟機、桌面背景……）
/// 的 shell 動詞；以文件化、可逆的 LegacyDisable 值切換其顯示——寫入即隱藏、移除即還原，
/// 完全不刪除原始指令，安全可還原。COM 處理常式（shellex\ContextMenuHandlers）僅唯讀列出，
/// 因其由各軟體自行註冊，建議於該軟體設定或 regedit 中處理。純本機、無第三方相依。
/// </summary>
public sealed class ContextMenuEntry : INotifyPropertyChanged
{
    public string Scope { get; init; } = "";        // 情境（繁中顯示名）
    public string Verb { get; init; } = "";          // 動詞（登錄機碼子鍵名）
    public string DisplayName { get; init; } = "";   // 友善顯示名
    public string Command { get; init; } = "";       // 實際執行指令（COM 為 CLSID）
    public string Kind { get; init; } = "";          // 「靜態指令」／「COM 處理常式」
    public bool CanToggle { get; init; }             // 靜態指令可切換；COM 唯讀
    public string RegistryPath { get; init; } = "";  // 完整登錄路徑（供 regedit 定位）

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; OnChanged(nameof(Enabled)); OnChanged(nameof(StateText)); } }
    }

    public string CommandText => Command.Length > 0 ? Command : "（由 COM 元件執行，無靜態指令）";
    public string StateText => !CanToggle ? "COM ・ 唯讀" : Enabled ? "啟用中" : "已隱藏";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class ContextMenuService : INotifyPropertyChanged
{
    public ObservableCollection<ContextMenuEntry> Entries { get; } = new();

    private string _status = "";
    public string Status { get => _status; private set { _status = value; OnChanged(nameof(Status)); } }

    // 各右鍵情境的 HKCR 根機碼 → 繁中顯示名，涵蓋最常見的情境。
    private static readonly (string key, string name)[] Scopes =
    {
        ("*",                     "所有檔案"),
        ("AllFilesystemObjects",  "所有檔案與資料夾"),
        ("Directory",             "資料夾"),
        ("Directory\\Background", "資料夾空白處"),
        ("Folder",                "資料夾（通用）"),
        ("Drive",                 "磁碟機"),
        ("DesktopBackground",     "桌面背景"),
    };

    public void Scan()
    {
        try
        {
            var list = new List<ContextMenuEntry>();
            foreach (var (key, name) in Scopes)
            {
                ReadStaticVerbs(key, name, list);
                ReadComHandlers(key, name, list);
            }
            // 排序：情境 → 可切換優先 → 名稱。
            list.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Scope, b.Scope);
                if (c != 0) return c;
                if (a.CanToggle != b.CanToggle) return a.CanToggle ? -1 : 1;
                return string.CompareOrdinal(a.DisplayName, b.DisplayName);
            });
            Entries.Clear();
            foreach (var e in list) Entries.Add(e);
            int toggle = list.Count(x => x.CanToggle);
            Status = $"共 {list.Count} 項（可切換靜態指令 {toggle}，COM 唯讀 {list.Count - toggle}） ・ {DateTime.Now:HH:mm:ss} 掃描";
        }
        catch (Exception ex)
        {
            Status = "讀取右鍵選單失敗：" + ex.Message;
        }
    }

    // 讀取某情境下的靜態 shell 動詞（登錄機碼名稱不分大小寫，故 DesktopBackground\Shell 亦可命中）。
    private static void ReadStaticVerbs(string scopeKey, string scopeName, List<ContextMenuEntry> outList)
    {
        string shellPath = scopeKey + "\\shell";
        using var sh = Registry.ClassesRoot.OpenSubKey(shellPath);
        if (sh == null) return;
        foreach (var verb in sh.GetSubKeyNames())
        {
            try
            {
                using var vk = sh.OpenSubKey(verb);
                if (vk == null) continue;
                bool disabled = vk.GetValue("LegacyDisable") != null;
                string cmd = "";
                using (var ck = vk.OpenSubKey("command"))
                    if (ck?.GetValue(null) is string s) cmd = s;
                outList.Add(new ContextMenuEntry
                {
                    Scope = scopeName,
                    Verb = verb,
                    DisplayName = ResolveDisplay(vk, verb),
                    Command = cmd,
                    Kind = "靜態指令",
                    CanToggle = true,
                    Enabled = !disabled,
                    RegistryPath = "HKEY_CLASSES_ROOT\\" + shellPath + "\\" + verb,
                });
            }
            catch { /* 個別動詞讀取失敗略過，不影響整體 */ }
        }
    }

    // 讀取某情境下的 COM 內容選單處理常式（唯讀）。
    private static void ReadComHandlers(string scopeKey, string scopeName, List<ContextMenuEntry> outList)
    {
        string basePath = scopeKey + "\\shellex\\ContextMenuHandlers";
        using var root = Registry.ClassesRoot.OpenSubKey(basePath);
        if (root == null) return;
        foreach (var name in root.GetSubKeyNames())
        {
            try
            {
                using var hk = root.OpenSubKey(name);
                string clsid = (hk?.GetValue(null) as string ?? name).Trim();
                outList.Add(new ContextMenuEntry
                {
                    Scope = scopeName,
                    Verb = name,
                    DisplayName = FriendlyClsid(clsid, name),
                    Command = clsid,
                    Kind = "COM 處理常式",
                    CanToggle = false,
                    Enabled = true,
                    RegistryPath = "HKEY_CLASSES_ROOT\\" + basePath + "\\" + name,
                });
            }
            catch { }
        }
    }

    // 顯示名解析：優先 MUIVerb，其次預設值；@dll,-id 資源字串以 SHLoadIndirectString 還原為文字。
    private static string ResolveDisplay(RegistryKey vk, string verb)
    {
        string? mui = vk.GetValue("MUIVerb") as string;
        string? def = vk.GetValue(null) as string;
        string pick = !string.IsNullOrWhiteSpace(mui) ? mui!
                    : !string.IsNullOrWhiteSpace(def) ? def! : verb;
        pick = Indirect(pick).Trim();
        return pick.Length > 0 ? pick : verb;
    }

    private static string FriendlyClsid(string clsid, string fallback)
    {
        if (clsid.StartsWith("{") && clsid.EndsWith("}"))
        {
            try
            {
                using var ck = Registry.ClassesRoot.OpenSubKey("CLSID\\" + clsid);
                if (ck?.GetValue(null) is string s && !string.IsNullOrWhiteSpace(s))
                    return Indirect(s).Trim();
            }
            catch { }
        }
        return fallback;
    }

    private static string Indirect(string s)
    {
        if (string.IsNullOrEmpty(s) || s[0] != '@') return s;
        var sb = new StringBuilder(1024);
        return SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0
            ? sb.ToString() : s;
    }

    // 切換靜態指令顯示與否：停用＝寫入空字串 LegacyDisable；啟用＝移除該值。不動原始指令，隨時可逆。
    public (bool ok, string message) SetEnabled(ContextMenuEntry e, bool enabled)
    {
        if (!e.CanToggle)
            return (false, "COM 處理常式由其所屬軟體註冊，本工具僅供檢視；請於該軟體設定或 regedit 中處理。");
        try
        {
            string path = e.RegistryPath.Replace("HKEY_CLASSES_ROOT\\", "");
            using var vk = Registry.ClassesRoot.OpenSubKey(path, writable: true);
            if (vk == null) return (false, "找不到對應的登錄機碼，可能已被移除；請重新掃描。");
            if (enabled)
            {
                if (vk.GetValue("LegacyDisable") != null) vk.DeleteValue("LegacyDisable", throwOnMissingValue: false);
            }
            else
            {
                vk.SetValue("LegacyDisable", "", RegistryValueKind.String);
            }
            e.Enabled = enabled;
            return (true, enabled
                ? $"已還原「{e.DisplayName}」，右鍵選單將再次顯示（可能需重開檔案總管）。"
                : $"已隱藏「{e.DisplayName}」，可隨時還原。");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "沒有權限修改此登錄機碼；請以系統管理員身分執行曦覽。");
        }
        catch (Exception ex)
        {
            return (false, "修改失敗：" + ex.Message);
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
