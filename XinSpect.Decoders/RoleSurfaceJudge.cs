using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>一個已安裝的伺服器角色或選用功能。</summary>
/// <param name="Name">功能名稱（Windows 的內部名稱，跨語言穩定）。</param>
/// <param name="DisplayName">顯示名稱。</param>
/// <param name="Installed">是否已安裝／啟用。</param>
/// <param name="IsServerRole">是否屬於伺服器角色類（相對於一般用戶端功能）。</param>
/// <param name="ServicesRunning">這個功能帶起來且正在執行的服務數；-1＝未查。</param>
/// <param name="ServicesStopped">已安裝但未執行的服務數；-1＝未查。</param>
public readonly record struct FeatureEntry(
    string Name, string DisplayName, bool Installed, bool IsServerRole,
    int ServicesRunning, int ServicesStopped);

/// <summary>
/// 已安裝角色與功能的判讀：把「裝了但沒在用」的東西挑出來，並說明它們的代價。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼這是攻擊面而不是雜項：</b>Windows 的伺服器角色與選用功能只要安裝了就佔三樣成本——
/// 磁碟空間、開機與維護時間，以及<b>可被利用的程式碼面積</b>。沒在用的角色不會因為「沒開」就沒有風險：
/// 它的檔案在、服務定義在、更新要跟著裝、而一旦有服務被觸發或設成自動啟動，它就在跑。
/// </para>
/// <para>
/// <b>本判讀不建議移除任何東西。</b>「這個角色對您沒用」是只有使用者知道的事——
/// 一台檔案伺服器的 File Server 角色是它的用途，不是多餘。本判讀只陳述三件可觀測的事實：
/// 裝了什麼、其中哪些是伺服器角色類、以及這些角色的服務現在是跑著還是停著。
/// </para>
/// <para>
/// <b>「已安裝但服務沒在跑」不等於「沒有風險」。</b>服務可能被任何觸發條件拉起來
/// （其他服務相依、RPC 呼叫、排程工作）。判讀會明說這一點，不讓它被讀成「閒置無害」。
/// </para>
/// </remarks>
public static class RoleSurfaceJudge
{
    /// <summary>判讀結果。</summary>
    /// <param name="Headline">一行結論。</param>
    /// <param name="InstalledRoles">已安裝的伺服器角色名稱。</param>
    /// <param name="RunningCount">其中服務正在執行的角色數。</param>
    /// <param name="Evidence">依據。</param>
    /// <param name="Attention">有角色裝了且服務在跑（值得看一眼，不是錯誤）。</param>
    public readonly record struct Verdict(
        IReadOnlyList<string> InstalledRoles, int RunningCount, string Headline,
        string Evidence, bool Attention);

    /// <summary>判讀一組功能清單。</summary>
    [SpecRef("Microsoft Windows 伺服器角色與選用功能的安裝狀態（Win32_OptionalFeature.InstallState；伺服器 SKU 另經 Get-WindowsFeature 的 Installed 欄位）。角色安裝後的程式碼面積與服務相依性屬 Windows 服務架構的既定行為（服務可由相依、RPC 或觸發條件啟動）。判讀僅陳述可觀測的安裝與服務狀態，不建議移除。")]
    public static Verdict Judge(IReadOnlyList<FeatureEntry> features)
    {
        if (features.Count == 0)
            return new Verdict([], 0, "—（讀不到功能清單）",
                "選用功能與角色的查詢沒有回報任何項目——無從判讀。", false);

        var installed = features.Where(f => f.Installed).ToList();
        if (installed.Count == 0)
            return new Verdict([], 0, "沒有安裝任何選用功能或伺服器角色",
                $"查詢了 {features.Count} 項，全部回報未安裝。這在精簡安裝的用戶端系統上是正常的。", false);

        var roles = installed.Where(f => f.IsServerRole).ToList();
        int running = roles.Count(r => r.ServicesRunning > 0);

        if (roles.Count == 0)
            return new Verdict([], 0,
                $"已安裝 {installed.Count} 項功能，其中沒有伺服器角色類",
                $"查詢了 {features.Count} 項，{installed.Count} 項已安裝，"
                + $"全部屬於一般用戶端功能（{string.Join("、", installed.Take(6).Select(f => f.DisplayName))}"
                + (installed.Count > 6 ? "…" : "") + "）。"
                + "一般用戶端功能的攻擊面比伺服器角色小，但同樣佔用磁碟與更新成本。", false);

        string names = string.Join("、", roles.Select(r => r.DisplayName));
        string runningText = running > 0
            ? $"其中 {running} 個角色有服務正在執行（{string.Join("、", roles.Where(r => r.ServicesRunning > 0).Select(r => r.DisplayName))}）"
            : "這些角色的服務目前都沒有在執行";

        string stopped = roles.Count - running > 0
            ? $"另有 {roles.Count - running} 個角色的服務目前停著——"
              + "但服務可以由相依關係、RPC 呼叫或觸發條件被拉起來，"
              + "「沒在跑」不等於「不會跑」，也不等於「沒有風險」。"
            : "";

        return new Verdict(roles.Select(r => r.DisplayName).ToList(), running,
            $"已安裝 {roles.Count} 個伺服器角色（{names}）",
            $"查詢了 {features.Count} 項，{installed.Count} 項已安裝，其中 {roles.Count} 個屬於伺服器角色類。"
            + $"{runningText}。{stopped}"
            + "安裝了的角色會佔用磁碟空間、拉長更新與維護時間，並增加可被利用的程式碼面積——"
            + "這是它們的成本，不是錯誤。哪一個角色對這台機器有用只有使用者知道，"
            + "本判讀不建議移除任何項目。",
            running > 0);
    }

    /// <summary>
    /// 已知的伺服器角色名稱（Windows 內部名稱）。不在這份清單裡的功能一律算一般用戶端功能——
    /// 這是保守的預設：寧可少報成角色，也不要把它當成一般功能而低估。
    /// </summary>
    [SpecRef("Microsoft Windows Server 角色與功能的內部名稱（Get-WindowsFeature／DISM 的功能名稱）：AD-Domain-Services、DNS、DHCP、FileAndStorage-Services、FS-FileServer、FS-Data-Deduplication、FS-iSCSITarget-Server、Hyper-V、Failover-Clustering、Multipath-IO、Storage-Replica、Data-Center-Bridging、Windows-Server-Backup、Containers、Web-Server、NPAS、RemoteAccess、Remote-Desktop-Services、Print-Services、WSUS、UpdateServices。清單為本程式的判讀約定，未收錄者保守視為一般功能。")]
    public static bool IsServerRoleName(string name)
    {
        foreach (string role in new[]
        {
            "AD-Domain-Services", "AD-Certificate", "AD-Federation-Services", "AD-LDS",
            "DNS", "DHCP", "FileAndStorage-Services", "FS-FileServer", "FS-Data-Deduplication",
            "FS-iSCSITarget-Server", "FS-Resource-Manager", "FS-SMBBW", "FS-NFS-Service",
            "Hyper-V", "Failover-Clustering", "Multipath-IO", "Storage-Replica",
            "Data-Center-Bridging", "Windows-Server-Backup", "Containers", "Web-Server",
            "NPAS", "RemoteAccess", "Remote-Desktop-Services", "Print-Services",
            "UpdateServices", "WSUS", "iSCSITarget-VSS-VDS", "Server-Gui-Shell",
            "ServerCore", "NET-Framework-Features", "System-DataArchiver",
        })
            if (name.Equals(role, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
