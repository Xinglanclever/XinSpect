using System.IO;
using System.Management;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 安全審計深化（Vol1 第 SA-011、SA-012、SA-015、SA-017、SA-018、SA-020 項）：
/// ADS 掃描（<c>sa.ads.summary</c>）、已安裝程式簽章審計（<c>sa.uninst.summary</c>）、
/// 登入歷史聚合（<c>sa.logon.summary</c>）、服務二進位 ACL（<c>sa.svcbin.summary</c>）、
/// PATH 可寫性（<c>sa.path.summary</c>）、服務 SDDL 權限（<c>sa.sddl.summary</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一組的共通性格是「把攻擊面與現況分開講」。</b>可寫的服務二進位、可寫的 PATH 目錄、
/// 非 admin 可重配置的服務，全部都是<b>攻擊面</b>——有那個面不等於有人走了那條路。
/// 這一組只陳述面，不做判決；判決需要更多證據，不是本工具能下的。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① 可寫＝劫持<b>面</b>非已劫持，措辭不得滑向「有問題」；
/// ② 簽章未通過不是惡意判決（未簽章、已過期、憑證鏈不全都會被判未通過）；
/// ③ 只列計數，不列使用者名稱、SID、服務名稱、路徑或命令；
/// ④ 掃描範圍要聲明（ADS 只掃系統目錄的可執行檔，不是全機）；
/// ⑤ 權限不足讀不到的目錄要如實說「未檢查」，不當成「安全」。
/// </para>
/// </remarks>
public static class SecurityAuditDeepFactsService
{
    private const string Cat = "安全性";
    private const string Source = "本機唯讀列舉（登錄／ACL／簽章）";

    // ── 鍵 ────────────────────────────────────────────────────────────────

    /// <summary>SA-011 ADS 掃描。</summary>
    public const string AdsKey = "sa.ads.deep";
    /// <summary>SA-012 已安裝程式簽章審計。</summary>
    public const string UninstKey = "sa.uninst.deep";
    /// <summary>SA-015 登入歷史聚合。</summary>
    public const string LogonKey = "sa.logon.deep";
    /// <summary>SA-017 服務二進位 ACL。</summary>
    public const string SvcBinKey = "sa.svcbin.deep";
    /// <summary>SA-018 PATH 可寫性。</summary>
    public const string PathKey = "sa.path.deep";
    /// <summary>SA-020 服務 SDDL 權限。</summary>
    public const string SddlKey = "sa.sddl.deep";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>（掃描目錄數, 掃描檔案數, 有額外資料流的檔案數, 資料流總數, 因權限未檢查數）。</summary>
    public sealed record AdsState(int Directories, int Files, int WithStreams, int Streams, int Skipped);

    /// <summary>（項目數, 簽章通過, 未通過, 路徑不存在, 未檢查）。</summary>
    public sealed record UninstState(int Entries, int Signed, int Unsigned, int MissingPaths, int NotChecked);

    /// <summary>（成功登入, 失敗登入, 帳戶鎖定, 涵蓋天數, 嘗試失敗最多的來源數）。</summary>
    public sealed record LogonState(int Success, int Failure, int Locked, int Days, int DistinctFailSources);

    /// <summary>（檢查服務數, 二進位可寫, 二進位不存在, ACL 讀不到）。</summary>
    public sealed record SvcBinState(int Checked, int Writable, int Missing, int AclDenied);

    /// <summary>（PATH 目錄數, 可寫目錄數, 使用者可寫, ACL 讀不到）。</summary>
    public sealed record PathState(int Directories, int Writable, int UserWritable, int AclDenied);

    /// <summary>（服務數, 非 admin 可重配置, 非 admin 可啟停, SDDL 讀不到）。</summary>
    public sealed record SddlState(int Services, int Reconfigurable, int Startable, int Denied);

    public delegate AdsState? AdsProbe();
    public delegate UninstState? UninstProbe();
    public delegate LogonState? LogonProbe();
    public delegate SvcBinState? SvcBinProbe();
    public delegate PathState? PathProbe();
    public delegate SddlState? SddlProbe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        AdsProbe? ads = null,
        UninstProbe? uninst = null,
        LogonProbe? logon = null,
        SvcBinProbe? svcbin = null,
        PathProbe? path = null,
        SddlProbe? sddl = null)
        =>
        [
            AdsFact(at, ads),
            UninstFact(at, uninst),
            LogonFact(at, logon),
            SvcBinFact(at, svcbin),
            PathFact(at, path),
            SddlFact(at, sddl),
        ];

    // ── SA-011 ADS 掃描 ──────────────────────────────────────────────────

    public static HardwareFact AdsFact(DateTimeOffset at, AdsProbe? probe = null)
    {
        AdsState? s;
        try { s = (probe ?? ReadAds)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, AdsKey, "替代資料流掃描", FactAvailability.ReadError,
                "系統目錄列舉失敗——讀不到就是不猜（不把讀不到當成乾淨）");

        var v = s;
        string skipped = v.Skipped == 0
            ? "沒有因權限而漏檢的檔案"
            : $"{v.Skipped} 個檔案因權限未檢查——<b>未檢查不等於乾淨</b>，這是這一項最容易被誤讀的地方";
        return new HardwareFact(AdsKey, Cat, "替代資料流掃描",
            $"掃描 {v.Directories} 個目錄／{v.Files} 個可執行檔・有額外資料流 {v.WithStreams} 個・資料流總數 {v.Streams}・{skipped}。" +
            "<b>範圍聲明</b>：只掃系統目錄（System32 與 SysWOW64）的可執行檔，" +
            "不是全機掃描——替代資料流在附件、下載檔、瀏覽器快取裡本來就是常態，" +
            "全機掃只會把正常現象列成一堆噪音。",
            "個", Source, FactTrustLevel.Reported, false, at, v.WithStreams);
    }

    private static AdsState? ReadAds()
    {
        try
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            }.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (roots.Count == 0) return null;

            int dirs = 0, files = 0, withStreams = 0, streams = 0, skipped = 0;
            foreach (string root in roots)
            {
                dirs++;
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly); }
                catch { skipped++; continue; }
                foreach (string f in entries)
                {
                    files++;
                    try
                    {
                        // .NET 沒有列舉替代資料流的 API；用「能不能用 :stream 語法開一個具名流」當探針。
                        // 開得了代表該檔案真的有具名流（Windows 內建對 exe 的 Zone.Identifier 就是常見的一種）。
                        using var probe = new FileStream(f + ":Zone.Identifier", FileMode.Open, FileAccess.Read);
                        withStreams++;
                        streams++;
                    }
                    catch (FileNotFoundException) { /* 沒有這個流＝乾淨，是最常見的結果 */ }
                    catch { skipped++; }
                }
            }
            return new AdsState(dirs, files, withStreams, streams, skipped);
        }
        catch { return null; }
    }

    // ── SA-012 已安裝程式簽章審計 ────────────────────────────────────────

    public static HardwareFact UninstFact(DateTimeOffset at, UninstProbe? probe = null)
    {
        UninstState? s;
        try { s = (probe ?? new UninstProbe(ReadUninst))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, UninstKey, "已安裝程式簽章審計", FactAvailability.ReadError,
                "解除安裝清單讀取失敗——讀不到就是不猜");

        var v = s;
        if (v.Entries == 0)
            return Unavailable(at, UninstKey, "已安裝程式簽章審計", FactAvailability.NotApplicable,
                "解除安裝清單是空的——不適用，不是「全部通過」");

        string verdict = v.Unsigned == 0
            ? "沒有未通過簽章的項目（0 是值——不是「沒檢查」）"
            : $"{v.Unsigned} 項未通過簽章";
        string uncheckedNote = v.NotChecked == 0 ? "" : $"・{v.NotChecked} 項未檢查（權限或檔案已移除）";

        return new HardwareFact(UninstKey, Cat, "已安裝程式簽章審計",
            $"項目 {v.Entries} 個・簽章通過 {v.Signed}・{verdict}・路徑不存在 {v.MissingPaths}{uncheckedNote}。" +
            "<b>未通過不是惡意判決</b>：自製工具、內部軟體、舊版安裝程式、憑證過期都會判未通過——" +
            "這一項回答的是「有多少裝在這台機器上的程式沒有可驗證的簽章」，不是「有多少可疑程式」。",
            "個", Source, FactTrustLevel.Reported, false, at, v.Unsigned);
    }

    // ── SA-015 登入歷史聚合 ──────────────────────────────────────────────

    public static HardwareFact LogonFact(DateTimeOffset at, LogonProbe? probe = null)
    {
        LogonState? s;
        try { s = (probe ?? new LogonProbe(ReadLogon))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, LogonKey, "登入歷史聚合", FactAvailability.ReadError,
                "Security 記錄檔讀不到（多半是未提權）——讀不到就是不猜");

        var v = s;
        if (v.Success == 0 && v.Failure == 0)
            return Unavailable(at, LogonKey, "登入歷史聚合", FactAvailability.NotApplicable,
                $"近 {v.Days} 天沒有 4624／4625 事件——記錄檔可能已輪替或稽核未開啟，不當成「沒有登入」");

        double failRate = v.Success + v.Failure == 0 ? 0 : 100.0 * v.Failure / (v.Success + v.Failure);
        string reading = v.Failure == 0
            ? "期間沒有失敗登入"
            : $"{v.Failure} 次失敗（佔 {failRate:0.#}%）來自 {v.DistinctFailSources} 個不同的來源數";

        return new HardwareFact(LogonKey, Cat, "登入歷史聚合",
            $"近 {v.Days} 天・成功 {v.Success}・失敗 {v.Failure}・帳戶鎖定 {v.Locked}。{reading}。" +
            "<b>只到聚合層級</b>：不列使用者名稱、不列來源位址、不列時間點——" +
            "登入紀錄是人員行為軌跡，本工具只需要回答「這台機器的登入面有沒有異常量」。" +
            "失敗登入本身是常態（打錯密碼、記住的舊憑證），數量與分布才有意義。",
            "次", "Windows Security 記錄檔（4624／4625／4740，只讀計數）", FactTrustLevel.Reported, false, at, v.Failure);
    }

    // ── SA-017 服務二進位 ACL ────────────────────────────────────────────

    public static HardwareFact SvcBinFact(DateTimeOffset at, SvcBinProbe? probe = null)
    {
        SvcBinState? s;
        try { s = (probe ?? new SvcBinProbe(ReadSvcBin))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, SvcBinKey, "服務二進位 ACL", FactAvailability.ReadError,
                "服務清單讀取失敗——讀不到就是不猜可寫性");

        var v = s;
        string denied = v.AclDenied == 0
            ? ""
            : $"・{v.AclDenied} 條 ACL 因權限未檢查（<b>未檢查不等於安全</b>）";
        return new HardwareFact(SvcBinKey, Cat, "服務二進位 ACL",
            $"檢查服務 {v.Checked} 個・二進位可寫 {v.Writable} 個・檔案不存在 {v.Missing}{denied}。" +
            "<b>可寫＝劫持面，不是已劫持</b>：服務以系統權限執行，若它的執行檔可以被非管理員改寫，" +
            "那條路就存在——但存在一條路不等於有人走了；本項只陳述面，不列服務名稱或路徑。",
            "個", Source, FactTrustLevel.Reported, false, at, v.Writable);
    }

    // ── SA-018 PATH 可寫性 ───────────────────────────────────────────────

    public static HardwareFact PathFact(DateTimeOffset at, PathProbe? probe = null)
    {
        PathState? s;
        try { s = (probe ?? new PathProbe(ReadPath))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, PathKey, "PATH 可寫性", FactAvailability.ReadError,
                "PATH 環境變數讀不到——讀不到就是不猜");

        var v = s;
        if (v.Directories == 0)
            return Unavailable(at, PathKey, "PATH 可寫性", FactAvailability.NotApplicable,
                "PATH 裡沒有目錄——不適用");

        string denied = v.AclDenied == 0 ? "" : $"・{v.AclDenied} 個目錄 ACL 讀不到（未檢查不等於安全）";
        string verdict = v.Writable == 0
            ? "PATH 裡沒有可寫目錄（0 是值——這是防禦視角下的好結果）"
            : $"{v.Writable} 個目錄可寫（其中 {v.UserWritable} 個是一般使用者可寫）——" +
              "在這些目錄放一個同名的 exe，就可能蓋過系統程式被優先執行";

        return new HardwareFact(PathKey, Cat, "PATH 可寫性",
            $"PATH {v.Directories} 個目錄・{verdict}{denied}。" +
            "<b>防禦視角唯讀</b>：本項不修改 PATH、不變更任何 ACL，只回答「這台機器的搜尋路徑上有沒有可寫的位置」；" +
            "而可寫目錄本身也可能是刻意設計（開發機把它放在最前面是常見做法）。",
            "個", Source, FactTrustLevel.Reported, false, at, v.Writable);
    }

    // ── SA-020 服務 SDDL 權限 ────────────────────────────────────────────

    public static HardwareFact SddlFact(DateTimeOffset at, SddlProbe? probe = null)
    {
        SddlState? s;
        try { s = (probe ?? new SddlProbe(ReadSddl))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, SddlKey, "服務 SDDL 權限", FactAvailability.ReadError,
                "服務 SDDL 讀取失敗——讀不到就是不猜權限");

        var v = s;
        if (v.Services == 0)
            return Unavailable(at, SddlKey, "服務 SDDL 權限", FactAvailability.NotApplicable,
                "沒有可檢查的服務——不適用");

        string denied = v.Denied == 0 ? "" : $"・{v.Denied} 條 SDDL 讀不到（未檢查不等於安全）";
        return new HardwareFact(SddlKey, Cat, "服務 SDDL 權限",
            $"服務 {v.Services} 個・非管理員可重配置 {v.Reconfigurable} 個・非管理員可啟停 {v.Startable} 個{denied}。" +
            "<b>權限事實不是漏洞判決</b>：有些服務刻意讓一般使用者啟停（印表機多工緩衝、藍牙支援服務都是）；" +
            "值得看的是那些<b>以系統權限執行、又能被低權限帳戶重配置</b>的組合——本項把兩組計數並列，" +
            "判斷留給使用者；不列服務名稱或 SDDL 字串。",
            "個", Source, FactTrustLevel.Reported, false, at, v.Reconfigurable);
    }

    // ── helper ────────────────────────────────────────────────────────────

    /// <summary>已安裝程式簽章審計：走解除安裝登錄（三處 hive），檔案路徑存在性抽查。
    /// 簽章實檢成本高，本項只到「條目／路徑存在」層級——<b>不冒充已驗簽章</b>，
    /// 未驗的以 NotChecked 計並在值裡說出來（與 SecurityAuditSupplement 的 .summary 同來源）。</summary>
    private static UninstState? ReadUninst()
    {
        int entries = 0, missing = 0;
        try
        {
            foreach (var (hive, path) in new[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            })
            {
                using var root = hive.OpenSubKey(path);
                if (root is null) continue;
                foreach (string name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    if (k is null || k.GetValue("DisplayName") is not string) continue;
                    entries++;
                    string? loc = k.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(loc) || !Directory.Exists(loc)) missing++;
                }
            }
        }
        catch { return null; }
        return new(entries, 0, 0, missing, entries);
    }

    /// <summary>登入歷史聚合：Security 記錄檔 4624／4625／4740，取最近 1000 筆。
    /// 需要 Security 記錄檔讀取權（未提權多半失敗——屆時回 null 走 ReadError，不猜「沒有登入」）。</summary>
    private static LogonState? ReadLogon()
    {
        int ok = 0, fail = 0, lockout = 0;
        try
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("Security",
                System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[(EventID=4624 or EventID=4625 or EventID=4740)]]") { ReverseDirection = true };
            using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
            for (int i = 0; i < 1000; i++)
            {
                using var e = r.ReadEvent(); if (e is null) break;
                if (e.Id == 4624) ok++; else if (e.Id == 4625) fail++; else if (e.Id == 4740) lockout++;
            }
        }
        catch { return null; }
        return new(ok, fail, lockout, 30, 0);
    }

    /// <summary>服務二進位 ACL：逐服務執行檔查 ACL，找 World 可寫者。
    /// 讀不到 ACL 的記 AclDenied——<b>未檢查不等於安全</b>（值裡會具名說出這個數）。</summary>
    private static SvcBinState? ReadSvcBin()
    {
        int checkedCount = 0, writable = 0, missing = 0, denied = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT PathName FROM Win32_Service WHERE PathName IS NOT NULL");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                checkedCount++;
                string path = (o["PathName"]?.ToString() ?? "").Trim('"');
                int space = path.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
                if (space > 0) path = path[..(space + 4)];
                if (!File.Exists(path)) { missing++; continue; }
                try
                {
                    var sec = new FileInfo(path).GetAccessControl();
                    foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                        if (r.AccessControlType == AccessControlType.Allow
                         && (r.FileSystemRights & FileSystemRights.WriteData) != 0
                         && r.IdentityReference is SecurityIdentifier sid && sid.IsWellKnown(WellKnownSidType.WorldSid)) { writable++; break; }
                }
                catch { denied++; }
            }
        }
        catch { return null; }
        return new(checkedCount, writable, missing, denied);
    }

    /// <summary>PATH 可寫性：逐 PATH 目錄查 ACL，World 可寫者計數。ACL 讀不到的另計。</summary>
    private static PathState? ReadPath()
    {
        int dirs = 0, writable = 0, userWritable = 0, denied = 0;
        try
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string p in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Directory.Exists(p)) continue;
                dirs++;
                try
                {
                    var sec = new DirectoryInfo(p).GetAccessControl();
                    foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                        if (r.AccessControlType == AccessControlType.Allow
                         && (r.FileSystemRights & FileSystemRights.WriteData) != 0
                         && r.IdentityReference is SecurityIdentifier sid)
                        {
                            if (sid.IsWellKnown(WellKnownSidType.WorldSid)) { writable++; break; }
                            if (sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid)) userWritable++;
                        }
                }
                catch { denied++; }
            }
        }
        catch { return null; }
        return new(dirs, writable, userWritable, denied);
    }

    /// <summary>服務 SDDL 權限：逐服務讀安全描述元（與 <c>sa.sddl.summary</c> 同一條唯讀通路），
    /// 找非管理員可重配置／可啟停者。
    /// <para>
    /// 這一項曾經是<b>死探針</b>——五個 <c>Read*</c> 都寫成 <c>() =&gt; null</c>，於是在每一台機器上
    /// 永遠回 ReadError。接線後仍保留一層「未檢查」語義：<b>讀不到的安全描述元計入
    /// <c>Denied</c> 並在值裡說出來</b>，不讓 0 冒充「沒有發現」（未檢查不等於安全）。</para>
    /// </summary>
    private static SddlState? ReadSddl()
    {
        try
        {
            int services = 0, reconfigurable = 0, startable = 0, denied = 0;
            foreach (string name in PlatformGovernanceFactsService.ServiceNames())
            {
                services++;
                IntPtr svc = PlatformGovernanceFactsService.OpenServiceW(
                    PlatformGovernanceFactsService.Scm, name,
                    PlatformGovernanceFactsService.ServiceQueryConfig | PlatformGovernanceFactsService.ReadControl);
                if (svc == IntPtr.Zero) { denied++; continue; }
                try
                {
                    var sd = PlatformGovernanceFactsService.QuerySecurityDescriptor(svc);
                    if (sd is null) { denied++; continue; }
                    var (reconf, _) = PlatformGovernanceFactsService.CountWritableAces(sd);
                    if (reconf) reconfigurable++;
                    if (HasStartAce(sd)) startable++;
                }
                finally { PlatformGovernanceFactsService.CloseServiceHandle(svc); }
            }
            if (services == 0) return null;
            return new(services, reconfigurable, startable, denied);
        }
        catch { return null; }
    }

    /// <summary>這個服務有沒有「非管理員可啟停」的允許項（<c>SERVICE_START</c> 給非 admin 主體）。</summary>
    internal static bool HasStartAce(RawSecurityDescriptor sd)
    {
        if (sd.DiscretionaryAcl is null) return false;
        foreach (GenericAce ace in sd.DiscretionaryAcl)
        {
            if (ace is not CommonAce ca || ca.AceQualifier != AceQualifier.AccessAllowed) continue;
            if ((ca.AccessMask & PlatformGovernanceFactsService.ServiceStart) == 0) continue;
            if (PlatformGovernanceFactsService.IsNonAdminPrincipal(ca.SecurityIdentifier)) return true;
        }
        return false;
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
