using System.Xml.Linq;

namespace XinSpect;

/// <summary>一件會在登入或開機時自動執行的排程工作（由工作定義 XML 解析出來）。</summary>
public sealed class ScheduledTaskStartupInfo
{
    /// <summary>工作排程器裡的完整路徑，例如 <c>\Microsoft\Windows\UpdateOrchestrator\Reboot</c>。</summary>
    public string TaskPath { get; init; } = "";

    /// <summary>路徑最後一段（顯示用）。</summary>
    public string Name { get; init; } = "";

    /// <summary>觸發時機的繁中描述（登入時／開機時／兩者），觸發程序本身被關掉時會註明。</summary>
    public string TriggerText { get; init; } = "";

    /// <summary>要執行的指令（含參數）；只有 COM 處理常式時寫出 CLSID。</summary>
    public string Command { get; init; } = "";

    /// <summary>工作本身是否啟用（工作定義的 <c>Settings/Enabled</c>）。</summary>
    public bool Enabled { get; init; }

    /// <summary><c>\Microsoft\</c> 底下＝Windows 自己的維護工作，不是使用者裝的東西。</summary>
    public bool SystemBuiltIn { get; init; }
}

/// <summary>
/// 從工作排程器的工作定義 XML 認出「會在登入或開機時自動跑」的工作（純函式，單元測試涵蓋）。
/// <para>
/// 為什麼需要這一支：工作管理員的「啟動」分頁和登錄 Run 機碼<b>都看不到排程工作</b>，
/// 而很多常駐更新程式（各家的 Updater、雲端硬碟的背景更新）正是靠登入觸發的排程工作啟動的。
/// 少了這一塊，「開機到底自動跑了什麼」這個問題就答不完整。
/// </para>
/// <para>
/// 解析刻意<b>不綁命名空間</b>——只比對元素的 LocalName。工作定義的 schema 版本從 1.0 到 1.6 都在流通，
/// 綁死命名空間會在某些機器上整批解析不到，那比看錯一件工作更糟。
/// </para>
/// </summary>
public static class ScheduledTaskStartup
{
    /// <summary>是不是 Windows 自己的維護工作。</summary>
    public static bool IsSystemBuiltIn(string? taskPath)
        => taskPath is { Length: > 0 } p && p.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);

    /// <summary>指令與參數併成一行；兩者都空回空字串。</summary>
    public static string CommandText(string? command, string? arguments)
    {
        string c = (command ?? "").Trim(), a = (arguments ?? "").Trim();
        if (c.Length == 0) return a;
        return a.Length == 0 ? c : c + " " + a;
    }

    /// <summary>
    /// 解析一份工作定義。<b>沒有登入或開機觸發程序就回 <c>null</c></b>——這一支只管開機自啟這件事，
    /// 每天定時跑的維護工作不屬於這個範圍。XML 壞掉也回 <c>null</c>，不讓一個壞檔擋掉整份清單。
    /// </summary>
    public static ScheduledTaskStartupInfo? Parse(string? xml, string taskPath)
    {
        if (xml is not { Length: > 0 }) return null;

        XElement root;
        try { root = XDocument.Parse(xml).Root ?? throw new InvalidOperationException("沒有根節點"); }
        catch { return null; }

        var triggers = Kids(root, "Triggers").FirstOrDefault();
        if (triggers is null) return null;

        bool logon = false, boot = false, logonOff = false, bootOff = false;
        foreach (var t in triggers.Elements())
        {
            bool on = !IsFalse(Val(t, "Enabled"));
            switch (t.Name.LocalName)
            {
                case "LogonTrigger": if (on) logon = true; else logonOff = true; break;
                case "BootTrigger": if (on) boot = true; else bootOff = true; break;
            }
        }
        if (!logon && !boot && !logonOff && !bootOff) return null;

        var settings = Kids(root, "Settings").FirstOrDefault();
        return new ScheduledTaskStartupInfo
        {
            TaskPath = taskPath,
            Name = LastSegment(taskPath),
            TriggerText = TriggerLabel(logon, boot, logonOff, bootOff),
            Command = ActionText(root),
            Enabled = !IsFalse(Val(settings, "Enabled")),
            SystemBuiltIn = IsSystemBuiltIn(taskPath),
        };
    }

    /// <summary>觸發時機的說法。全部觸發程序都被關掉時要講出來，否則「啟用中」會看起來自相矛盾。</summary>
    private static string TriggerLabel(bool logon, bool boot, bool logonOff, bool bootOff)
    {
        if (logon && boot) return "登入時＋開機時";
        if (logon) return "登入時";
        if (boot) return "開機時";
        if (logonOff && bootOff) return "登入時＋開機時（觸發程序已停用）";
        return logonOff ? "登入時（觸發程序已停用）" : "開機時（觸發程序已停用）";
    }

    /// <summary>第一個 Exec 動作；沒有 Exec 就退而說明是 COM 處理常式，兩者都沒有就如實說沒有。</summary>
    private static string ActionText(XElement root)
    {
        var actions = Kids(root, "Actions").FirstOrDefault();
        if (actions is not null)
        {
            if (Kids(actions, "Exec").FirstOrDefault() is { } exec)
            {
                string cmd = CommandText(Val(exec, "Command"), Val(exec, "Arguments"));
                if (cmd.Length > 0) return cmd;
            }
            if (Kids(actions, "ComHandler").FirstOrDefault() is { } com)
            {
                string id = Val(com, "ClassId");
                return id.Length > 0 ? "COM 處理常式 " + id : "COM 處理常式";
            }
        }
        return "（工作定義裡沒有可執行的動作）";
    }

    private static string LastSegment(string path)
    {
        int i = path.LastIndexOf('\\');
        return i >= 0 && i + 1 < path.Length ? path[(i + 1)..] : path;
    }

    private static IEnumerable<XElement> Kids(XElement? e, string localName)
        => e is null ? [] : e.Elements().Where(c => c.Name.LocalName == localName);

    private static string Val(XElement? e, string localName)
        => Kids(e, localName).FirstOrDefault()?.Value.Trim() ?? "";

    private static bool IsFalse(string s) => string.Equals(s, "false", StringComparison.OrdinalIgnoreCase);
}
