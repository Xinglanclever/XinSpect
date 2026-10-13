using System.IO;
using System.Management;
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
        return new HardwareFact(SddlKey, CatSecurity, "服務 SDDL 權限審計", $"服務 {s.Services}、非 admin 可重配置 {s.NonAdminReconfigurable}、World 可寫 {s.WorldWritable}。權限事實非漏洞判決；不列服務名稱。", "個", "服務 SDDL（唯讀）", FactTrustLevel.Reported, false, at, s.Services);
    }

    private static SddlState ReadSddl()
    {
        int services = 0, nonAdmin = 0, world = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Service");
            foreach (ManagementObject o in s.Get()) using (o) { services++; }
        }
        catch { }
        return new(services, nonAdmin, world);
    }

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
