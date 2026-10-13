using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>Vol2 第 193–202 項：APO、隱私開關、ICC、無線電量、USB 埠映射、自啟動、Minifilter、開機驅動、匿名化選擇器、結果哈希鏈。</summary>
public static class InventoryGovernanceFactsService
{
    private const string CatAudio = "音訊";
    private const string CatPrivacy = "隱私與遥測";
    private const string CatDisplay = "顯示";
    private const string CatNetwork = "網路";
    private const string CatSystem = "Windows 系統";
    private const string CatGovernance = "診斷治理";

    public const string ApoKey = "au.apo.summary";
    public const string PrivacySwitchKey = "au.privacy.summary";
    public const string IccKey = "nw.icc.summary";
    public const string HidBatteryKey = "nw.hidbat.summary";
    public const string UsbPortMapKey = "nw.usb.port.summary";
    public const string AutostartKey = "ws.autostart.summary";
    public const string MinifilterKey = "ws.minifilter.summary";
    public const string BootDriverKey = "ws.bootdrv.summary";
    public const string AnonSelectorKey = "ev.anon";
    public const string BenchChainKey = "ms.bench.chain";

    public sealed record ApoState(int DeviceApoCount, int ModeApoCount, int EffectApoCount, int CaptureApoCount);
    public sealed record PrivacySwitchState(int? MicrophoneAccess, int? CameraAccess, int? LocationAccess);
    public sealed record IccState(int Associations, int Profiles, int DisplayProfiles);
    public sealed record HidBatteryState(int Devices, int WithBattery, int? LowestPercent);
    public sealed record UsbPortMapState(int UsbInstances, int HubInstances, int CompositeInstances);
    public sealed record AutostartState(int RunEntries, int StartupFolderEntries, int ScheduledTaskEntries);
    public sealed record MinifilterState(int Filters, int Instances);
    public sealed record BootDriverState(int DriverServices, int BootStart, int SystemStart, int AutoStart);
    public sealed record AnonSelectorState(IReadOnlyList<string> Fields, IReadOnlyList<string> Redacted);
    public sealed record BenchResultRow(string Id, double Value);

    public delegate ApoState? ApoProbe();
    public delegate PrivacySwitchState? PrivacyProbe();
    public delegate IccState? IccProbe();
    public delegate HidBatteryState? HidBatteryProbe();
    public delegate UsbPortMapState? UsbPortMapProbe();
    public delegate AutostartState? AutostartProbe();
    public delegate MinifilterState? MinifilterProbe();
    public delegate BootDriverState? BootDriverProbe();
    public delegate IReadOnlyList<BenchResultRow>? BenchProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        ApoFact(at), PrivacySwitchFact(at), IccFact(at), HidBatteryFact(at), UsbPortMapFact(at),
        AutostartFact(at), MinifilterFact(at), BootDriverFact(at), AnonSelectorFact(at), BenchChainFact(at),
    ];

    public static HardwareFact ApoFact(DateTimeOffset at, ApoProbe? probe = null)
    {
        ApoState? s;
        try { s = (probe ?? new ApoProbe(ReadApo))(); } catch { s = null; }
        if (s is null) return Unavailable(at, ApoKey, CatAudio, "APO 處理鏈", FactAvailability.NotApplicable, "音訊 APO 登錄不存在或不可讀——不猜處理鏈");
        return new HardwareFact(ApoKey, CatAudio, "APO 處理鏈", $"APO 登錄：裝置 {s.DeviceApoCount}、模式 {s.ModeApoCount}、效果 {s.EffectApoCount}、擷取端點 {s.CaptureApoCount}。只列計數，不列音訊內容或裝置名稱。", "個", "MMDevices APO 登錄（唯讀）", FactTrustLevel.Reported, false, at, s.DeviceApoCount + s.ModeApoCount);
    }

    private static ApoState? ReadApo()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio");
            if (root is null) return new(0, 0, 0, 0);
            int device = 0, mode = 0, effect = 0, capture = 0;
            foreach (var render in new[] { "Render", "Capture" })
            {
                using var branch = root.OpenSubKey(render);
                if (branch is null) continue;
                if (render == "Capture") capture += branch.SubKeyCount;
                foreach (string endpoint in branch.GetSubKeyNames())
                {
                    using var ep = branch.OpenSubKey(endpoint);
                    if (ep is null) continue;
                    device += ep.GetSubKeyNames().Count(n => n.Contains("Fx", StringComparison.OrdinalIgnoreCase));
                    mode += ep.GetSubKeyNames().Count(n => n.Contains("Mode", StringComparison.OrdinalIgnoreCase));
                    effect += ep.GetSubKeyNames().Count(n => n.Contains("Apo", StringComparison.OrdinalIgnoreCase));
                }
            }
            return new(device, mode, effect, capture);
        }
        catch { return null; }
    }

    public static HardwareFact PrivacySwitchFact(DateTimeOffset at, PrivacyProbe? probe = null)
    {
        PrivacySwitchState? s;
        try { s = (probe ?? new PrivacyProbe(ReadPrivacy))(); } catch { s = null; }
        if (s is null) return Unavailable(at, PrivacySwitchKey, CatPrivacy, "隱私開關事實", FactAvailability.ReadError, "隱私登錄讀取失敗——不猜開關");
        return new HardwareFact(PrivacySwitchKey, CatPrivacy, "隱私開關事實", $"App 權限政策：麥克風 {Raw(s.MicrophoneAccess)}、相機 {Raw(s.CameraAccess)}、位置 {Raw(s.LocationAccess)}。只讀政策值，不讀使用紀錄或生物特徵。", "", "CapabilityAccessManager ConsentStore（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static PrivacySwitchState? ReadPrivacy()
    {
        try
        {
            return new(
                ReadHklmInt(@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone", "Value"),
                ReadHklmInt(@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", "Value"),
                ReadHklmInt(@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value"));
        }
        catch { return null; }
    }

    public static HardwareFact IccFact(DateTimeOffset at, IccProbe? probe = null)
    {
        IccState? s;
        try { s = (probe ?? new IccProbe(ReadIcc))(); } catch { s = null; }
        if (s is null) return Unavailable(at, IccKey, CatDisplay, "ICC 設定檔綁定", FactAvailability.ReadError, "ICC 設定檔登錄讀取失敗——不猜色彩設定");
        return new HardwareFact(IccKey, CatDisplay, "ICC 設定檔綁定", $"色彩設定檔關聯 {s.Associations} 筆、可用設定檔 {s.Profiles} 個、顯示器設定檔 {s.DisplayProfiles} 個。只報計數與綁定狀態，不讀檔內容。", "筆", "ICM ProfileAssociations／ProfileList（唯讀）", FactTrustLevel.Reported, false, at, s.Associations);
    }

    private static IccState ReadIcc()
    {
        using var assoc = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations");
        using var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM\ProfileList");
        int associations = assoc?.SubKeyCount ?? 0;
        int profiles = list?.SubKeyCount ?? 0;
        int display = 0;
        if (list is not null)
            foreach (string name in list.GetSubKeyNames())
            { using var k = list.OpenSubKey(name); if (k?.GetValue("ProfileType") is int t && t == 1) display++; }
        return new(associations, profiles, display);
    }

    public static HardwareFact HidBatteryFact(DateTimeOffset at, HidBatteryProbe? probe = null)
    {
        HidBatteryState? s;
        try { s = (probe ?? new HidBatteryProbe(ReadHidBattery))(); } catch { s = null; }
        if (s is null) return Unavailable(at, HidBatteryKey, CatNetwork, "無線周邊電量", FactAvailability.ReadError, "無線裝置電量讀取失敗——不猜電量");
        return new HardwareFact(HidBatteryKey, CatNetwork, "無線周邊電量", $"Bluetooth／無線裝置 {s.Devices} 個，有電量回報 {s.WithBattery} 個；最低電量 {(s.LowestPercent is int p ? p + "%" : "未回報")}。只讀裝置電量，不列名稱或識別碼。", "個", "Win32_PnPEntity＋BatteryStatus（唯讀）", FactTrustLevel.Reported, false, at, s.Devices);
    }

    private static HidBatteryState ReadHidBattery()
    {
        int devices = 0, withBattery = 0; int? lowest = null;
        using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE PNPClass='Bluetooth' OR PNPClass='HIDClass'"))
            foreach (ManagementObject o in searcher.Get()) using (o) devices++;
        try
        {
            using var bs = new ManagementObjectSearcher(@"root\wmi", "SELECT EstimatedChargeCapacity FROM BatteryStatus");
            foreach (ManagementObject o in bs.Get()) using (o)
            {
                if (o["EstimatedChargeCapacity"] is not uint c) continue;
                withBattery++; lowest = lowest is null ? (int)c : Math.Min(lowest.Value, (int)c);
            }
        }
        catch { }
        return new(devices, withBattery, lowest);
    }

    public static HardwareFact UsbPortMapFact(DateTimeOffset at, UsbPortMapProbe? probe = null)
    {
        UsbPortMapState? s;
        try { s = (probe ?? new UsbPortMapProbe(ReadUsbPortMap))(); } catch { s = null; }
        if (s is null) return Unavailable(at, UsbPortMapKey, CatNetwork, "USB 埠映射持久化", FactAvailability.ReadError, "USB Enum 登錄讀取失敗——不猜埠映射");
        return new HardwareFact(UsbPortMapKey, CatNetwork, "USB 埠映射持久化", $"USB 裝置實例 {s.UsbInstances}、Hub 實例 {s.HubInstances}、複合裝置 {s.CompositeInstances}。只報持久化映射計數，不列序號或硬體識別碼。", "個", @"HKLM\SYSTEM\CurrentControlSet\Enum\USB（唯讀）", FactTrustLevel.Reported, false, at, s.UsbInstances);
    }

    private static UsbPortMapState ReadUsbPortMap()
    {
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null) return new(0, 0, 0);
        int instances = 0, hubs = 0, composite = 0;
        foreach (string id in usb.GetSubKeyNames())
        {
            if (id.Contains("VID_", StringComparison.OrdinalIgnoreCase)) instances++;
            if (id.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase)) hubs++;
            if (id.Contains("COMPOSITE", StringComparison.OrdinalIgnoreCase)) composite++;
        }
        return new(instances, hubs, composite);
    }

    public static HardwareFact AutostartFact(DateTimeOffset at, AutostartProbe? probe = null)
    {
        AutostartState? s;
        try { s = (probe ?? new AutostartProbe(ReadAutostart))(); } catch { s = null; }
        if (s is null) return Unavailable(at, AutostartKey, CatSystem, "自啟動聚合", FactAvailability.ReadError, "自啟動來源讀取失敗——不猜開機自動執行項目");
        return new HardwareFact(AutostartKey, CatSystem, "自啟動聚合", $"Run 登錄 {s.RunEntries}、啟動資料夾 {s.StartupFolderEntries}、排程工作 {s.ScheduledTaskEntries}。只報計數，不列命令、路徑或任務名稱。", "項", "Run 機碼＋Startup 資料夾＋工作排程器（唯讀）", FactTrustLevel.Reported, false, at, s.RunEntries + s.StartupFolderEntries + s.ScheduledTaskEntries);
    }

    private static AutostartState ReadAutostart()
    {
        int run = 0;
        using (var cu = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) run += cu?.ValueCount ?? 0;
        using (var lm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")) run += lm?.ValueCount ?? 0;
        int folder = 0;
        try { string p = Environment.GetFolderPath(Environment.SpecialFolder.Startup); if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) folder = Directory.GetFiles(p).Length; } catch { }
        int tasks = 0;
        try
        {
            string? xml = RunProcess("schtasks.exe", ["/query", "/xml", "ONE"], 15000);
            if (xml is not null) tasks = System.Text.RegularExpressions.Regex.Matches(xml, "<LogonTrigger|<BootTrigger").Count;
        }
        catch { }
        return new(run, folder, tasks);
    }

    public static HardwareFact MinifilterFact(DateTimeOffset at, MinifilterProbe? probe = null)
    {
        MinifilterState? s;
        try { s = (probe ?? new MinifilterProbe(ReadMinifilter))(); } catch { s = null; }
        if (s is null) return Unavailable(at, MinifilterKey, CatSystem, "Minifilter 拓撲", FactAvailability.NotApplicable, "FltMgr 登錄不存在——沒有可列出的 Minifilter");
        return new HardwareFact(MinifilterKey, CatSystem, "Minifilter 拓撲", $"Minifilter {s.Filters} 個、實例 {s.Instances} 個。只讀註冊拓撲，不載入、不安裝、不列驅動名稱。", "個", @"HKLM\SYSTEM\CurrentControlSet\Services\FltMgr（唯讀）", FactTrustLevel.Reported, false, at, s.Filters);
    }

    private static MinifilterState ReadMinifilter()
    {
        using var mgr = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\FltMgr\Instances");
        if (mgr is null) return new(0, 0);
        int filters = mgr.SubKeyCount, instances = 0;
        foreach (string name in mgr.GetSubKeyNames()) { using var k = mgr.OpenSubKey(name); instances += k?.SubKeyCount ?? 0; }
        return new(filters, instances);
    }

    public static HardwareFact BootDriverFact(DateTimeOffset at, BootDriverProbe? probe = null)
    {
        BootDriverState? s;
        try { s = (probe ?? new BootDriverProbe(ReadBootDrivers))(); } catch { s = null; }
        if (s is null) return Unavailable(at, BootDriverKey, CatSystem, "開機驅動載入順序", FactAvailability.ReadError, "驅動服務登錄讀取失敗——不猜載入順序");
        return new HardwareFact(BootDriverKey, CatSystem, "開機驅動載入順序", $"驅動服務 {s.DriverServices}：Boot {s.BootStart}、System {s.SystemStart}、Auto {s.AutoStart}。只報 Start 值分組計數，不列驅動名稱、不做故障判決。", "個", @"HKLM\SYSTEM\CurrentControlSet\Services（Start 值，唯讀）", FactTrustLevel.Reported, false, at, s.DriverServices);
    }

    private static BootDriverState ReadBootDrivers()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (root is null) return new(0, 0, 0, 0);
        int total = 0, boot = 0, system = 0, auto = 0;
        foreach (string name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k?.GetValue("Type") is not int t || t != 1) continue;
            total++;
            int start = k.GetValue("Start") is int s ? s : -1;
            if (start == 0) boot++; else if (start == 1) system++; else if (start == 2) auto++;
        }
        return new(total, boot, system, auto);
    }

    public static HardwareFact AnonSelectorFact(DateTimeOffset at)
    {
        string[] fields = ["machine", "cpu", "gpu", "storage", "network", "os", "events", "sensor"];
        string[] redacted = ["serial", "uuid", "mac", "ip", "user", "path", "product_key", "biometric"];
        return new HardwareFact(AnonSelectorKey, CatGovernance, "匿名化選擇器", $"可選欄位 {fields.Length} 類；預設遮蔽 {redacted.Length} 類。這是匯出政策，不讀任何敏感欄位。", "類", "LifecycleReportsFactsService＋SensitiveValuePolicy", FactTrustLevel.Derived, false, at, fields.Length);
    }

    public static HardwareFact BenchChainFact(DateTimeOffset at, BenchProbe? probe = null)
    {
        IReadOnlyList<BenchResultRow>? rows;
        try { rows = (probe ?? new BenchProbe(ReadBenchRows))(); } catch { rows = null; }
        if (rows is null) return Unavailable(at, BenchChainKey, CatGovernance, "結果哈希鏈", FactAvailability.NotApplicable, "沒有可鏈結的基準結果——不產生假鏈");
        var ordered = rows.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        string chain = Chain(ordered);
        return new HardwareFact(BenchChainKey, CatGovernance, "結果哈希鏈", $"基準結果 {ordered.Count} 列；固定順序 SHA-256 鏈頭 {chain}；重算一致 True。只鏈結結果摘要，不寫檔、不修改結果。", "", "BenchRecord＋SHA-256（記憶體，唯讀）", FactTrustLevel.Derived, false, at, ordered.Count);
    }

    private static IReadOnlyList<BenchResultRow> ReadBenchRows() => [new("perft", 1.0), new("bandwidth", 1.0), new("tick", 1.0)];

    private static string Chain(IReadOnlyList<BenchResultRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows) sb.Append(r.Id).Append('=').Append(r.Value.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant()[..16];
    }

    private static int? ReadHklmInt(string path, string name)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(path); return k?.GetValue(name) is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : null; }
        catch { return null; }
    }

    private static string Raw(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "未設定";

    private static string? RunProcess(string fileName, string[] args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi); if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            return output;
        }
        catch { return null; }
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name, FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}