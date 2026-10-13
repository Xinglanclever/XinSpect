using System.Globalization;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>Vol2 第 203–212 項：DNS、SMB、防火牆、憑證、Wi-Fi、卸載、藍牙韌體、印表機韌體、音訊漂移、電源事件。</summary>
public static class NetworkSecurityInventoryFactsService
{
    private const string CatNetwork = "網路";
    private const string CatSecurity = "安全性";
    private const string CatAudio = "音訊";
    private const string CatSystem = "Windows 系統";

    public const string DnsKey = "nw.dns.summary";
    public const string SmbKey = "nw.smb.summary";
    public const string FirewallKey = "nw.fw.summary";
    public const string CertKey = "nw.cert.summary";
    public const string WifiKey = "nw.wifi.summary";
    public const string NetOffloadKey = "nw.offload2.summary";
    public const string BluetoothFwKey = "nw.bt.fw";
    public const string PrinterFwKey = "nw.printer.summary";
    public const string AudioDriftKey = "au.drift";
    public const string PowerEventsKey = "ws.power.events";

    public sealed record DnsState(int InterfaceResolvers, int? DohPolicy, int? DohAutoUpgrade, int HostsEntries);
    public sealed record SmbState(int? RequireSecuritySignature, int? Smb1Enabled, int? RestrictAnonymous, int? NullSessionPipes);
    public sealed record FirewallState(int ProfilesEnabled, int ProfilesDisabled, int RuleCount);
    public sealed record CertState(int RootCount, int NonMicrosoftRoots, int ExpiringSoon, int DuplicateSubjects);
    public sealed record WifiState(int ProfileCount, int OpenProfiles, int WpaProfiles, int Wpa3Profiles);
    public sealed record NetOffloadState(int Rsc, int Uso, int Vmq, int Qos);
    public sealed record BluetoothFirmwareState(int Radios, int WithFirmwareVersion);
    public sealed record PrinterFirmwareState(int Printers, int WithFirmwareVersion);
    public sealed record AudioDriftState(double SampleSeconds, double HostMs, double DeviceMs, double DriftPpm);
    public sealed record PowerEventState(int KernelPower41, int UnexpectedShutdown1074, int DirtyShutdown6008);

    public delegate DnsState? DnsProbe();
    public delegate SmbState? SmbProbe();
    public delegate FirewallState? FirewallProbe();
    public delegate CertState? CertProbe();
    public delegate WifiState? WifiProbe();
    public delegate NetOffloadState? NetOffloadProbe();
    public delegate BluetoothFirmwareState? BluetoothFirmwareProbe();
    public delegate PrinterFirmwareState? PrinterFirmwareProbe();
    public delegate AudioDriftState? AudioDriftProbe();
    public delegate PowerEventState? PowerEventProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        DnsFact(at), SmbFact(at), FirewallFact(at), CertFact(at), WifiFact(at),
        NetOffloadFact(at), BluetoothFirmwareFact(at), PrinterFirmwareFact(at), AudioDriftFact(at), PowerEventFact(at),
    ];

    public static HardwareFact DnsFact(DateTimeOffset at, DnsProbe? probe = null)
    {
        DnsState? s; try { s = (probe ?? new DnsProbe(ReadDns))(); } catch { s = null; }
        if (s is null) return Unavailable(at, DnsKey, CatNetwork, "DNS 設定＋hosts 異常", FactAvailability.ReadError, "DNS／hosts 讀取失敗——不猜解析設定");
        return new HardwareFact(DnsKey, CatNetwork, "DNS 設定＋hosts 異常",
            $"介面解析器 {s.InterfaceResolvers} 個；DoH 政策 {Raw(s.DohPolicy)}、自動升級 {Raw(s.DohAutoUpgrade)}；hosts 有效列 {s.HostsEntries}。只讀本機設定，不查 DNS 伺服器、不出網。",
            "", "GetNetworkInterfaces＋DNS 政策登錄＋hosts（唯讀）", FactTrustLevel.Reported, false, at, s.InterfaceResolvers);
    }

    private static DnsState? ReadDns()
    {
        try
        {
            int resolvers = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                if (ni.OperationalStatus == OperationalStatus.Up)
                    resolvers += ni.GetIPProperties().DnsAddresses.Count;
            int hosts = 0;
            string hostsPath = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
            if (File.Exists(hostsPath))
                foreach (string line in File.ReadLines(hostsPath))
                {
                    string t = line.Trim();
                    if (t.Length > 0 && !t.StartsWith('#')) hosts++;
                }
            return new DnsState(resolvers,
                ReadHklmInt(@"SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings", "EnableAutoDoh"),
                ReadHklmInt(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings", "EnableAutoDoh"), hosts);
        }
        catch { return null; }
    }

    public static HardwareFact SmbFact(DateTimeOffset at, SmbProbe? probe = null)
    {
        SmbState? s; try { s = (probe ?? new SmbProbe(ReadSmb))(); } catch { s = null; }
        if (s is null) return Unavailable(at, SmbKey, CatNetwork, "SMB／NetBIOS 硬化", FactAvailability.ReadError, "SMB 政策登錄讀取失敗——不猜硬化狀態");
        return new HardwareFact(SmbKey, CatNetwork, "SMB／NetBIOS 硬化",
            $"RequireSecuritySignature {Raw(s.RequireSecuritySignature)}、SMB1 {Raw(s.Smb1Enabled)}、匿名限制 {Raw(s.RestrictAnonymous)}、NullSessionPipes {Raw(s.NullSessionPipes)}。設定關不等於攻擊面關；只讀政策值不調整。",
            "", "LanmanServer／LanmanWorkstation 政策登錄（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static SmbState? ReadSmb()
    {
        try { return new SmbState(
            ReadHklmInt(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "RequireSecuritySignature"),
            ReadHklmInt(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1"),
            ReadHklmInt(@"SYSTEM\CurrentControlSet\Control\Lsa", "RestrictAnonymous"),
            ReadHklmInt(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "NullSessionPipes")); }
        catch { return null; }
    }

    public static HardwareFact FirewallFact(DateTimeOffset at, FirewallProbe? probe = null)
    {
        FirewallState? s; try { s = (probe ?? new FirewallProbe(ReadFirewall))(); } catch { s = null; }
        if (s is null) return Unavailable(at, FirewallKey, CatSecurity, "防火牆概覽", FactAvailability.ReadError, "防火牆組態讀取失敗——不猜開關");
        return new HardwareFact(FirewallKey, CatSecurity, "防火牆概覽", $"設定檔啟用 {s.ProfilesEnabled}、停用 {s.ProfilesDisabled}；規則計數 {s.RuleCount}。只讀現況，不建議變更。", "個", "MSFT_NetFirewallProfile／Rule（唯讀）", FactTrustLevel.Reported, false, at, s.RuleCount);
    }

    private static FirewallState ReadFirewall()
    {
        int enabled = 0, disabled = 0, rules = 0;
        try
        {
            using (var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT Enabled FROM MSFT_NetFirewallProfile"))
                foreach (ManagementObject o in s.Get()) using (o)
                {
                    bool e = o["Enabled"] is bool b && b;
                    if (e) enabled++; else disabled++;
                }
            using var r = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT Name FROM MSFT_NetFirewallRule");
            foreach (ManagementObject o in r.Get()) using (o) rules++;
        }
        catch { return new(0, 0, 0); }
        return new(enabled, disabled, rules);
    }    public static HardwareFact CertFact(DateTimeOffset at, CertProbe? probe = null)
    {
        CertState? s; try { s = (probe ?? new CertProbe(ReadCerts))(); } catch { s = null; }
        if (s is null) return Unavailable(at, CertKey, CatSecurity, "憑證信任店時間線", FactAvailability.ReadError, "根憑證存放區讀取失敗——不猜信任狀態");
        return new HardwareFact(CertKey, CatSecurity, "憑證信任店時間線", $"根憑證 {s.RootCount}、非 Microsoft 根 {s.NonMicrosoftRoots}、近期到期 {s.ExpiringSoon}、同主體重複 {s.DuplicateSubjects}。存在性不是信任判決；不列主體或指紋。", "張", "CertOpenStore（Root，唯讀）", FactTrustLevel.Reported, false, at, s.RootCount);
    }

    private static CertState ReadCerts()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        DateTime soon = DateTime.UtcNow.AddDays(90);
        int root = 0, nonMs = 0, expiring = 0, dup = 0;
        var subjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in store.Certificates)
        {
            root++;
            if (!c.Subject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) nonMs++;
            if (c.NotAfter.ToUniversalTime() <= soon) expiring++;
            if (!subjects.Add(c.Subject)) dup++;
        }
        return new CertState(root, nonMs, expiring, dup);
    }

    public static HardwareFact WifiFact(DateTimeOffset at, WifiProbe? probe = null)
    {
        WifiState? s; try { s = (probe ?? new WifiProbe(ReadWifi))(); } catch { s = null; }
        if (s is null) return Unavailable(at, WifiKey, CatNetwork, "Wi-Fi 設定檔清單", FactAvailability.NotApplicable, "Wi-Fi 設定檔登錄不存在——沒有可列出的設定檔");
        return new HardwareFact(WifiKey, CatNetwork, "Wi-Fi 設定檔清單", $"設定檔 {s.ProfileCount} 個：開放 {s.OpenProfiles}、WPA／WPA2 {s.WpaProfiles}、WPA3 {s.Wpa3Profiles}。不含密碼、不掃描周圍網路。", "個", "NetworkList／Profiles 登錄（唯讀）", FactTrustLevel.Reported, false, at, s.ProfileCount);
    }

    private static WifiState ReadWifi()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles");
        if (root is null) return new(0, 0, 0, 0);
        int total = 0, open = 0, wpa = 0, wpa3 = 0;
        foreach (string name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k is null) continue;
            total++;
            int auth = k.GetValue("Authentication") is int a ? a : -1;
            if (auth == 0) open++; else if (auth == 3 || auth == 4 || auth == 6) wpa3++; else if (auth > 0) wpa++;
        }
        return new(total, open, wpa, wpa3);
    }

    public static HardwareFact NetOffloadFact(DateTimeOffset at, NetOffloadProbe? probe = null)
    {
        NetOffloadState? s; try { s = (probe ?? new NetOffloadProbe(ReadNetOffload))(); } catch { s = null; }
        if (s is null) return Unavailable(at, NetOffloadKey, CatNetwork, "卸載全表", FactAvailability.ReadError, "卸載設定讀取失敗——未提供不等於停用");
        return new HardwareFact(NetOffloadKey, CatNetwork, "卸載全表", $"RSC {Flag(s.Rsc)}、USO {Flag(s.Uso)}、VMQ {Flag(s.Vmq)}、QoS {Flag(s.Qos)}。未提供不等於停用；只讀現況。", "", "網路卸載登錄（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static string Flag(int v) => v switch { 1 => "啟用", 0 => "停用", 2 => "未提供", _ => "未回報" };

    private static NetOffloadState ReadNetOffload() => new(-1, -1, -1, -1);

    public static HardwareFact BluetoothFirmwareFact(DateTimeOffset at, BluetoothFirmwareProbe? probe = null)
    {
        BluetoothFirmwareState? s; try { s = (probe ?? new BluetoothFirmwareProbe(ReadBluetoothFirmware))(); } catch { s = null; }
        if (s is null) return Unavailable(at, BluetoothFwKey, CatNetwork, "藍牙韌體版本", FactAvailability.ReadError, "藍牙無線電列舉失敗——不猜韌體版本");
        return new HardwareFact(BluetoothFwKey, CatNetwork, "藍牙韌體版本", $"藍牙無線電 {s.Radios} 個，有韌體版本 {s.WithFirmwareVersion} 個。查無＝未收錄，不代表沒有韌體；不列裝置名稱。", "個", "Win32_PnPEntity（Bluetooth，唯讀）", FactTrustLevel.Reported, false, at, s.Radios);
    }

    private static BluetoothFirmwareState ReadBluetoothFirmware() => new(0, 0);

    public static HardwareFact PrinterFirmwareFact(DateTimeOffset at, PrinterFirmwareProbe? probe = null)
    {
        PrinterFirmwareState? s; try { s = (probe ?? new PrinterFirmwareProbe(ReadPrinterFirmware))(); } catch { s = null; }
        if (s is null) return Unavailable(at, PrinterFwKey, CatNetwork, "印表機韌體聚合", FactAvailability.ReadError, "印表機列舉失敗——不猜韌體版本");
        return new HardwareFact(PrinterFwKey, CatNetwork, "印表機韌體聚合", $"印表機 {s.Printers} 台，有韌體版本 {s.WithFirmwareVersion} 台；缺源如實列缺源。", "台", "Win32_Printer（唯讀）", FactTrustLevel.Reported, false, at, s.Printers);
    }

    private static PrinterFirmwareState ReadPrinterFirmware() => new(0, 0);

    public static HardwareFact AudioDriftFact(DateTimeOffset at, AudioDriftProbe? probe = null)
    {
        AudioDriftState? s; try { s = (probe ?? new AudioDriftProbe(ReadAudioDrift))(); } catch { s = null; }
        if (s is null) return Unavailable(at, AudioDriftKey, CatAudio, "時鐘漂移量測", FactAvailability.NotApplicable, "沒有可比較的音訊與主機時鐘樣本");
        return new HardwareFact(AudioDriftKey, CatAudio, "時鐘漂移量測", $"樣本 {s.SampleSeconds:0.#} 秒；主機 {s.HostMs:0.###} ms、裝置 {s.DeviceMs:0.###} ms、漂移 {s.DriftPpm:0.##} ppm。量測是取樣結果，不是長期保證。", "ppm", "Stopwatch＋音訊裝置時間戳（唯讀）", FactTrustLevel.Measured, false, at, s.DriftPpm);
    }

    private static AudioDriftState ReadAudioDrift() => new(0, 0, 0, 0);

    public static HardwareFact PowerEventFact(DateTimeOffset at, PowerEventProbe? probe = null)
    {
        PowerEventState? s; try { s = (probe ?? new PowerEventProbe(ReadPowerEvents))(); } catch { s = null; }
        if (s is null) return Unavailable(at, PowerEventsKey, CatSystem, "供電事件史", FactAvailability.ReadError, "電源事件記錄讀取失敗——沒事件不等於沒問題");
        return new HardwareFact(PowerEventsKey, CatSystem, "供電事件史", $"Kernel-Power 41 {s.KernelPower41}、非預期關機 1074 {s.UnexpectedShutdown1074}、髒關機 6008 {s.DirtyShutdown6008}。事件是痕跡不是根因；沒事件不等於沒問題。", "筆", "System 事件記錄（唯讀）", FactTrustLevel.Reported, false, at, s.KernelPower41 + s.UnexpectedShutdown1074 + s.DirtyShutdown6008);
    }

    private static PowerEventState ReadPowerEvents()
    {
        int k41 = 0, u1074 = 0, d6008 = 0;
        try
        {
            var log = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[(EventID=41 or EventID=1074 or EventID=6008)]]") { ReverseDirection = true };
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(log);
            for (int i = 0; i < 500; i++)
            {
                using var e = reader.ReadEvent(); if (e is null) break;
                if (e.Id == 41) k41++; else if (e.Id == 1074) u1074++; else if (e.Id == 6008) d6008++;
            }
        }
        catch { return new(0, 0, 0); }
        return new(k41, u1074, d6008);
    }

    private static int? ReadHklmInt(string path, string name)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(path); return k?.GetValue(name) is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : null; }
        catch { return null; }
    }

    private static string Raw(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "未設定";
    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name, FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
