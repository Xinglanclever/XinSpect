using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace XinSpect;

/// <summary>
/// 檔案與列印伺服器角色視角（Vol2 第 SV-006、SV-007 項）：
/// <c>sv.file.shares</c>（檔案共用視角）、<c>sv.print.queues</c>（列印服務視角）。
/// </summary>
/// <remarks>
/// <para>
/// <b>與既有能力的界線（這一組最容易做重）：</b>
/// <c>sv.shares</c>（SV-006／007 早期的收口）已經列出<b>共用名稱與磁碟／印表分類</b>；
/// <c>nw.print.summary</c>（NW-012）已經數過<b>列印佇列的健康狀態</c>；
/// <c>sv.roles.services</c> 已經回答<b>Spooler／LanmanServer 服務在不在跑</b>。
/// 本項刻意不重做那三層，改問兩個<b>只有角色視角才問得出來</b>的問題：
/// 檔案共用<b>掛在哪個路徑類別、授權給誰</b>（不列路徑本身），
/// 列印<b>是不是真的有印表機在服務</b>（佇列存在不等於有裝置）。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① <b>共用路徑只報「類別」不報路徑</b>——路徑常含使用者資料夾名稱或專案目錄，
/// 但「這台機器共用的是系統磁碟還是資料磁碟」是角色事實，值得回答；
/// ② <b>不列 ACL 的個別帳號</b>——只報權限項數與是否含廣泛授權（Everyone／Users），
/// 帳號名稱一旦列出就等於把誰在什麼群組說出去；
/// ③ <b>佇列存在不等於有裝置</b>——Windows 預設會裝若干「虛擬」印表機（PDF／XPS／Fax），
/// 那些不是實體輸出裝置，因此<b>實體／虛擬分開計數</b>；
/// ④ 系統預設共用（C$／ADMIN$／IPC$）如果被關掉，那是風險面的<b>減少</b>，
/// 值得如實說出來而不是當成預設值放過。
/// </para>
/// </remarks>
public static class ServerRoleFilePrintFactsService
{
    private const string Cat = "伺服器角色";
    private const string Source = "WMI root\\CIMV2（Win32_Share／Win32_Printer／Win32_LogicalShareSecuritySetting，唯讀）";

    /// <summary>SV-006 檔案共用視角。</summary>
    public const string FileSharesKey = "sv.file.shares";
    /// <summary>SV-007 列印服務視角。</summary>
    public const string PrintQueuesKey = "sv.print.queues";

    /// <summary>需要管理員權限才讀得到的共用 ACL：讀不到時用這個標記。</summary>
    public const uint ErrorAccessDenied = 5;

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 一個共用：名稱、型別（0 磁碟／1 印表／3 IPC）、路徑類別（見 <see cref="PathClass"/>）、
    /// ACE 數、是否含廣泛授權。<b>不含路徑本身與個別帳號。</b>
    /// </summary>
    public sealed record ShareRow(string Name, int Type, string PathClass, int AceCount, bool BroadGrant);

    /// <summary>
    /// 一台印表機：是否為實體輸出裝置（虛擬印表機如 PDF／XPS／Fax 為 false）、
    /// 是否共用、是否需要離線。
    /// </summary>
    public sealed record PrinterRow(bool PhysicalDevice, bool Shared, bool Offline);

    /// <summary>（共用清單, ACL 可讀的共用數）。</summary>
    public sealed record FileSharesState(IReadOnlyList<ShareRow> Shares, int AclReadable);

    public delegate FileSharesState? FileSharesProbe();
    public delegate IReadOnlyList<PrinterRow>? PrintersProbe();

    // ── 路徑類別（不揭露路徑本身）─────────────────────────────────────────

    /// <summary>系統磁碟根（例如 <c>C:\</c>）。</summary>
    public const string PathClassSystemRoot = "系統磁碟根";
    /// <summary>非系統的固定磁碟。</summary>
    public const string PathClassDataVolume = "資料磁碟";
    /// <summary>使用者設定檔下的目錄。</summary>
    public const string PathClassUserProfile = "使用者目錄";
    /// <summary>IPC／具名管道（無檔案路徑語意）。</summary>
    public const string PathClassIpc = "IPC";
    /// <summary>路徑讀不到或型別沒有路徑語意。</summary>
    public const string PathClassUnknown = "未取得";

    /// <summary>
    /// 把共用路徑歸類。<b>保留的只有類別</b>——原字串永遠不進事實值。
    /// 系統磁碟以 <see cref="Environment.SpecialFolder.Windows"/> 的磁碟機代號判定，
    /// 不比對寫死的「C:」（機器上系統碟可能是別的代號）。
    /// </summary>
    public static string PathClass(int shareType, string? path)
    {
        if ((shareType & 0xFF) == 3) return PathClassIpc;
        if (string.IsNullOrWhiteSpace(path)) return PathClassUnknown;

        string systemDrive = System.IO.Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "";
        string root = System.IO.Path.GetPathRoot(path) ?? "";
        if (root.Length == 0) return PathClassUnknown;

        // 使用者目錄判定只比對「路徑上層是不是 Users」——不回傳任何路徑片段。
        string usersDir = System.IO.Path.Combine(root, "Users");
        if (path.StartsWith(usersDir, StringComparison.OrdinalIgnoreCase)) return PathClassUserProfile;

        return root.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)
            ? PathClassSystemRoot
            : PathClassDataVolume;
    }

    /// <summary>
    /// 這個共用的授權算不算「廣泛」。<b>只看得到 ACE 的信任者是否為 WellKnownSidType
    /// 的 Everyone／Users／Authenticated Users 這一類</b>——個別帳號名稱不讀、不列。
    /// </summary>
    public static bool IsBroadGrant(IEnumerable<string> trusteeSids) =>
        trusteeSids.Any(sid => sid.Length > 0 && sid.StartsWith("S-1-1-0", StringComparison.Ordinal)   // Everyone
                            || sid.StartsWith("S-1-5-32-545", StringComparison.Ordinal)               // BUILTIN\Users
                            || sid.StartsWith("S-1-5-11", StringComparison.Ordinal));                 // Authenticated Users

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        FileSharesProbe? shares = null,
        PrintersProbe? printers = null)
        =>
        [
            FileSharesFact(at, shares),
            PrintQueuesFact(at, printers),
        ];

    // ── SV-006 檔案共用視角 ───────────────────────────────────────────────

    public static HardwareFact FileSharesFact(DateTimeOffset at, FileSharesProbe? probe = null)
    {
        FileSharesState? s;
        try { s = (probe ?? ReadFileShares)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, FileSharesKey, "檔案共用視角", FactAvailability.ReadError,
                "檔案共用列舉失敗——讀不到就是不猜（不畫成 0 個共用）");

        var v = s;
        var disk = v.Shares.Where(x => (x.Type & 0xFF) == 0).ToList();
        if (disk.Count == 0)
            return new HardwareFact(FileSharesKey, Cat, "檔案共用視角",
                "沒有磁碟共用（只有 IPC 或完全沒有共用）。" +
                "「沒有共用」本身是<b>收斂攻擊面</b>的狀態，不是缺陷——" +
                "很多伺服器刻意只提供服務而不開放檔案共用。" +
                "<b>系統預設共用（C$／ADMIN$）若不在清單裡，代表它們被關掉了</b>，" +
                "這是風險面的減少，本項如實陳述不當成異常。",
                "個", Source, FactTrustLevel.Reported, false, at, 0);

        var byClass = disk.GroupBy(x => x.PathClass, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} {g.Count()}")
            .ToList();
        int broad = disk.Count(x => x.BroadGrant);
        int admin = disk.Count(x => x.Name.EndsWith("$", StringComparison.Ordinal));
        int aclUnknown = disk.Count - v.AclReadable;

        string aclText = aclUnknown > 0
            ? $"・其中 {aclUnknown} 個共用的 ACL <b>讀不到</b>（需要系統管理員）——" +
              "那幾個的授權廣度<b>未經檢查</b>，不是「沒有廣泛授權」"
            : "";
        string broadText = broad == 0
            ? "沒有發現廣泛授權（Everyone／Users／Authenticated Users）"
            : $"<b>{broad} 個含廣泛授權</b>（Everyone／Users／Authenticated Users）——" +
              "廣泛授權是風險面不是漏洞判決：內部唯讀共享用 Users 是常見且合理的配置";

        return new HardwareFact(FileSharesKey, Cat, "檔案共用視角",
            $"磁碟共用 {disk.Count} 個（預設系統共用 {admin} 個）・依路徑類別：" +
            $"{string.Join("、", byClass)}・{broadText}{aclText}。" +
            "<b>只報路徑類別不報路徑</b>：路徑常含使用者資料夾名或專案目錄，" +
            "但「共用的是系統磁碟還是資料磁碟」是角色事實。" +
            "<b>不列個別帳號</b>：只報 ACE 數與是否含廣泛授權——" +
            "帳號清單一旦列出就等於把誰在什麼群組說出去。" +
            "共用存在不等於網路可及（防火牆與 SMB 簽章另判）。",
            "個", Source, FactTrustLevel.Reported, false, at, disk.Count);
    }

    // ── SV-007 列印服務視角 ───────────────────────────────────────────────

    public static HardwareFact PrintQueuesFact(DateTimeOffset at, PrintersProbe? probe = null)
    {
        IReadOnlyList<PrinterRow>? rows;
        try { rows = (probe ?? ReadPrinters)(); }
        catch { rows = null; }

        if (rows is null)
            return Unavailable(at, PrintQueuesKey, "列印服務視角", FactAvailability.ReadError,
                "印表機列舉失敗——讀不到就是不猜（不畫成 0 台）");

        if (rows.Count == 0)
            return new HardwareFact(PrintQueuesKey, Cat, "列印服務視角",
                "沒有安裝任何印表機。若 Spooler 服務仍在執行，這是「服務在跑但沒有裝置」——" +
                "不等於列印角色可用，也不等於服務該關（見 sv.roles.services 的服務狀態）。",
                "台", Source, FactTrustLevel.Reported, false, at, 0);

        int physical = rows.Count(r => r.PhysicalDevice);
        int shared = rows.Count(r => r.Shared);
        int offline = rows.Count(r => r.Offline);

        // 這一句是本項存在的理由：佇列數會被虛擬印表機撐大。
        string deviceText = physical == 0
            ? $"<b>沒有任何實體輸出裝置</b>（{rows.Count} 台全是虛擬印表機：PDF／XPS／Fax 這一類）——" +
              "「有印表機」與「能印東西」在這裡是兩件事"
            : $"其中實體裝置 {physical} 台、虛擬印表機 {rows.Count - physical} 台";

        return new HardwareFact(PrintQueuesKey, Cat, "列印服務視角",
            $"印表機 {rows.Count} 台・{deviceText}・共用 {shared} 台・離線 {offline} 台。" +
            "<b>佇列存在不等於有裝置</b>：Windows 預設會裝若干虛擬印表機" +
            "（列印成 PDF、XPS、Fax），它們在佇列清單裡與真印表機長得一樣，" +
            "但沒有紙、沒有碳粉、也不會卡紙。把兩者混在一起數，" +
            "會讓一台只有 PDF 印表機的機器看起來像列印伺服器。" +
            "「離線」是當下狀態不是故障——省電或未連線的網路印表機常標離線。",
            "台", Source, FactTrustLevel.Reported, false, at, physical);
    }

    // ── 讀取層 ────────────────────────────────────────────────────────────

    /// <summary>
    /// WMI 通路：共用清單。<b>路徑只用於歸類後丟棄</b>，ACL 走
    /// <c>Win32_LogicalShareSecuritySetting</c>（非管理員會拿到存取被拒 → 計入 readError 而非 0）。
    /// </summary>
    internal static FileSharesState? ReadFileShares()
    {
        var list = new List<ShareRow>();
        int aclReadable = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name, Type, Path FROM Win32_Share");
            foreach (ManagementObject o in searcher.Get())
                using (o)
                {
                    string name = o["Name"] as string ?? "";
                    if (name.Length == 0) continue;
                    int type = o["Type"] is { } t ? Convert.ToInt32(t) : 0;
                    string? path = o["Path"] as string;

                    var (aces, broad, readable) = ReadShareAcl(name);
                    if (readable) aclReadable++;
                    list.Add(new ShareRow(name, type, PathClass(type, path), aces, broad));
                }
        }
        catch (ManagementException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        return new FileSharesState(list, aclReadable);
    }

    /// <summary>
    /// 讀單一共用的 ACL。<b>只數 ACE 條數與判廣泛授權</b>；
    /// 信任者以 SID 判定（見 <see cref="IsBroadGrant"/>），帳號<b>名稱</b>不取用。
    /// </summary>
    internal static (int Aces, bool Broad, bool Readable) ReadShareAcl(string shareName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT AccessMask, Trustee FROM Win32_LogicalShareSecuritySetting " +
                "WHERE Name = '" + shareName.Replace("'", "''") + "'");
            foreach (ManagementObject o in searcher.Get())
                using (o)
                {
                    if (o["Trustee"] is not ManagementBaseObject[] trustees)
                        return (0, false, true);   // 讀得到但沒有 ACE：不是「讀不到」
                    var sids = new List<string>(trustees.Length);
                    foreach (var t in trustees)
                        using (t)
                            if (t["SIDString"] is string sid) sids.Add(sid);
                    return (trustees.Length, IsBroadGrant(sids), true);
                }
            return (0, false, true);
        }
        catch (ManagementException) { return (0, false, false); }   // 常見：非管理員
        catch (UnauthorizedAccessException) { return (0, false, false); }
    }

    /// <summary>
    /// WMI 通路：印表機清單。<b>實體／虛擬以驅動名稱與連接埠形式判定</b>——
    /// 沒有硬體 ID 可查，所以判準要保守：明確看得出是虛擬的（PDF／XPS／Fax／OneNote）
    /// 才算虛擬，其餘一律當成實體（寧可高估實體，也不要讓真印表機被歸成虛擬）。
    /// </summary>
    internal static IReadOnlyList<PrinterRow>? ReadPrinters()
    {
        var list = new List<PrinterRow>();
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name, DriverName, PortName, Shared, WorkOffline FROM Win32_Printer");
            foreach (ManagementObject o in searcher.Get())
                using (o)
                {
                    string name = o["Name"] as string ?? "";
                    if (name.Length == 0) continue;
                    string driver = o["DriverName"] as string ?? "";
                    string port = o["PortName"] as string ?? "";
                    bool shared = o["Shared"] is bool b && b;
                    bool offline = o["WorkOffline"] is bool ob && ob;
                    list.Add(new PrinterRow(IsPhysicalPrinter(name, driver, port), shared, offline));
                }
        }
        catch (ManagementException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        return list;
    }

    /// <summary>
    /// 是不是實體輸出裝置。只有<b>明確看得出虛擬</b>的才回 <c>false</c>：
    /// 驅動或名稱含 PDF／XPS／Fax／OneNote／傳真字樣，或連接埠是 <c>PORTPROMPT:</c>／<c>nul:</c>。
    /// </summary>
    public static bool IsPhysicalPrinter(string name, string driver, string port)
    {
        string hay = (name + " " + driver).ToUpperInvariant();
        string p = port.ToUpperInvariant();
        string[] virtualMarkers = ["PDF", "XPS", "FAX", "傳真", "ONENOTE", "MICROSOFT PRINT TO"];
        if (virtualMarkers.Any(m => hay.Contains(m, StringComparison.Ordinal))) return false;
        if (p is "PORTPROMPT:" or "NUL:" or "SHRFAX:") return false;
        return true;
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
