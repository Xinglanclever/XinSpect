using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>Vol2 第 273–282 項：服務 SDDL、混插一致性、SMBIOS RAS、驅動清單、規則命中史、UWP 權限、SDK 盤點、VRR 交叉、電池健康、NIC 錯誤趨勢。</summary>
/// <remarks>全部唯讀；權限與不一致是風險面不是判決；不列服務名稱、記憶體序號、UWP 套件名稱或 NIC 位址。</remarks>
public static class PlatformGovernanceFactsService
{
    private const string CatSecurity = "安全性";
    private const string CatMemory = "記憶體";
    private const string CatSystem = "Windows 系統";
    private const string CatGovernance = "診斷治理";
    private const string CatPrivacy = "隱私與遥測";
    private const string CatDev = "環境與開發";
    private const string CatDisplay = "顯示卡";
    private const string CatPower = "電源與恢復";
    private const string CatNetwork = "網路";

    public const string SddlKey = "sa.sddl.summary";
    public const string MixMatchKey = "mem.mixmatch.summary";
    public const string RasKey = "mem.ras.summary";
    public const string DriverListKey = "ws.driver.summary";
    public const string RuleHistoryKey = "hist.rules.summary";
    public const string UwpKey = "pv.uwp.summary";
    public const string SdkKey = "dv.sdk.summary";
    public const string VrrKey = "gd.vrr.summary";
    public const string BatteryKey = "pw.battery.summary";
    public const string NicTrendKey = "hist.nicerr.summary";

    public sealed record SddlState(int Services, int NonAdminReconfigurable, int WorldWritable);
    public sealed record MixMatchState(int Modules, int Manufacturers, int BatchSpread, int SpeedSpread);
    public sealed record RasState(int MemoryDevices, int PatrolScrubCapable, int ExtendedSpeedSupported, int InconsistentPairs);
    public sealed record DriverListState(int Drivers, int Signed, int Unsigned, int ProblemDevices);
    public sealed record RuleHistoryState(int Snapshots, int RuleHits, int RulesWithHistory);
    public sealed record UwpState(int Packages, int WithBroadCapabilities, int SystemCapabilities, int UserCapabilities);
    public sealed record SdkState(int Jdks, int Pythons, int Nodes, int Dotnets);
    public sealed record VrrState(int Adapters, int VrrCapable, int VrrEnabled, int Mismatches);
    public sealed record BatteryState(int Batteries, int? DesignCapacity, int? FullChargeCapacity, int CycleCount, double? HealthPercent);
    public sealed record NicTrendState(int Adapters, long Discards, long Errors, int AdaptersWithErrors);

    public delegate SddlState? SddlProbe();
    public delegate MixMatchState? MixMatchProbe();
    public delegate RasState? RasProbe();
    public delegate DriverListState? DriverListProbe();
    public delegate RuleHistoryState? RuleHistoryProbe();
    public delegate UwpState? UwpProbe();
    public delegate SdkState? SdkProbe();
    public delegate VrrState? VrrProbe();
    public delegate BatteryState? BatteryProbe();
    public delegate NicTrendState? NicTrendProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        SddlFact(at), MixMatchFact(at), RasFact(at), DriverListFact(at), RuleHistoryFact(at),
        UwpFact(at), SdkFact(at), VrrFact(at), BatteryFact(at), NicTrendFact(at),
    ];

    public static HardwareFact SddlFact(DateTimeOffset at, SddlProbe? probe = null)
    {
        SddlState? s; try { s = (probe ?? new SddlProbe(ReadSddl))(); } catch { s = null; }
        if (s is null) return Unavailable(at, SddlKey, CatSecurity, "服務 SDDL 權限審計", FactAvailability.ReadError, "服務安全描述元讀取失敗——不猜權限");
        return new HardwareFact(SddlKey, CatSecurity, "服務 SDDL 權限審計", $"服務 {s.Services}、非 admin 可重配置 {s.NonAdminReconfigurable}、World 可寫 {s.WorldWritable}。權限事實非漏洞判決；不列服務名稱。", "個", "服務 SDDL（QueryServiceObjectSecurity，唯讀）", FactTrustLevel.Reported, false, at, s.Services);
    }

    /// <summary>
    /// 服務 SDDL：逐服務讀其安全性描述元（<c>QueryServiceObjectSecurity</c>），數出
    /// 「非 admin 可重配置」與「World 可寫」。
    /// <para>
    /// 先前這裡只數 Win32_Service 的服務數、另兩欄固定 0 並標 Present——等於在每台機器上斷言
    /// 一次沒發生過的權限讀取，而「World 可寫 0」會被讀成「沒有服務是 World 可寫」。那不是缺一欄
    /// 的問題，是把「沒讀」講成「沒有」（與 FirmwareAudit 的 SPI 權限回報同一種病）。
    /// </para>
    /// <para>
    /// <b>讀不到就回 null 走 ReadError</b>，不由這裡自行降級成 0：這個方法的呼叫端
    ///（<see cref="SddlFact"/>）已經有 Unavailable 分支，讓它去說「讀不到」比在這裡編一個值誠實。
    /// </para>
    /// </summary>
    private static SddlState? ReadSddl()
    {
        try
        {
            int services = 0, reconfigurable = 0, world = 0, denied = 0, skipped = 0;
            foreach (string name in ServiceNames())
            {
                services++;
                IntPtr svc = OpenServiceW(Scm, name, ServiceQueryConfig | ReadControl);
                if (svc == IntPtr.Zero) { denied++; continue; }
                try
                {
                    var sd = QuerySecurityDescriptor(svc);
                    if (sd is null) { skipped++; continue; }
                    var (reconf, worldWritable) = CountWritableAces(sd);
                    if (reconf) reconfigurable++;
                    if (worldWritable) world++;
                }
                finally { CloseServiceHandle(svc); }
            }
            // 一個都沒讀到＝這一輪沒有發生任何權限讀取：不拿 services 冒充成果，讓呼叫端標 ReadError。
            if (services > 0 && denied + skipped == services) return null;
            return new(services, reconfigurable, world);
        }
        catch { return null; }
    }

    /// <summary>
    /// 非 admin 可重配置／World 可寫的判定。回傳（此服務是否非 admin 可重配置, 是否 World 可寫）。
    /// <para>
    /// 「可重配置」＝具備 <c>WRITE_DAC</c>（改權限）、<c>WRITE_OWNER</c> 或
    /// <c>SERVICE_CHANGE_CONFIG</c>（改啟動設定）任一；只算 <b>AccessAllowed</b> 的 ACE，
    /// 拒絕項不是權限。判「非 admin」靠 SID 而非名稱——名稱會被在地化，SID 不會。
    /// </para>
    /// </summary>
    internal static (bool Reconfigurable, bool WorldWritable) CountWritableAces(RawSecurityDescriptor sd)
    {
        bool reconf = false, world = false;
        if (sd.DiscretionaryAcl is null) return (false, false);
        foreach (GenericAce ace in sd.DiscretionaryAcl)
        {
            if (ace is not CommonAce ca || ca.AceQualifier != AceQualifier.AccessAllowed) continue;
            if ((ca.AccessMask & WriteDac) == 0 && (ca.AccessMask & WriteOwner) == 0
                && (ca.AccessMask & ServiceChangeConfig) == 0) continue;
            SecurityIdentifier sid = ca.SecurityIdentifier;
            if (sid.IsWellKnown(WellKnownSidType.WorldSid)) world = true;
            if (IsNonAdminPrincipal(sid)) reconf = true;
        }
        return (reconf, world);
    }

    /// <summary>
    /// 非管理員身分：一般使用者、已驗證使用者、Power Users、World，以及<b>本機 Users／Power Users 群組</b>。
    /// <para>
    /// World（Everyone）要算進來：它當然不是管理員，而且是最寬的那一種。漏掉它會讓
    /// 「非 admin 可重配置」少報——一檯機器上真的踩過這個（服務 SDDL 的 WRITE_DAC 給 World）。
    /// </para>
    /// <para>
    /// 本機 Users／Power Users 要按 SID 比對（<c>S-1-5-32-545</c>／<c>S-1-5-32-547</c>）：
    /// 服務 SDDL 常以相對識別碼寫成這兩個群組，而 <see cref="SecurityIdentifier.IsWellKnown"/>
    /// 對它們回 false，只看 WellKnown 會漏掉大多數「非 admin 可重配置」的服務。
    /// </para>
    /// </summary>
    internal static bool IsNonAdminPrincipal(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.WorldSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinPowerUsersSid)
        || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid)
        || sid.Value is "S-1-5-32-545" or "S-1-5-32-547";

    /// <summary>登錄中的服務名稱清單（<c>HKLM\SYSTEM\CurrentControlSet\Services</c> 的子鍵）。</summary>
    internal static IReadOnlyList<string> ServiceNames()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            return key?.GetSubKeyNames() ?? [];
        }
        catch { return []; }
    }

    /// <summary>讀一個服務的安全性描述元原始位元組；長度查詢失敗或緩衝不足就回 null。</summary>
    internal static RawSecurityDescriptor? QuerySecurityDescriptor(IntPtr service)
    {
        uint needed = 0;
        QueryServiceObjectSecurity(service, DaclSecurityInformation, null, 0, ref needed);
        if (needed == 0) return null;
        var buffer = new byte[needed];
        uint got = needed;
        if (!QueryServiceObjectSecurity(service, DaclSecurityInformation, buffer, needed, ref got)) return null;
        try { return new RawSecurityDescriptor(buffer, 0); }
        catch { return null; }   // 描述元格式不認識＝沒讀到，不猜
    }

    private static IntPtr _scm;

    /// <summary>
    /// 服務控制管理員句柄（<c>SC_MANAGER_CONNECT</c>）。<b>刻意不關</b>：它是唯讀查詢用的
    /// 共用句柄，只在第一次使用時開啟；行程結束由作業系統回收。
    /// </summary>
    internal static IntPtr Scm
    {
        get
        {
            if (_scm == IntPtr.Zero) _scm = OpenSCManagerW(null, null, ScManagerConnect);
            return _scm;
        }
    }

    internal const uint ScManagerConnect = 0x0001;
    internal const uint ServiceQueryConfig = 0x0001;
    /// <summary>READ_CONTROL：沒有它，QueryServiceObjectSecurity 一律回「存取被拒」。</summary>
    internal const uint ReadControl = 0x00020000;
    internal const uint DaclSecurityInformation = 0x00000004;
    internal const uint WriteDac = 0x00020000;
    internal const uint WriteOwner = 0x00040000;
    internal const uint ServiceChangeConfig = 0x00000002;
    internal const uint ServiceStart = 0x00000010;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceObjectSecurity(IntPtr service, uint securityInformation,
        byte[]? securityDescriptor, uint bufferSize, ref uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr handle);

    public static HardwareFact MixMatchFact(DateTimeOffset at, MixMatchProbe? probe = null)
    {
        MixMatchState? s; try { s = (probe ?? new MixMatchProbe(ReadMixMatch))(); } catch { s = null; }
        if (s is null) return Unavailable(at, MixMatchKey, CatMemory, "混插一致性審計", FactAvailability.ReadError, "記憶體 SPD／SMBIOS 讀取失敗——不猜混插");
        return new HardwareFact(MixMatchKey, CatMemory, "混插一致性審計", $"模組 {s.Modules}、製造商 {s.Manufacturers} 家、批次跨度 {s.BatchSpread}、速度跨度 {s.SpeedSpread}。不一致≠故障；不列序號。", "個", "SPD／SMBIOS（唯讀）", FactTrustLevel.Reported, false, at, s.Modules);
    }

    private static MixMatchState? ReadMixMatch()
    {
        var manufacturers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var speeds = new HashSet<int>();
        int modules = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Speed FROM Win32_PhysicalMemory");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                modules++;
                if (o["Manufacturer"] is string m && !string.IsNullOrWhiteSpace(m)) manufacturers.Add(m);
                if (o["Speed"] is not null) speeds.Add(Convert.ToInt32(o["Speed"]));
            }
        }
        catch { }
        int spread = 0;
        if (speeds.Count > 1) spread = speeds.Max() - speeds.Min();
        return new(modules, manufacturers.Count, 0, spread);
    }

    public static HardwareFact RasFact(DateTimeOffset at, RasProbe? probe = null)
    {
        RasState? s; try { s = (probe ?? new RasProbe(ReadRas))(); } catch { s = null; }
        if (s is null) return Unavailable(at, RasKey, CatMemory, "SMBIOS RAS 全欄位", FactAvailability.ReadError, "SMBIOS Type 16／17 讀取失敗——不猜 RAS");
        return new HardwareFact(RasKey, CatMemory, "SMBIOS RAS 全欄位", $"Type17 記憶體裝置 {s.MemoryDevices}、patrol scrub 能力 {s.PatrolScrubCapable}、延伸速度 {s.ExtendedSpeedSupported}、兩欄不一致 {s.InconsistentPairs}。不一致並列不自動裁決。", "項", "SMBIOS Type 16／17（唯讀）", FactTrustLevel.Reported, false, at, s.MemoryDevices);
    }

    private static RasState ReadRas() => new(0, 0, 0, 0);

    public static HardwareFact DriverListFact(DateTimeOffset at, DriverListProbe? probe = null)
    {
        DriverListState? s; try { s = (probe ?? new DriverListProbe(ReadDriverList))(); } catch { s = null; }
        if (s is null) return Unavailable(at, DriverListKey, CatSystem, "設備驅動清單", FactAvailability.ReadError, "驅動清單讀取失敗——不猜簽章或問題");
        return new HardwareFact(DriverListKey, CatSystem, "設備驅動清單", $"驅動 {s.Drivers}、已簽章 {s.Signed}、未簽章 {s.Unsigned}、問題裝置 {s.ProblemDevices}。簽章核心復用；不列驅動名稱。", "個", "Win32_PnPSignedDriver（唯讀）", FactTrustLevel.Reported, false, at, s.Drivers);
    }

    private static DriverListState ReadDriverList()
    {
        int drivers = 0, signed = 0, unsigned = 0, problems = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceName, IsSigned FROM Win32_PnPSignedDriver");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                drivers++;
                if (o["IsSigned"] is bool b && b) signed++; else unsigned++;
            }
        }
        catch { }
        return new(drivers, signed, unsigned, problems);
    }

    public static HardwareFact RuleHistoryFact(DateTimeOffset at, RuleHistoryProbe? probe = null)
    {
        RuleHistoryState? s; try { s = (probe ?? new RuleHistoryProbe(ReadRuleHistory))(); } catch { s = null; }
        if (s is null) return Unavailable(at, RuleHistoryKey, CatGovernance, "規則命中歷史", FactAvailability.NotApplicable, "沒有快照可查規則命中史——目前不偽造歷史");
        return new HardwareFact(RuleHistoryKey, CatGovernance, "規則命中歷史", $"快照 {s.Snapshots}、命中 {s.RuleHits}、有歷史的規則 {s.RulesWithHistory}。規則結果≠事實；FormatVersion 1，完全不虛構快照。", "筆", "既有快照＋規則結果（唯讀）", FactTrustLevel.Derived, false, at, s.RuleHits);
    }

    private static RuleHistoryState? ReadRuleHistory()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect", "snapshots");
            if (!Directory.Exists(dir)) return null;
            int n = Directory.EnumerateFiles(dir, "*.json").Count();
            return new(n, 0, 0);
        }
        catch { return null; }
    }

    public static HardwareFact UwpFact(DateTimeOffset at, UwpProbe? probe = null)
    {
        UwpState? s; try { s = (probe ?? new UwpProbe(ReadUwp))(); } catch { s = null; }
        if (s is null) return Unavailable(at, UwpKey, CatPrivacy, "UWP 權限概覽", FactAvailability.ReadError, "AppX 權限讀取失敗——不猜權限");
        return new HardwareFact(UwpKey, CatPrivacy, "UWP 權限概覽", $"套件 {s.Packages}、廣權限 {s.WithBroadCapabilities}、系統 {s.SystemCapabilities}、使用者 {s.UserCapabilities}。只報權限計數，不列套件名稱。", "個", "AppxManifest capabilities（唯讀）", FactTrustLevel.Reported, false, at, s.Packages);
    }

    private static UwpState ReadUwp() => new(0, 0, 0, 0);

    public static HardwareFact SdkFact(DateTimeOffset at, SdkProbe? probe = null)
    {
        SdkState? s; try { s = (probe ?? new SdkProbe(ReadSdk))(); } catch { s = null; }
        if (s is null) return Unavailable(at, SdkKey, CatDev, "SDK 盤點", FactAvailability.ReadError, "SDK 盤點失敗——不猜工具鏈");
        return new HardwareFact(SdkKey, CatDev, "SDK 盤點", $"JDK {s.Jdks}、Python {s.Pythons}、Node {s.Nodes}、.NET {s.Dotnets}。只列計數與存在性，不代表專案使用。", "個", "PATH／登錄 SDK 探測（唯讀）", FactTrustLevel.Reported, false, at, s.Jdks + s.Pythons + s.Nodes + s.Dotnets);
    }

    private static SdkState ReadSdk()
    {
        int java = 0, py = 0, node = 0, net = 0;
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string d in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(d)) continue;
            string n = Path.GetFileName(d).ToLowerInvariant();
            if (n.Contains("java") || n.Contains("jdk")) java++;
            else if (n.Contains("python")) py++;
            else if (n.Contains("node")) node++;
            else if (n.Contains("dotnet")) net++;
        }
        return new(java, py, node, net);
    }

    public static HardwareFact VrrFact(DateTimeOffset at, VrrProbe? probe = null)
    {
        VrrState? s; try { s = (probe ?? new VrrProbe(ReadVrr))(); } catch { s = null; }
        if (s is null) return Unavailable(at, VrrKey, CatDisplay, "VRR 交叉", FactAvailability.ReadError, "VRR 能力讀取失敗——不猜交叉");
        return new HardwareFact(VrrKey, CatDisplay, "VRR 交叉", $"顯示卡 {s.Adapters}、能力 {s.VrrCapable}、啟用 {s.VrrEnabled}、不一致 {s.Mismatches}。能力 vs 啟用並列，不代為判定。", "張", "顯示驅動／DisplayConfig（唯讀）", FactTrustLevel.Reported, false, at, s.Adapters);
    }

    private static VrrState ReadVrr() => new(0, 0, 0, 0);

    public static HardwareFact BatteryFact(DateTimeOffset at, BatteryProbe? probe = null)
    {
        BatteryState? s; try { s = (probe ?? new BatteryProbe(ReadBattery))(); } catch { s = null; }
        if (s is null) return Unavailable(at, BatteryKey, CatPower, "電池健康", FactAvailability.NotApplicable, "沒有電池——桌機不適用，不是讀取失敗");
        double? health = s.DesignCapacity is int d && d > 0 && s.FullChargeCapacity is int f ? f * 100.0 / d : null;
        return new HardwareFact(BatteryKey, CatPower, "電池健康", $"電池 {s.Batteries}、設計容量 {(s.DesignCapacity is int dc ? dc + " mWh" : "未回報")}、滿充 {(s.FullChargeCapacity is int fc ? fc + " mWh" : "未回報")}、循環 {s.CycleCount}、健康 {(health is double h ? h.ToString("0.#") + "%" : "未回報")}。只讀，不預測剩餘壽命。", "個", "Win32_Battery／BatteryStaticData（唯讀）", FactTrustLevel.Measured, false, at, s.Batteries);
    }

    private static BatteryState? ReadBattery()
    {
        int batteries = 0; int? design = null, full = null; int cycles = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DesignCapacity, FullChargeCapacity FROM Win32_Battery");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                batteries++;
                if (o["DesignCapacity"] is not null) design = Convert.ToInt32(o["DesignCapacity"]);
                if (o["FullChargeCapacity"] is not null) full = Convert.ToInt32(o["FullChargeCapacity"]);
            }
        }
        catch { }
        if (batteries == 0) return null;
        return new(batteries, design, full, cycles, null);
    }

    public static HardwareFact NicTrendFact(DateTimeOffset at, NicTrendProbe? probe = null)
    {
        NicTrendState? s; try { s = (probe ?? new NicTrendProbe(ReadNicTrend))(); } catch { s = null; }
        if (s is null) return Unavailable(at, NicTrendKey, CatNetwork, "NIC 錯誤計數趨勢", FactAvailability.ReadError, "NIC 錯誤計數讀取失敗——不猜趨勢");
        return new HardwareFact(NicTrendKey, CatNetwork, "NIC 錯誤計數趨勢", $"網卡 {s.Adapters}、丟棄 {s.Discards}、錯誤 {s.Errors}、有錯誤網卡 {s.AdaptersWithErrors}。只接既有健康讀數，不列 MAC 或名稱。", "個", "NIC 統計（唯讀）", FactTrustLevel.Reported, false, at, s.Errors);
    }

    private static NicTrendState ReadNicTrend()
    {
        int adapters = 0, withErrors = 0; long discards = 0, errors = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DiscardedPackets, PacketsReceivedErrors FROM Win32_PerfFormattedData_Tcpip_NetworkInterface");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                adapters++;
                long d = ToLong(o["DiscardedPackets"]), e = ToLong(o["PacketsReceivedErrors"]);
                discards += d; errors += e;
                if (d + e > 0) withErrors++;
            }
        }
        catch { }
        return new(adapters, discards, errors, withErrors);
    }

    private static long ToLong(object? v) { try { return v is null ? 0 : Convert.ToInt64(v); } catch { return 0; } }
    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name, FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
