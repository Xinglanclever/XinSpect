using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Management;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 作業與資產盤點補充（Vol2 第 173–182 項）：虛擬網卡、舊式連接埠、服務依賴圖、
/// 傾印設定、NTFS 特性、實體磁碟交叉、舊式儲存、遙測政策、時區資源與診斷記錄器。
/// </summary>
/// <remarks>
/// <para><b>全部唯讀</b>：登錄、WMI、FileVersionInfo 與 Windows API 只讀；不啟停服務、
/// 不改傾印設定、不改遙測政策、不觸發記錄器。</para>
/// <para><b>誠實界線</b>：0 個裝置只有在來源回報空集合時才是值；讀不到用
/// <see cref="FactAvailability.ReadError"/> 附原因；不列本機路徑、序號、MAC/IP 或事件內容。</para>
/// </remarks>
public static class OperationsInventoryFactsService
{
    private const string CatNetwork = "網路";
    private const string CatSystem = "Windows 系統";
    private const string CatStorage = "儲存";
    private const string CatPrivacy = "隱私與遥測";
    private const string CatTime = "時間";

    public const string VirtualAdapterKey = "nw.vnic.summary";
    public const string LegacyPortKey = "nw.legacy.summary";
    public const string ServiceGraphKey = "ws.svc.graph";
    public const string DumpConfigKey = "ws.dumpcfg.summary";
    public const string NtfsAuditKey = "st.ntfs.summary";
    public const string PhysicalDiskCrossKey = "st.xcheck.summary";
    public const string LegacyStorageKey = "st.legacy.summary";
    public const string TelemetryKey = "pv.telemetry";
    public const string TimeZoneKey = "tm.tzdb";
    public const string DiagnosticViewerKey = "pv.ddv";

    public sealed record ServiceNode(string Name, string State, IReadOnlyList<string> Dependencies);
    public sealed record DumpConfiguration(bool KeyExists, int? CrashDumpEnabled, bool? AutoReboot, int PageFileCount);
    public sealed record NtfsFeatureState(int VolumeCount, int? Disable8Dot3, int? DisableLastAccess);
    public sealed record PhysicalDiskSizes(IReadOnlyList<long> WmiDrives, IReadOnlyList<long> StorageDisks);
    public sealed record LegacyStorageState(int OpticalDrives, int FloppyDrives);
    public sealed record TimeZoneState(string Id, string DisplayName, TimeSpan UtcOffset, bool SupportsDaylightSavingTime, string? TzResVersion);
    public sealed record DiagnosticLoggerState(int TotalLoggers, int EnabledLoggers, int DisabledLoggers);

    public delegate IReadOnlyList<string>? VirtualAdapterProbe();
    public delegate IReadOnlyDictionary<string, string>? LegacyPortProbe();
    public delegate IReadOnlyList<ServiceNode>? ServiceGraphProbe();
    public delegate DumpConfiguration? DumpConfigProbe();
    public delegate NtfsFeatureState? NtfsProbe();
    public delegate PhysicalDiskSizes? PhysicalDiskProbe();
    public delegate LegacyStorageState? LegacyStorageProbe();
    public delegate int? TelemetryProbe();
    public delegate TimeZoneState? TimeZoneProbe();
    public delegate DiagnosticLoggerState? DiagnosticLoggerProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        VirtualAdapterFact(at),
        LegacyPortFact(at),
        ServiceGraphFact(at),
        DumpConfigFact(at),
        NtfsAuditFact(at),
        PhysicalDiskCrossFact(at),
        LegacyStorageFact(at),
        TelemetryFact(at),
        TimeZoneFact(at),
        DiagnosticViewerFact(at),
    ];

    // ── NW-006 虛擬網卡盤點 ────────────────────────────────────────────────

    public static HardwareFact VirtualAdapterFact(DateTimeOffset at, VirtualAdapterProbe? probe = null)
    {
        IReadOnlyList<string>? names;
        try { names = (probe ?? new VirtualAdapterProbe(ReadVirtualAdapters))(); }
        catch { names = null; }
        if (names is null)
            return Unavailable(at, VirtualAdapterKey, CatNetwork, "虛擬網卡盤點", FactAvailability.ReadError,
                "Win32_NetworkAdapter 列舉失敗——不猜虛擬網卡數量");

        var groups = names.GroupBy(ClassifyVirtualAdapter, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        string detail = groups.Count == 0 ? "沒有回報虛擬網卡"
            : string.Join("、", groups.Select(g => $"{g.Key} {g.Count()}"));
        return new HardwareFact(VirtualAdapterKey, CatNetwork, "虛擬網卡盤點",
            $"虛擬／軟體網卡 {names.Count} 個：{detail}。只報分類計數，不列網卡名稱、MAC 或 IP；分類依介面名稱關鍵字，未知實作歸「其他」。",
            "個", "Win32_NetworkAdapter（PhysicalAdapter=false 或名稱命中）", FactTrustLevel.Reported, false, at, names.Count);
    }

    private static IReadOnlyList<string> ReadVirtualAdapters()
    {
        var names = new List<string>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, NetConnectionID, PhysicalAdapter FROM Win32_NetworkAdapter");
        foreach (ManagementObject o in searcher.Get())
            using (o)
            {
                string name = o["Name"]?.ToString() ?? "";
                string connection = o["NetConnectionID"]?.ToString() ?? "";
                bool physical = o["PhysicalAdapter"] is bool b && b;
                string label = string.IsNullOrWhiteSpace(name) ? connection : name;
                if (!physical || LooksVirtual(label) || LooksVirtual(connection))
                    names.Add(label);
            }
        return names;
    }

    private static bool LooksVirtual(string name)
        => ClassifyVirtualAdapter(name) != "其他";

    private static string ClassifyVirtualAdapter(string name)
    {
        string n = name.ToUpperInvariant();
        if (n.Contains("HYPER-V") || n.Contains("VSWITCH") || n.Contains("VETHERNET")) return "Hyper-V";
        if (n.Contains("WIREGUARD") || n.Contains("TAP-WINDOWS") || n.Contains("OPENVPN") || n.Contains("VPN")) return "VPN／TAP";
        if (n.Contains("VMWARE") || n.Contains("VIRTUALBOX") || n.Contains("VIRTIO")) return "VM 網路";
        if (n.Contains("LOOPBACK") || n.Contains("NPcap") || n.Contains("NPTEST")) return "測試／迴路";
        return "其他";
    }

    // ── NW-022 LPT／COM 盤點 ───────────────────────────────────────────────

    public static HardwareFact LegacyPortFact(DateTimeOffset at, LegacyPortProbe? probe = null)
    {
        IReadOnlyDictionary<string, string>? ports;
        try { ports = (probe ?? new LegacyPortProbe(ReadLegacyPorts))(); }
        catch { ports = null; }
        if (ports is null)
            return Unavailable(at, LegacyPortKey, CatNetwork, "LPT／COM 盤點", FactAvailability.NotApplicable,
                "SERIALCOMM 裝置對映不存在或不支援——沒有可列出的舊式連接埠");

        int com = ports.Values.Count(v => v.StartsWith("COM", StringComparison.OrdinalIgnoreCase));
        int lpt = ports.Values.Count(v => v.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
        int other = ports.Count - com - lpt;
        return new HardwareFact(LegacyPortKey, CatNetwork, "LPT／COM 盤點",
            $"舊式連接埠 {ports.Count} 個：COM {com}、LPT {lpt}、其他 {other}。只報計數與埠類別，不列裝置名稱或路徑；0 個是值。",
            "個", @"HKLM\HARDWARE\DEVICEMAP\SERIALCOMM", FactTrustLevel.Reported, false, at, ports.Count);
    }

    private static IReadOnlyDictionary<string, string>? ReadLegacyPorts()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is null) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in key.GetValueNames())
                map[name] = key.GetValue(name)?.ToString() ?? "";
            return map;
        }
        catch { return null; }
    }

    // ── WS-002 服務依賴圖 ─────────────────────────────────────────────────

    public static HardwareFact ServiceGraphFact(DateTimeOffset at, ServiceGraphProbe? probe = null)
    {
        IReadOnlyList<ServiceNode>? nodes;
        try { nodes = (probe ?? new ServiceGraphProbe(ReadServiceGraph))(); }
        catch { nodes = null; }
        if (nodes is null)
            return Unavailable(at, ServiceGraphKey, CatSystem, "服務依賴圖", FactAvailability.ReadError,
                "Win32_Service 列舉失敗——不猜服務依賴鏈");

        if (nodes.Count == 0)
            return Unavailable(at, ServiceGraphKey, CatSystem, "服務依賴圖", FactAvailability.NotApplicable,
                "WMI 回報 0 個服務（0 是值）");
        int running = nodes.Count(n => n.State.Equals("Running", StringComparison.OrdinalIgnoreCase));
        int stopped = nodes.Count(n => n.State.Equals("Stopped", StringComparison.OrdinalIgnoreCase));
        int withDependencies = nodes.Count(n => n.Dependencies.Count > 0);
        int edges = nodes.Sum(n => n.Dependencies.Count);
        int missing = nodes.SelectMany(n => n.Dependencies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(d => !nodes.Any(n => n.Name.Equals(d, StringComparison.OrdinalIgnoreCase)));
        int depth = MaxDependencyDepth(nodes);
        return new HardwareFact(ServiceGraphKey, CatSystem, "服務依賴圖",
            $"服務 {nodes.Count} 個（執行中 {running}、已停止 {stopped}）；有依賴者 {withDependencies} 個、依賴邊 {edges} 條、最深鏈 {depth} 層、未對應依賴 {missing} 個。" +
            "只報圖形狀與計數，不列服務名稱；未對應依賴可能是驅動或已移除服務，不判故障。",
            "個", "Win32_Service（Name／State／Dependencies，唯讀）", FactTrustLevel.Derived, false, at, nodes.Count);
    }

    private static IReadOnlyList<ServiceNode> ReadServiceGraph()
    {
        var nodes = new List<ServiceNode>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, State, Dependencies FROM Win32_Service");
        foreach (ManagementObject o in searcher.Get())
            using (o)
            {
                var deps = (o["Dependencies"] as string[] ?? []).Where(d => !string.IsNullOrWhiteSpace(d)).ToArray();
                nodes.Add(new ServiceNode(o["Name"]?.ToString() ?? "", o["State"]?.ToString() ?? "", deps));
            }
        return nodes;
    }

    private static int MaxDependencyDepth(IReadOnlyList<ServiceNode> nodes)
    {
        var byName = nodes.GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var memo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int Depth(string name)
        {
            if (!byName.TryGetValue(name, out var node)) return 0;
            if (memo.TryGetValue(name, out int cached)) return cached;
            if (!visiting.Add(name)) return 0; // 循環邊不遞迴，避免無限展開
            int result = 1 + node.Dependencies.Select(Depth).DefaultIfEmpty(0).Max();
            visiting.Remove(name);
            memo[name] = result;
            return result;
        }
        return nodes.Select(n => Depth(n.Name)).DefaultIfEmpty(0).Max();
    }

    // ── WS-007 傾印設定審計 ───────────────────────────────────────────────

    public static HardwareFact DumpConfigFact(DateTimeOffset at, DumpConfigProbe? probe = null)
    {
        DumpConfiguration? cfg;
        try { cfg = (probe ?? new DumpConfigProbe(ReadDumpConfiguration))(); }
        catch { cfg = null; }
        if (cfg is null)
            return Unavailable(at, DumpConfigKey, CatSystem, "傾印設定審計", FactAvailability.ReadError,
                "CrashControl 登錄讀取失敗——不猜傾印設定");
        if (!cfg.KeyExists)
            return Unavailable(at, DumpConfigKey, CatSystem, "傾印設定審計", FactAvailability.NotApplicable,
                "沒有 CrashControl 設定鍵；不代填 Windows 預設");

        string type = cfg.CrashDumpEnabled switch
        {
            0 => "無傾印",
            1 => "完整傾印",
            2 => "核心傾印",
            3 => "小型傾印",
            7 => "自動傾印",
            null => "未設定",
            int n => $"未知（原始值 {n}）",
        };
        string auto = cfg.AutoReboot is null ? "未回報" : YesNo(cfg.AutoReboot.Value);
        return new HardwareFact(DumpConfigKey, CatSystem, "傾印設定審計",
            $"傾印類型 {type}；崩潰後自動重啟 {auto}；分頁檔條目 {cfg.PageFileCount} 個。只審計不調整；傾印路徑含本機目錄，刻意不揭露。",
            "", @"HKLM\SYSTEM\CurrentControlSet\Control\CrashControl＋Session Manager\Memory Management", FactTrustLevel.Reported, false, at);
    }

    private static DumpConfiguration ReadDumpConfiguration()
    {
        using var crash = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\CrashControl");
        if (crash is null) return new DumpConfiguration(false, null, null, 0);
        int? enabled = crash.GetValue("CrashDumpEnabled") is { } e ? Convert.ToInt32(e, CultureInfo.InvariantCulture) : null;
        bool? auto = crash.GetValue("AutoReboot") is { } a ? Convert.ToInt32(a, CultureInfo.InvariantCulture) != 0 : null;
        int pageFiles = 0;
        using var mm = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
        if (mm?.GetValue("PagingFiles") is string[] lines)
            pageFiles = lines.Count(l => !string.IsNullOrWhiteSpace(l));
        else if (mm?.GetValue("PagingFiles") is string one && !string.IsNullOrWhiteSpace(one))
            pageFiles = 1;
        return new DumpConfiguration(true, enabled, auto, pageFiles);
    }

    // ── MS-012 NTFS 特性審計 ──────────────────────────────────────────────

    public static HardwareFact NtfsAuditFact(DateTimeOffset at, NtfsProbe? probe = null)
    {
        NtfsFeatureState? state;
        try { state = (probe ?? new NtfsProbe(ReadNtfsFeatures))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, NtfsAuditKey, CatStorage, "NTFS 特性審計", FactAvailability.ReadError,
                "NTFS 卷冊或檔案系統登錄讀取失敗——不猜特性");
        if (state.VolumeCount == 0)
            return Unavailable(at, NtfsAuditKey, CatStorage, "NTFS 特性審計", FactAvailability.NotApplicable,
                "沒有回報 NTFS 卷冊（0 是值）");

        return new HardwareFact(NtfsAuditKey, CatStorage, "NTFS 特性審計",
            $"NTFS 卷冊 {state.VolumeCount} 個；8.3 短檔名設定 {(RawSetting(state.Disable8Dot3))}；" +
            $"上次存取時間更新設定 {(RawSetting(state.DisableLastAccess))}。只報 Windows 設定原值，不做效能或安全判決。",
            "卷", @"Win32_Volume＋HKLM\SYSTEM\CurrentControlSet\Control\FileSystem", FactTrustLevel.Reported, false, at, state.VolumeCount);
    }

    private static NtfsFeatureState ReadNtfsFeatures()
    {
        int volumes = 0;
        using (var searcher = new ManagementObjectSearcher("SELECT FileSystem FROM Win32_Volume WHERE FileSystem='NTFS'"))
        foreach (ManagementObject o in searcher.Get())
            using (o) volumes++;
        using var fs = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
        int? shortNames = fs?.GetValue("NtfsDisable8dot3NameCreation") is { } s
            ? Convert.ToInt32(s, CultureInfo.InvariantCulture) : null;
        int? lastAccess = fs?.GetValue("NtfsDisableLastAccessUpdate") is { } l
            ? Convert.ToInt32(l, CultureInfo.InvariantCulture) : null;
        return new NtfsFeatureState(volumes, shortNames, lastAccess);
    }

    private static string RawSetting(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "未設定";

    // ── ST-016 Get-PhysicalDisk 交叉 ─────────────────────────────────────

    public static HardwareFact PhysicalDiskCrossFact(DateTimeOffset at, PhysicalDiskProbe? probe = null)
    {
        PhysicalDiskSizes? sizes;
        try { sizes = (probe ?? new PhysicalDiskProbe(ReadPhysicalDiskSizes))(); }
        catch { sizes = null; }
        if (sizes is null)
            return Unavailable(at, PhysicalDiskCrossKey, CatStorage, "Win32_DiskDrive vs MSFT_PhysicalDisk", FactAvailability.ReadError,
                "WMI 磁碟列舉失敗——不猜兩個來源是否一致");

        var wmi = sizes.WmiDrives.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var storage = sizes.StorageDisks.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        int matched = wmi.Keys.Intersect(storage.Keys).Sum(k => Math.Min(wmi[k], storage[k]));
        int onlyWmi = sizes.WmiDrives.Count - matched;
        int onlyStorage = sizes.StorageDisks.Count - matched;
        int ambiguous = wmi.Keys.Concat(storage.Keys).Distinct()
            .Count(k => (wmi.GetValueOrDefault(k) > 1) || (storage.GetValueOrDefault(k) > 1));
        string verdict = onlyWmi == 0 && onlyStorage == 0
            ? "兩來源以容量多重集完全對上；不自動判定哪個來源正確"
            : "兩來源有容量差集；不自動判定哪個來源正確";
        return new HardwareFact(PhysicalDiskCrossKey, CatStorage, "Win32_DiskDrive vs MSFT_PhysicalDisk",
            $"Win32 {sizes.WmiDrives.Count} 顆、Storage {sizes.StorageDisks.Count} 顆；容量匹配 {matched}、只在 Win32 {onlyWmi}、只在 Storage {onlyStorage}、同容量歧義群 {ambiguous}。{verdict}。" +
            "同名容量無法唯一配對時不硬配；這是交叉事實，不是故障判決。",
            "顆", "Win32_DiskDrive.Size vs MSFT_PhysicalDisk.Size", FactTrustLevel.Derived, false, at, matched);
    }

    private static PhysicalDiskSizes ReadPhysicalDiskSizes()
    {
        var wmi = new List<long>();
        using (var searcher = new ManagementObjectSearcher("SELECT Size FROM Win32_DiskDrive"))
        foreach (ManagementObject o in searcher.Get())
            using (o)
                if (o["Size"] is not null) wmi.Add(ToLong(o["Size"]));
        var storage = new List<long>();
        using (var searcher = new ManagementObjectSearcher(
            "SELECT Size FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
        foreach (ManagementObject o in searcher.Get())
            using (o)
                if (o["Size"] is not null) storage.Add(ToLong(o["Size"]));
        return new PhysicalDiskSizes(wmi, storage);
    }

    // ── ST-019 長尾儲存盤點 ───────────────────────────────────────────────

    public static HardwareFact LegacyStorageFact(DateTimeOffset at, LegacyStorageProbe? probe = null)
    {
        LegacyStorageState? state;
        try { state = (probe ?? new LegacyStorageProbe(ReadLegacyStorage))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, LegacyStorageKey, CatStorage, "舊式儲存盤點", FactAvailability.ReadError,
                "光碟機／軟碟機 WMI 列舉失敗——不猜舊式裝置");

        string verdict = state.OpticalDrives == 0 && state.FloppyDrives == 0
            ? "沒有回報舊式儲存；0 是值"
            : "只列類別數量，不讀光碟內容、不掛載媒體";
        return new HardwareFact(LegacyStorageKey, CatStorage, "舊式儲存盤點",
            $"光碟機 {state.OpticalDrives} 台、軟碟機 {state.FloppyDrives} 台。{verdict}。",
            "台", "Win32_CDROMDrive＋Win32_FloppyDrive", FactTrustLevel.Reported, false, at,
            state.OpticalDrives + state.FloppyDrives);
    }

    private static LegacyStorageState ReadLegacyStorage()
    {
        int optical = CountWmi("SELECT * FROM Win32_CDROMDrive");
        int floppy = CountWmi("SELECT * FROM Win32_FloppyDrive");
        return new LegacyStorageState(optical, floppy);
    }

    private static int CountWmi(string query)
    {
        int count = 0;
        using var searcher = new ManagementObjectSearcher(query);
        foreach (ManagementObject o in searcher.Get())
            using (o) count++;
        return count;
    }

    // ── PV-001 遙測層級事實 ───────────────────────────────────────────────

    public static HardwareFact TelemetryFact(DateTimeOffset at, TelemetryProbe? probe = null)
    {
        int? value;
        try { value = (probe ?? new TelemetryProbe(ReadTelemetryPolicy))(); }
        catch { value = null; }
        if (value is null)
            return Unavailable(at, TelemetryKey, CatPrivacy, "遙測層級事實", FactAvailability.NotApplicable,
                "AllowTelemetry 未由政策設定——不代填依版本而異的 Windows 預設");

        string label = value switch
        {
            0 => "安全性（0）",
            1 => "基本（1）",
            2 => "增強（2）",
            3 => "完整（3）",
            _ => $"未知（原始值 {value}）",
        };
        return new HardwareFact(TelemetryKey, CatPrivacy, "遙測層級事實",
            $"政策 AllowTelemetry＝{label}。只報政策字面值，不判隱私好壞；未設定不代填預設。",
            "", @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\AllowTelemetry", FactTrustLevel.Reported, false, at, value);
    }

    private static int? ReadTelemetryPolicy()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection");
        if (key?.GetValue("AllowTelemetry") is not { } v) return null;
        return Convert.ToInt32(v, CultureInfo.InvariantCulture);
    }

    // ── TM-004 時區／DST 資源 ─────────────────────────────────────────────

    public static HardwareFact TimeZoneFact(DateTimeOffset at, TimeZoneProbe? probe = null)
    {
        TimeZoneState? state;
        try { state = (probe ?? new TimeZoneProbe(ReadTimeZoneState))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, TimeZoneKey, CatTime, "時區／DST 資源", FactAvailability.ReadError,
                "TimeZoneInfo 或 tzres.dll 讀取失敗——不猜時區資料版本");

        string tzres = state.TzResVersion ?? "未回報";
        return new HardwareFact(TimeZoneKey, CatTime, "時區／DST 資源",
            $"目前時區 {state.Id}；位移 UTC{FormatOffset(state.UtcOffset)}；支援日光節約 {(state.SupportsDaylightSavingTime ? "是" : "否")}；tzres.dll 檔案版本 {tzres}。" +
            "界線：Windows 公開 API 不提供 IANA tzdb 版本；tzres.dll 是 Windows 資源版本，不冒充 tzdb 版本。",
            "", "TimeZoneInfo.Local＋%SystemRoot%\\System32\\tzres.dll", FactTrustLevel.Reported, false, at);
    }

    private static TimeZoneState ReadTimeZoneState()
    {
        var tz = TimeZoneInfo.Local;
        string path = Path.Combine(Environment.SystemDirectory, "tzres.dll");
        string? version = null;
        try { if (File.Exists(path)) version = FileVersionInfo.GetVersionInfo(path).FileVersion; }
        catch { }
        return new TimeZoneState(tz.Id, tz.DisplayName, tz.BaseUtcOffset, tz.SupportsDaylightSavingTime, version);
    }

    // ── PV-003 診斷記錄器狀態 ─────────────────────────────────────────────

    public static HardwareFact DiagnosticViewerFact(DateTimeOffset at, DiagnosticLoggerProbe? probe = null)
    {
        DiagnosticLoggerState? state;
        try { state = (probe ?? new DiagnosticLoggerProbe(ReadDiagnosticLoggers))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, DiagnosticViewerKey, CatPrivacy, "診斷記錄器狀態", FactAvailability.ReadError,
                "WMI Autologger 登錄讀取失敗——不猜自動記錄器狀態");
        if (state.TotalLoggers == 0)
            return Unavailable(at, DiagnosticViewerKey, CatPrivacy, "診斷記錄器狀態", FactAvailability.NotApplicable,
                "沒有回報自動記錄器（0 是值）");

        return new HardwareFact(DiagnosticViewerKey, CatPrivacy, "診斷記錄器狀態",
            $"自動記錄器 {state.TotalLoggers} 個：啟用 {state.EnabledLoggers}、停用 {state.DisabledLoggers}、其他 {state.TotalLoggers - state.EnabledLoggers - state.DisabledLoggers}。" +
            "只讀登錄狀態，不啟停記錄器、不讀事件內容。",
            "個", @"HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger", FactTrustLevel.Reported, false, at, state.TotalLoggers);
    }

    private static DiagnosticLoggerState ReadDiagnosticLoggers()
    {
        using var root = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\WMI\Autologger");
        if (root is null) return new DiagnosticLoggerState(0, 0, 0);
        int total = 0, enabled = 0, disabled = 0;
        foreach (string name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key is null) continue;
            total++;
            int? start = key.GetValue("Start") is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : null;
            if (start == 1) enabled++;
            else if (start == 0) disabled++;
        }
        return new DiagnosticLoggerState(total, enabled, disabled);
    }

    // ── 共用工具 ──────────────────────────────────────────────────────────

    private static long ToLong(object? value)
    {
        try { return value is null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        catch { return 0L; }
    }

    private static string YesNo(bool value) => value ? "是" : "否";

    private static string FormatOffset(TimeSpan offset) => (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}