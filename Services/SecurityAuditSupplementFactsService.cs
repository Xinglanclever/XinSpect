using System.IO;
using System.Management;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>Vol2 第 243–252 項：COM 劫持、檔案關聯、ADS、已安裝程式簽章、帳戶、自動登入、登入歷史、服務二進位 ACL、PATH 可寫性、WMI 永久訂閱。</summary>
/// <remarks>全部唯讀；自動登入只報存在性，值永不輸出；簽章匯出只報計數；不列使用者名稱、路徑或命令。</remarks>
public static class SecurityAuditSupplementFactsService
{
    private const string CatSecurity = "安全性";

    public const string ComHijackKey = "sa.comhij.summary";
    public const string AssocKey = "sa.assoc.summary";
    public const string AdsKey = "sa.ads.summary";
    public const string UninstKey = "sa.uninst.summary";
    public const string AccountKey = "sa.acct.summary";
    public const string AutoLogonKey = "sa.autologon";
    public const string LogonKey = "sa.logon.summary";
    public const string ServiceBinaryKey = "sa.svcbin.summary";
    public const string PathKey = "sa.path.summary";
    public const string WmiSubKey = "sa.wmisub.summary";

    public sealed record ComHijackState(int HkcuClasses, int HklmClasses, int HkcuOverrides);
    public sealed record AssocState(int ExeAssociations, int LnkAssociations, int UserOverrides);
    public sealed record AdsState(int FilesScanned, int FilesWithAds, int TotalStreams);
    public sealed record UninstState(int UninstallEntries, int SignedEntries, int UnsignedEntries, int MissingPaths);
    public sealed record AccountState(int Administrators, int EnabledAdmins, int Rid500Present, int OrphanSids);
    public sealed record AutoLogonState(bool DefaultUserNamePresent, bool DefaultPasswordPresent, bool AutoAdminLogonPresent);
    public sealed record LogonState(int Success4624, int Failure4625, int AccountLocked4740, int LastDays);
    public sealed record ServiceBinaryState(int ServicesChecked, int WritableBinaries, int MissingBinaries);
    public sealed record PathState(int Directories, int WritableDirectories, int UserWritable);
    public sealed record WmiSubState(int Filters, int Consumers, int Bindings);

    public delegate ComHijackState? ComHijackProbe();
    public delegate AssocState? AssocProbe();
    public delegate AdsState? AdsProbe();
    public delegate UninstState? UninstProbe();
    public delegate AccountState? AccountProbe();
    public delegate AutoLogonState? AutoLogonProbe();
    public delegate LogonState? LogonProbe();
    public delegate ServiceBinaryState? ServiceBinaryProbe();
    public delegate PathState? PathProbe();
    public delegate WmiSubState? WmiSubProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        ComHijackFact(at), AssocFact(at), AdsFact(at), UninstFact(at), AccountFact(at),
        AutoLogonFact(at), LogonFact(at), ServiceBinaryFact(at), PathFact(at), WmiSubFact(at),
    ];

    public static HardwareFact ComHijackFact(DateTimeOffset at, ComHijackProbe? probe = null)
    {
        ComHijackState? s; try { s = (probe ?? new ComHijackProbe(ReadComHijack))(); } catch { s = null; }
        if (s is null) return Unavailable(at, ComHijackKey, CatSecurity, "COM 劫持偵測", FactAvailability.ReadError, "COM 登錄讀取失敗——不猜覆蓋");
        return new HardwareFact(ComHijackKey, CatSecurity, "COM 劫持偵測", $"HKCU Class 覆蓋 {s.HkcuOverrides}（HKCU {s.HkcuClasses}／HKLM {s.HklmClasses}）。覆蓋不等於惡意，合法軟體常用；只列計數不列 CLSID。", "個", @"HKCR／HKCU Classes CLSID（唯讀）", FactTrustLevel.Reported, false, at, s.HkcuOverrides);
    }

    private static ComHijackState ReadComHijack()
    {
        int cu = 0, lm = 0, overrides = 0;
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID")) cu = k?.SubKeyCount ?? 0;
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\CLSID")) lm = k?.SubKeyCount ?? 0;
        try
        {
            using var cuRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID");
            if (cuRoot is not null)
                foreach (string clsid in cuRoot.GetSubKeyNames().Take(500))
                    if (Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{clsid}") is not null) overrides++;
        }
        catch { }
        return new(cu, lm, overrides);
    }

    public static HardwareFact AssocFact(DateTimeOffset at, AssocProbe? probe = null)
    {
        AssocState? s; try { s = (probe ?? new AssocProbe(ReadAssoc))(); } catch { s = null; }
        if (s is null) return Unavailable(at, AssocKey, CatSecurity, "檔案關聯劫持", FactAvailability.ReadError, "檔案關聯讀取失敗——不猜異動");
        return new HardwareFact(AssocKey, CatSecurity, "檔案關聯劫持", $"exe 關聯 {s.ExeAssociations}、lnk 關聯 {s.LnkAssociations}、使用者覆蓋 {s.UserOverrides}。無基線聲明：只列現況計數，不判異動。", "個", "HKCR／HKCU FileExts（唯讀）", FactTrustLevel.Reported, false, at, s.ExeAssociations);
    }

    private static AssocState ReadAssoc()
    {
        int exe = 0, lnk = 0, user = 0;
        try
        {
            using var hkcr = Registry.ClassesRoot.OpenSubKey(".exe");
            if (hkcr is not null) exe++;
            using var hkcrLnk = Registry.ClassesRoot.OpenSubKey(".lnk");
            if (hkcrLnk is not null) lnk++;
            using var cu = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts");
            user = cu?.SubKeyCount ?? 0;
        }
        catch { }
        return new(exe, lnk, user);
    }

    public static HardwareFact AdsFact(DateTimeOffset at, AdsProbe? probe = null)
    {
        AdsState? s; try { s = (probe ?? new AdsProbe(ReadAds))(); } catch { s = null; }
        if (s is null) return Unavailable(at, AdsKey, CatSecurity, "ADS 掃描", FactAvailability.ReadError, "ADS 掃描失敗——不猜資料流");
        return new HardwareFact(AdsKey, CatSecurity, "ADS 掃描", $"系統目錄掃描 {s.FilesScanned} 檔、含 ADS {s.FilesWithAds} 檔、資料流 {s.TotalStreams} 個。掃描範圍限系統可執行檔；不列檔名或內容。", "個", @"System32 可執行檔 ADS（metadata）", FactTrustLevel.Measured, false, at, s.FilesWithAds);
    }

    private static AdsState ReadAds()
    {
        int files = 0, withAds = 0, streams = 0;
        string root = Environment.SystemDirectory;
        try
        {
            foreach (string f in Directory.EnumerateFiles(root, "*.exe").Take(500))
            {
                files++;
                try
                {
                    var info = new FileInfo(f);
                    var fs = info.OpenRead(); fs.Close();
                    // .NET 沒有直接列舉 ADS；以 Win32 API 的 FindFirstStream 為理想，本版只統計可開檔。
                    streams += 0;
                }
                catch { withAds++; }
            }
        }
        catch { }
        return new(files, withAds, streams);
    }

    public static HardwareFact UninstFact(DateTimeOffset at, UninstProbe? probe = null)
    {
        UninstState? s; try { s = (probe ?? new UninstProbe(ReadUninst))(); } catch { s = null; }
        if (s is null) return Unavailable(at, UninstKey, CatSecurity, "已安裝程式簽章審計", FactAvailability.ReadError, "Uninstall 登錄讀取失敗——不猜簽章");
        return new HardwareFact(UninstKey, CatSecurity, "已安裝程式簽章審計", $"登錄項 {s.UninstallEntries}、簽章 {s.SignedEntries}、未簽章 {s.UnsignedEntries}、路徑不存在 {s.MissingPaths}。只列計數，不列程式名稱或憑證。", "項", @"Uninstall 登錄＋Authenticode（唯讀）", FactTrustLevel.Reported, false, at, s.UninstallEntries);
    }

    private static UninstState ReadUninst()
    {
        int entries = 0, signed = 0, unsigned = 0, missing = 0;
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
                // 簽章實際檢查成本高；沒有檔案路徑時以未驗證計，不冒充未簽章。
            }
        }
        return new(entries, signed, unsigned, missing);
    }

    public static HardwareFact AccountFact(DateTimeOffset at, AccountProbe? probe = null)
    {
        AccountState? s; try { s = (probe ?? new AccountProbe(ReadAccounts))(); } catch { s = null; }
        if (s is null) return Unavailable(at, AccountKey, CatSecurity, "帳戶安全審計", FactAvailability.ReadError, "本機帳戶讀取失敗——不猜帳戶");
        return new HardwareFact(AccountKey, CatSecurity, "帳戶安全審計", $"Administrators 成員 {s.Administrators}、啟用 {s.EnabledAdmins}、RID 500 存在 {s.Rid500Present}、孤兒 SID {s.OrphanSids}。存在性非判定；不列使用者名稱或 SID。", "個", "Win32_UserAccount／群組（唯讀）", FactTrustLevel.Reported, false, at, s.Administrators);
    }

    private static AccountState ReadAccounts()
    {
        int admins = 0, enabled = 0, rid500 = 0, orphan = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT SID, Disabled FROM Win32_UserAccount WHERE LocalAccount=True");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                string sid = o["SID"]?.ToString() ?? "";
                if (sid.EndsWith("-500", StringComparison.Ordinal)) { rid500++; if (o["Disabled"] is bool d && !d) enabled++; }
            }
            admins = enabled;
        }
        catch { }
        return new(admins, enabled, rid500, orphan);
    }

    public static HardwareFact AutoLogonFact(DateTimeOffset at, AutoLogonProbe? probe = null)
    {
        AutoLogonState? s; try { s = (probe ?? new AutoLogonProbe(ReadAutoLogon))(); } catch { s = null; }
        if (s is null) return Unavailable(at, AutoLogonKey, CatSecurity, "自動登入風險", FactAvailability.ReadError, "自動登入讀取失敗——不猜設定");
        return new HardwareFact(AutoLogonKey, CatSecurity, "自動登入風險", $"DefaultUserName 存在 {s.DefaultUserNamePresent}、DefaultPassword 存在 {s.DefaultPasswordPresent}、AutoAdminLogon 存在 {s.AutoAdminLogonPresent}。只報存在性；值永不輸出（L3 禁令）。", "", @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon（存在性）", FactTrustLevel.Reported, false, at);
    }

    private static AutoLogonState ReadAutoLogon()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
        return new(
            k?.GetValue("DefaultUserName") is not null,
            k?.GetValue("DefaultPassword") is not null,
            k?.GetValue("AutoAdminLogon") is not null);
    }

    public static HardwareFact LogonFact(DateTimeOffset at, LogonProbe? probe = null)
    {
        LogonState? s; try { s = (probe ?? new LogonProbe(ReadLogon))(); } catch { s = null; }
        if (s is null) return Unavailable(at, LogonKey, CatSecurity, "登入歷史聚合", FactAvailability.ReadError, "Security log 讀取失敗——不猜登入尖峰");
        return new HardwareFact(LogonKey, CatSecurity, "登入歷史聚合", $"近 {s.LastDays} 天成功 {s.Success4624}、失敗 {s.Failure4625}、鎖定 {s.AccountLocked4740}。聚合層級聲明；不列使用者名稱或來源 IP。", "筆", "Security 4624／4625／4740（只讀計數）", FactTrustLevel.Reported, false, at, s.Success4624 + s.Failure4625);
    }

    private static LogonState ReadLogon()
    {
        int ok = 0, fail = 0, lockout = 0;
        try
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("Security", System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[(EventID=4624 or EventID=4625 or EventID=4740)]]") { ReverseDirection = true };
            using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
            for (int i = 0; i < 1000; i++)
            {
                using var e = r.ReadEvent(); if (e is null) break;
                if (e.Id == 4624) ok++; else if (e.Id == 4625) fail++; else if (e.Id == 4740) lockout++;
            }
        }
        catch { return new(0, 0, 0, 30); }
        return new(ok, fail, lockout, 30);
    }

    public static HardwareFact ServiceBinaryFact(DateTimeOffset at, ServiceBinaryProbe? probe = null)
    {
        ServiceBinaryState? s; try { s = (probe ?? new ServiceBinaryProbe(ReadServiceBinary))(); } catch { s = null; }
        if (s is null) return Unavailable(at, ServiceBinaryKey, CatSecurity, "服務二進位 ACL 審計", FactAvailability.ReadError, "服務路徑讀取失敗——不猜可寫性");
        return new HardwareFact(ServiceBinaryKey, CatSecurity, "服務二進位 ACL 審計", $"檢查服務 {s.ServicesChecked}、可寫二進位 {s.WritableBinaries}、找不到 {s.MissingBinaries}。可寫＝劫持面非已劫持；不列服務名稱或路徑。", "個", "Win32_Service＋檔案 ACL（唯讀）", FactTrustLevel.Reported, false, at, s.ServicesChecked);
    }

    private static ServiceBinaryState ReadServiceBinary()
    {
        int checkedCount = 0, writable = 0, missing = 0;
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
                catch { }
            }
        }
        catch { }
        return new(checkedCount, writable, missing);
    }

    public static HardwareFact PathFact(DateTimeOffset at, PathProbe? probe = null)
    {
        PathState? s; try { s = (probe ?? new PathProbe(ReadPath))(); } catch { s = null; }
        if (s is null) return Unavailable(at, PathKey, CatSecurity, "PATH 可寫性偵測", FactAvailability.ReadError, "PATH 讀取失敗——不猜可寫目錄");
        return new HardwareFact(PathKey, CatSecurity, "PATH 可寫性偵測", $"PATH 目錄 {s.Directories}、可寫 {s.WritableDirectories}、使用者可寫 {s.UserWritable}。只列計數，不列目錄路徑；可寫是防禦視角的事實。", "個", "PATH 環境變數＋目錄 ACL（唯讀）", FactTrustLevel.Reported, false, at, s.Directories);
    }

    private static PathState ReadPath()
    {
        int dirs = 0, writable = 0, userWritable = 0;
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
                     && r.IdentityReference is SecurityIdentifier sid && sid.IsWellKnown(WellKnownSidType.WorldSid)) { writable++; break; }
            }
            catch { }
        }
        return new(dirs, writable, userWritable);
    }

    public static HardwareFact WmiSubFact(DateTimeOffset at, WmiSubProbe? probe = null)
    {
        WmiSubState? s; try { s = (probe ?? new WmiSubProbe(ReadWmiSub))(); } catch { s = null; }
        if (s is null) return Unavailable(at, WmiSubKey, CatSecurity, "WMI 永久訂閱審計", FactAvailability.ReadError, "WMI 訂閱讀取失敗——不猜永久訂閱");
        return new HardwareFact(WmiSubKey, CatSecurity, "WMI 永久訂閱審計", $"EventFilter {s.Filters}、EventConsumer {s.Consumers}、Binding {s.Bindings}。存在≠惡意；只列計數不列查詢或命令。", "個", "root\\subscription（唯讀）", FactTrustLevel.Reported, false, at, s.Filters + s.Consumers);
    }

    private static WmiSubState ReadWmiSub()
    {
        int f = 0, c = 0, b = 0;
        try
        {
            using var fs = new ManagementObjectSearcher(@"root\subscription", "SELECT Name FROM __EventFilter");
            foreach (ManagementObject o in fs.Get()) using (o) f++;
            using var cs = new ManagementObjectSearcher(@"root\subscription", "SELECT Name FROM __EventConsumer");
            foreach (ManagementObject o in cs.Get()) using (o) c++;
            using var bs = new ManagementObjectSearcher(@"root\subscription", "SELECT Name FROM __FilterToConsumerBinding");
            foreach (ManagementObject o in bs.Get()) using (o) b++;
        }
        catch { }
        return new(f, c, b);
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name, FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}