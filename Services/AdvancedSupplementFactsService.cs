using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// Vol2 先進補充批次（第 163–172 項）：CM-005 快取共享拓撲、DV-002 巢狀虛擬化能力、
/// DV-003 .NET／VC++ 執行階段、PW-008 排程喚醒對照、GD-004 多螢幕拓撲快照、
/// DK-005 匿名 ID 穩定性、DK-009 匯出／匯入往返、SC-004 I/O 來源聚合、
/// MU-003 DRM 就緒、MU-006 空間音訊。
/// </summary>
/// <remarks>
/// <para><b>全部唯讀</b>：CPUID／登錄／WMI／schtasks XML／DisplayConfig 只讀；不啟動或停止服務、
/// 不切換電源計畫、不改任務、不開啟音訊或顯示裝置。</para>
/// <para><b>誠實界線</b>：讀不到用 <see cref="FactAvailability.ReadError"/> 附原因，不用 0／false
/// 冒充；0 個項目只有在來源真的回報空集合時才是值。序號、UUID、產品金鑰與本機路徑不進值。</para>
/// </remarks>
public static class AdvancedSupplementFactsService
{
    private const string CatTopology = "處理器與平台";
    private const string CatVirtualization = "虛擬化";
    private const string CatRuntime = "環境與開發";
    private const string CatPower = "電源與恢復";
    private const string CatDisplay = "顯示卡";
    private const string CatGovernance = "診斷治理";
    private const string CatStorage = "儲存";
    private const string CatMedia = "多媒體";

    public const string CacheShareKey = "cm.cache.share.summary";
    public const string NestedVirtualizationKey = "dv.nested";
    public const string RuntimeStackKey = "dv.runtime.summary";
    public const string WakeTaskKey = "pw.waketask.summary";
    public const string MonitorTopologyKey = "hist.mon.summary";
    public const string AnonymousIdKey = "dk.anon.stability";
    public const string RoundTripKey = "dk.roundtrip";
    public const string DiskIoKey = "sc.io.summary";
    public const string SpatialAudioKey = "mu.spatial.summary";
    public const string DrmKey = "mu.drm.summary";

    public const string MonitorTopologyFormatVersion = "mon-v1";

    public sealed record CacheShareRow(string Level, long CapacityBytes, uint SharedLogical, bool Inclusive);
    public sealed record NestedVirtualizationState(bool HypervisorPresent, bool? FirmwareVirtualizationEnabled,
        string VbsStatus, int OptionalFeatureCount);
    public sealed record MonitorTopology(int X, int Y, int Width, int Height, uint DpiX, uint DpiY, bool Primary);
    public sealed record DiskIoSource(string Name, long ReadBytes, long WriteBytes);
    public sealed record SpatialAudioState(int EndpointCount, int SpatialEnabledEndpoints);
    public sealed record DrmState(bool Registered, string? Version);

    public delegate IReadOnlyList<CacheShareRow>? CacheShareProbe();
    public delegate NestedVirtualizationState? NestedVirtualizationProbe();
    public delegate IReadOnlyDictionary<string, string>? RuntimeProbe();
    public delegate string? WakeTaskProbe();
    public delegate IReadOnlyList<MonitorTopology>? MonitorTopologyProbe();
    public delegate string? AnonymousIdentityProbe();
    public delegate IReadOnlyDictionary<string, string>? RoundTripProbe();
    public delegate IReadOnlyList<DiskIoSource>? DiskIoProbe();
    public delegate SpatialAudioState? SpatialAudioProbe();
    public delegate DrmState? DrmProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at)
    {
        var facts = new List<HardwareFact>(10)
        {
            CacheShareFact(at),
            NestedVirtualizationFact(at),
            RuntimeStackFact(at),
            WakeTaskFact(at),
            MonitorTopologyFact(at),
            AnonymousIdFact(at),
            RoundTripFact(at),
            DiskIoFact(at),
            SpatialAudioFact(at),
            DrmFact(at),
        };
        return facts;
    }

    // ── CM-005 快取共享拓撲 ───────────────────────────────────────────────

    /// <summary>
    /// CPUID leaf 0x04 的共享遮罩。SharedLogical 是硬體回報的可定址邏輯處理器上界，
    /// 不是實體核心數；輸出文字會明說這個界線。
    /// </summary>
    public static HardwareFact CacheShareFact(DateTimeOffset at, CacheShareProbe? probe = null)
    {
        IReadOnlyList<CacheShareRow>? rows;
        try { rows = (probe ?? new CacheShareProbe(ReadCacheSharing))(); }
        catch { rows = null; }
        if (rows is null)
            return Unavailable(at, CacheShareKey, CatTopology, "快取共享拓撲", FactAvailability.NotSupported,
                "CPUID leaf 0x04 不可用或超出最大葉位——不猜快取共享關係");
        if (rows.Count == 0)
            return Unavailable(at, CacheShareKey, CatTopology, "快取共享拓撲", FactAvailability.NotApplicable,
                "平台沒有回報任何快取子葉（0 是值，不是讀取失敗）");

        var ordered = rows.OrderBy(r => r.Level, StringComparer.Ordinal).ThenByDescending(r => r.CapacityBytes).ToList();
        string text = string.Join("；", ordered.Select(r =>
            $"{r.Level} {FormatBytes(r.CapacityBytes)}・最多 {r.SharedLogical} 邏輯處理器" + (r.Inclusive ? "・含括" : "・非含括")));
        return new HardwareFact(CacheShareKey, CatTopology, "快取共享拓撲",
            text + "。界線：SharedLogical 是 CPUID 回報的上界（常被向上取成 2 的冪次），不是實際核心數或目前排程關係。",
            "", "CPUID leaf 0x04（Intel SDM／AMD APM 快取幾何）", FactTrustLevel.Derived, false, at, ordered.Count);
    }

    private static IReadOnlyList<CacheShareRow>? ReadCacheSharing()
    {
        if (!X86Base.IsSupported) return null;
        uint maxStd = (uint)X86Base.CpuId(0, 0).Eax;
        if (maxStd < 0x04) return null;
        var rows = new List<CacheShareRow>();
        for (uint sub = 0; sub < 64; sub++)
        {
            var r = X86Base.CpuId(0x04, (int)sub);
            var raw = CpuIdService.Decoder.DecodeCacheRaw((uint)r.Eax, (uint)r.Ebx, (uint)r.Ecx, (uint)r.Edx);
            if (raw is null) break;
            rows.Add(new CacheShareRow(raw.Value.LevelName, raw.Value.CapacityBytes, raw.Value.SharedLogical,
                (raw.Value.EdxFlags & 0x2) != 0));
        }
        return rows;
    }

    // ── DV-002 巢狀虛擬化能力 ─────────────────────────────────────────────

    public static HardwareFact NestedVirtualizationFact(DateTimeOffset at, NestedVirtualizationProbe? probe = null)
    {
        NestedVirtualizationState? state;
        try { state = (probe ?? new NestedVirtualizationProbe(ReadNestedVirtualization))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, NestedVirtualizationKey, CatVirtualization, "巢狀虛擬化能力", FactAvailability.ReadError,
                "Hypervisor／VBS／WMI 虛擬化能力查詢失敗——讀不到不推算");

        string fw = state.FirmwareVirtualizationEnabled switch
        {
            true => "已啟用",
            false => "未啟用",
            null => "未回報",
        };
        string optional = state.OptionalFeatureCount < 0 ? "未回報" : state.OptionalFeatureCount.ToString(CultureInfo.InvariantCulture) + " 項";
        return new HardwareFact(NestedVirtualizationKey, CatVirtualization, "巢狀虛擬化能力",
            $"Hypervisor 存在={YesNo(state.HypervisorPresent)}；韌體虛擬化={fw}；VBS={Clean(state.VbsStatus)}；" +
            $"虛擬化相關選用功能={optional}。界線：這些是能力與平台狀態的輸入，不宣稱來賓 VM 一定能巢狀執行，也不判隔離強度。",
            "", "Win32_ComputerSystem／Win32_Processor／Win32_DeviceGuard／Win32_OptionalFeature", FactTrustLevel.Reported,
            false, at, null, FactAvailability.Present);
    }

    private static NestedVirtualizationState? ReadNestedVirtualization()
    {
        try
        {
            bool hypervisor = false;
            bool? firmware = null;
            using (var searcher = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem"))
            foreach (ManagementObject o in searcher.Get())
                using (o)
                    hypervisor = o["HypervisorPresent"] is bool b && b;

            using (var searcher = new ManagementObjectSearcher("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor"))
            foreach (ManagementObject o in searcher.Get())
                using (o)
                    if (o["VirtualizationFirmwareEnabled"] is bool b)
                        firmware = b;

            string vbs = "未回報";
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard");
                foreach (ManagementObject o in searcher.Get())
                    using (o)
                        vbs = o["VirtualizationBasedSecurityStatus"]?.ToString() ?? "未回報";
            }
            catch { /* Device Guard 類別可能不存在；保留未回報 */ }

            int optional = -1;
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_OptionalFeature WHERE InstallState=1");
                int n = 0;
                foreach (ManagementObject o in searcher.Get())
                    using (o)
                        n++;
                optional = n;
            }
            catch { /* 選用功能通道可能不可用；不把失敗讀成 0 */ }

            return new NestedVirtualizationState(hypervisor, firmware, vbs, optional);
        }
        catch { return null; }
    }

    // ── DV-003 .NET／VC++ 執行階段 ────────────────────────────────────────

    public static HardwareFact RuntimeStackFact(DateTimeOffset at, RuntimeProbe? probe = null)
    {
        IReadOnlyDictionary<string, string>? map;
        try { map = (probe ?? new RuntimeProbe(ReadRuntimeStack))(); }
        catch { map = null; }
        if (map is null)
            return Unavailable(at, RuntimeStackKey, CatRuntime, ".NET／VC++ 執行階段", FactAvailability.ReadError,
                "執行階段登錄或目前 CLR 查詢失敗——讀不到不推測已安裝版本");
        if (map.Count == 0)
            return Unavailable(at, RuntimeStackKey, CatRuntime, ".NET／VC++ 執行階段", FactAvailability.NotApplicable,
                "登錄沒有回報 .NET Framework／VC++ 執行階段（0 是值；不代表其他封裝形式不存在）");

        string text = string.Join("、", map.OrderBy(k => k.Key, StringComparer.Ordinal)
            .Select(k => $"{k.Key}={Clean(k.Value)}"));
        return new HardwareFact(RuntimeStackKey, CatRuntime, ".NET／VC++ 執行階段",
            text + "。界線：登錄與目前程序 CLR 版本只說明這些元件被回報；不代表專案或應用程式實際使用該版本，也不做 EOL 判決。",
            "項", @"HKLM\SOFTWARE\Microsoft\NET Framework Setup／VisualStudio VC Runtimes＋RuntimeInformation",
            FactTrustLevel.Reported, false, at, map.Count);
    }

    private static IReadOnlyDictionary<string, string> ReadRuntimeStack()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        map["CLR"] = RuntimeInformation.FrameworkDescription;
        map["目前 CLR 版本"] = Environment.Version.ToString();

        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
        {
            string? version = key?.GetValue("Version")?.ToString();
            object? release = key?.GetValue("Release");
            if (!string.IsNullOrWhiteSpace(version))
                map[".NET Framework 4.x"] = release is null ? version : $"{version}（Release {release}）";
        }

        AddRuntimeRegistryValue(map, "VC++ x64", @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
        AddRuntimeRegistryValue(map, "VC++ x86", @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86");
        AddRuntimeRegistryValue(map, "VC++ x64（WOW6432）", @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
        AddRuntimeRegistryValue(map, "VC++ x86（WOW6432）", @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86");
        return map;
    }

    private static void AddRuntimeRegistryValue(IDictionary<string, string> map, string label, string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            string? value = key?.GetValue("Version")?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) map[label] = value;
        }
        catch { /* 單一路徑失敗不影響其他執行階段事實 */ }
    }

    // ── PW-008 排程喚醒對照 ───────────────────────────────────────────────

    public static HardwareFact WakeTaskFact(DateTimeOffset at, WakeTaskProbe? probe = null)
    {
        string? xml;
        try { xml = (probe ?? new WakeTaskProbe(ReadWakeTaskXml))(); }
        catch { xml = null; }
        if (xml is null)
            return Unavailable(at, WakeTaskKey, CatPower, "排程喚醒對照", FactAvailability.ReadError,
                "schtasks /query /xml 讀取失敗——不猜有哪些任務可喚醒電腦");

        int wakeTasks;
        try
        {
            var doc = XDocument.Parse(xml);
            wakeTasks = doc.Descendants()
                .Where(e => e.Name.LocalName.Equals("WakeToRun", StringComparison.OrdinalIgnoreCase))
                .Count(e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return Unavailable(at, WakeTaskKey, CatPower, "排程喚醒對照", FactAvailability.ReadError,
                "schtasks XML 格式無法解析——不把解析失敗讀成沒有喚醒任務");
        }

        return new HardwareFact(WakeTaskKey, CatPower, "排程喚醒對照",
            $"WakeToRun=true 的排程工作 {wakeTasks} 個。界線：只陳述任務設定，不代表電腦一定會被喚醒，也不列出任務名稱或命令。",
            "個", "schtasks.exe /query /xml ONE（唯讀 XML，不修改任務）", FactTrustLevel.Reported, false, at, wakeTasks);
    }

    private static string? ReadWakeTaskXml() => RunProcess("schtasks.exe", ["/query", "/xml", "ONE"], 15000);

    // ── GD-004 多螢幕拓撲快照 ─────────────────────────────────────────────

    public static HardwareFact MonitorTopologyFact(DateTimeOffset at, MonitorTopologyProbe? probe = null)
    {
        IReadOnlyList<MonitorTopology>? monitors;
        try { monitors = (probe ?? new MonitorTopologyProbe(ReadMonitorTopology))(); }
        catch { monitors = null; }
        if (monitors is null)
            return Unavailable(at, MonitorTopologyKey, CatDisplay, "多螢幕拓撲快照", FactAvailability.ReadError,
                "EnumDisplayMonitors／GetMonitorInfo 讀取失敗——不猜螢幕數量或排列");
        if (monitors.Count == 0)
            return Unavailable(at, MonitorTopologyKey, CatDisplay, "多螢幕拓撲快照", FactAvailability.NotApplicable,
                "作業系統沒有回報任何顯示器（0 是值，不是讀取失敗）");

        var sorted = monitors.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
        string text = string.Join("；", sorted.Select((m, i) =>
            $"#{i + 1}{(m.Primary ? "主" : "")} {m.Width}×{m.Height}@({m.X},{m.Y})" +
            (m.DpiX == 0 || m.DpiY == 0 ? " DPI未回報" : $" {m.DpiX}/{m.DpiY} DPI")));
        return new HardwareFact(MonitorTopologyKey, CatDisplay, "多螢幕拓撲快照",
            $"{sorted.Count} 個顯示器：{text}。拓撲格式 {MonitorTopologyFormatVersion}；只記排列與 DPI，不記序號、EDID 或畫面內容。",
            "個", "EnumDisplayMonitors／GetMonitorInfo／GetDpiForMonitor（唯讀）", FactTrustLevel.Measured, false, at,
            sorted.Count);
    }

    private static IReadOnlyList<MonitorTopology>? ReadMonitorTopology()
    {
        var result = new List<MonitorTopology>();
        bool Callback(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data)
        {
            try
            {
                var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
                if (!GetMonitorInfo(monitor, ref info)) return true;
                uint dpiX = 0, dpiY = 0;
                try { _ = GetDpiForMonitor(monitor, 0, out dpiX, out dpiY); }
                catch { /* shcore 不存在時 DPI 未回報 */ }
                result.Add(new MonitorTopology(info.rcMonitor.Left, info.rcMonitor.Top,
                    info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top,
                    dpiX, dpiY, (info.dwFlags & 1) != 0));
            }
            catch { /* 單一監視器失敗不讓整組讀值冒充空集合 */ }
            return true;
        }

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero)) return null;
        return result;
    }

    // ── DK-005 匿名 ID 穩定性 ─────────────────────────────────────────────

    public static HardwareFact AnonymousIdFact(DateTimeOffset at, AnonymousIdentityProbe? probe = null)
    {
        string? identity;
        try { identity = (probe ?? new AnonymousIdentityProbe(ReadAnonymousIdentity))(); }
        catch { identity = null; }
        if (identity is null)
            return Unavailable(at, AnonymousIdKey, CatGovernance, "匿名 ID 穩定性", FactAvailability.ReadError,
                "匿名識別來源讀取失敗——不產生可能漂移的假 ID");
        if (string.IsNullOrWhiteSpace(identity))
            return Unavailable(at, AnonymousIdKey, CatGovernance, "匿名 ID 穩定性", FactAvailability.NotApplicable,
                "沒有可用的匿名識別欄位（0 是值；不讀序號或 UUID）");

        string normalized = NormalizeIdentity(identity);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return new HardwareFact(AnonymousIdKey, CatGovernance, "匿名 ID 穩定性",
            $"匿名識別 {hash[..16]}（16 位十六進位）。以正規化硬體描述計算；同一組描述穩定，不用序號、UUID、產品金鑰或路徑。" +
            "界線：這是可重現識別輔助，不是跨機器唯一性保證；來源改變時會如實漂移。",
            "", "Win32_ComputerSystem／Win32_Processor 的正常化描述＋SHA-256", FactTrustLevel.Derived, false, at);
    }

    private static string? ReadAnonymousIdentity()
    {
        try
        {
            string manufacturer = "", model = "", cpu = "";
            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            foreach (ManagementObject o in searcher.Get())
                using (o)
                {
                    manufacturer = o["Manufacturer"]?.ToString() ?? "";
                    model = o["Model"]?.ToString() ?? "";
                }
            using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
            foreach (ManagementObject o in searcher.Get())
                using (o) cpu = o["Name"]?.ToString() ?? "";
            return $"{manufacturer}|{model}|{cpu}";
        }
        catch { return null; }
    }

    private static string NormalizeIdentity(string value) =>
        string.Join("|", value.Split('|').Select(p => p.Trim().ToUpperInvariant()));

    // ── DK-009 匯出／匯入往返 ─────────────────────────────────────────────

    public static HardwareFact RoundTripFact(DateTimeOffset at, RoundTripProbe? probe = null)
    {
        IReadOnlyDictionary<string, string>? values;
        try { values = (probe ?? new RoundTripProbe(ReadRoundTripValues))(); }
        catch { values = null; }
        if (values is null)
            return Unavailable(at, RoundTripKey, CatGovernance, "匯出／匯入往返", FactAvailability.ReadError,
                "設定／歷史往返來源讀取失敗——不宣稱往返成功");

        var ordered = values.OrderBy(k => k.Key, StringComparer.Ordinal)
            .ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
        string json = JsonSerializer.Serialize(ordered);
        Dictionary<string, string>? parsed;
        try { parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch { parsed = null; }
        if (parsed is null)
            return Unavailable(at, RoundTripKey, CatGovernance, "匯出／匯入往返", FactAvailability.ReadError,
                "JSON 匯出後無法重新解析——往返失敗，不留下假成功");

        bool equal = parsed.Count == ordered.Count && parsed.All(kv => ordered.TryGetValue(kv.Key, out var v) && v == kv.Value);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()[..12];
        return new HardwareFact(RoundTripKey, CatGovernance, "匯出／匯入往返",
            equal
                ? $"往返 {parsed.Count} 個鍵成功；內容 digest {digest}（不列出值）。界線：驗證序列化/解析一致性，不代表外部檔案系統或備份媒體可靠。"
                : $"往返 {parsed.Count} 個鍵後內容不一致；digest {digest}。這不是成功，應保留原始匯出物再查。",
            "鍵", "System.Text.Json 記憶體往返（不寫檔）", FactTrustLevel.Derived, false, at, parsed.Count);
    }

    private static IReadOnlyDictionary<string, string> ReadRoundTripValues()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schema"] = "xinspect-governance-v1",
            ["anonymous"] = "true",
            ["source"] = "read-only",
        };
    }

    // ── SC-004 誰在動硬碟 ─────────────────────────────────────────────────

    public static HardwareFact DiskIoFact(DateTimeOffset at, DiskIoProbe? probe = null)
    {
        IReadOnlyList<DiskIoSource>? sources;
        try { sources = (probe ?? new DiskIoProbe(ReadDiskIoSources))(); }
        catch { sources = null; }
        if (sources is null)
            return Unavailable(at, DiskIoKey, CatStorage, "誰在動硬碟", FactAvailability.ReadError,
                "PerformanceData 程序 I/O 計數讀取失敗——不猜哪個程序造成磁碟活動");

        long totalRead = sources.Sum(s => Math.Max(0, s.ReadBytes));
        long totalWrite = sources.Sum(s => Math.Max(0, s.WriteBytes));
        string top = string.Join("、", sources.OrderByDescending(s => s.ReadBytes + s.WriteBytes)
            .ThenBy(s => s.Name, StringComparer.Ordinal).Take(5)
            .Select(s => $"{Clean(s.Name)} {FormatBytes(Math.Max(0, s.ReadBytes) + Math.Max(0, s.WriteBytes))}"));
        return new HardwareFact(DiskIoKey, CatStorage, "誰在動硬碟",
            $"程序 I/O 樣本 {sources.Count} 筆；累計讀 {FormatBytes(totalRead)}、寫 {FormatBytes(totalWrite)}" +
            (top.Length > 0 ? $"；前五大：{top}" : "；目前沒有可列出程序") +
            "。界線：這是 since-boot 的計數器聚合，不是即時磁碟佔用率，也不把單一程序當根因。",
            "bytes", "Win32_PerfFormattedData_PerfProc_Process（唯讀）", FactTrustLevel.Reported, false, at,
            sources.Count);
    }

    private static IReadOnlyList<DiskIoSource> ReadDiskIoSources()
    {
        var rows = new List<DiskIoSource>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, ReadTransferCount, WriteTransferCount FROM Win32_PerfFormattedData_PerfProc_Process");
        foreach (ManagementObject o in searcher.Get())
            using (o)
            {
                string name = o["Name"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(name) || name is "_Total" or "Idle") continue;
                rows.Add(new DiskIoSource(name, ToLong(o["ReadTransferCount"]), ToLong(o["WriteTransferCount"])));
            }
        return rows;
    }

    // ── MU-003 DRM 就讀 ───────────────────────────────────────────────────

    public static HardwareFact DrmFact(DateTimeOffset at, DrmProbe? probe = null)
    {
        DrmState? state;
        try { state = (probe ?? new DrmProbe(ReadDrm))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, DrmKey, CatMedia, "DRM 就緒", FactAvailability.ReadError,
                "PlayReady 登錄讀取失敗——不把未知讀成未安裝");
        if (!state.Registered)
            return Unavailable(at, DrmKey, CatMedia, "DRM 就緒", FactAvailability.NotApplicable,
                "沒有 PlayReady 登錄（0 是值；不代表其他 DRM 或串流服務一定不可用）");
        return new HardwareFact(DrmKey, CatMedia, "DRM 就緒",
            $"PlayReady 已登錄；版本 {Clean(state.Version)}。界線：這只說明媒體 DRM 元件存在，不測播放、不讀授權或內容金鑰。",
            "", @"HKLM\SOFTWARE\Microsoft\PlayReady", FactTrustLevel.Reported, false, at);
    }

    private static DrmState? ReadDrm()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\PlayReady")
                ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\PlayReady");
            if (key is null) return new DrmState(false, null);
            string? version = key.GetValue("CurrentVersion")?.ToString()
                ?? key.GetValue("Version")?.ToString();
            return new DrmState(true, version);
        }
        catch { return null; }
    }

    // ── MU-006 空間音訊 ───────────────────────────────────────────────────

    public static HardwareFact SpatialAudioFact(DateTimeOffset at, SpatialAudioProbe? probe = null)
    {
        SpatialAudioState? state;
        try { state = (probe ?? new SpatialAudioProbe(ReadSpatialAudio))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, SpatialAudioKey, CatMedia, "空間音訊", FactAvailability.ReadError,
                "MMDevices 音訊端點登錄讀取失敗——不猜空間音訊是否可用");
        if (state.EndpointCount == 0)
            return Unavailable(at, SpatialAudioKey, CatMedia, "空間音訊", FactAvailability.NotApplicable,
                "沒有渲染音訊端點（0 是值；沒有端點不等於空間音訊格式不支援）");
        return new HardwareFact(SpatialAudioKey, CatMedia, "空間音訊",
            $"渲染端點 {state.EndpointCount} 個，登錄宣告啟用空間音訊者 {state.SpatialEnabledEndpoints} 個。" +
            "界線：只讀端點登錄，不開音訊裝置、不測試空間效果，也不判音質。",
            "個", @"HKLM\...\MMDevices\Audio\Render（只讀端點屬性）", FactTrustLevel.Reported, false, at,
            state.EndpointCount);
    }

    private static SpatialAudioState? ReadSpatialAudio()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render");
            if (root is null) return new SpatialAudioState(0, 0);
            int endpoints = 0, spatial = 0;
            foreach (string endpoint in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(endpoint);
                if (sub is null) continue;
                endpoints++;
                if (EndpointDeclaresSpatial(sub)) spatial++;
            }
            return new SpatialAudioState(endpoints, spatial);
        }
        catch { return null; }
    }

    private static bool EndpointDeclaresSpatial(RegistryKey endpoint)
    {
        using var properties = endpoint.OpenSubKey("Properties");
        if (properties is null) return false;
        foreach (string name in properties.GetValueNames())
        {
            if (!name.Contains("Spatial", StringComparison.OrdinalIgnoreCase)) continue;
            object? value = properties.GetValue(name);
            if (value is int i && i != 0) return true;
            if (value is bool b && b) return true;
            if (value is string s && (s.Equals("1", StringComparison.Ordinal) ||
                                     s.Contains("enabled", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    // ── 共用工具 ──────────────────────────────────────────────────────────

    private static long ToLong(object? value)
    {
        try { return value is null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        catch { return 0L; }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.#} KiB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MiB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB";
    }

    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? "未回報" : value.Trim();
    private static string YesNo(bool value) => value ? "是" : "否";

    private static string? RunProcess(string fileName, string[] args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null) return null;
            string stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return process.ExitCode == 0 ? stdout : null;
        }
        catch { return null; }
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll", SetLastError = true)]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}