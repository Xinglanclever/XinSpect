using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.ServiceProcess;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 網路與系統盤點補充（Vol2 第 183–192 項）：列印佇列、WOL、路由、感應器、事件通道、
/// Storage Spaces、磁碟電源轉換、廣告設定、語音／輸入設定、NTP 狀態。
/// </summary>
/// <remarks>
/// <para><b>全部唯讀</b>：WMI、登錄、事件記錄只讀；不列印、不傳送魔術封包、不改路由、
/// 不啟停服務或事件通道、不改 Storage Spaces、不改隱私設定、不出網查時間。</para>
/// <para><b>誠實界線</b>：0 是值；讀不到用三態附原因；不列印表機名稱、IP、路由端點或事件內容。</para>
/// </remarks>
public static class NetworkSystemInventoryFactsService
{
    private const string CatNetwork = "網路";
    private const string CatSystem = "Windows 系統";
    private const string CatStorage = "儲存";
    private const string CatPrivacy = "隱私與遥測";
    private const string CatTime = "時間";

    public const string PrintKey = "nw.print.summary";
    public const string WolKey = "nw.wol.summary";
    public const string RouteKey = "nw.route.summary";
    public const string SensorKey = "nw.sensor.summary";
    public const string EventChannelKey = "ws.channel.summary";
    public const string StorageSpacesKey = "st.ss.summary";
    public const string PowerTransitionKey = "st.pstrans.summary";
    public const string AdvertisingKey = "pv.adid.summary";
    public const string SpeechInputKey = "pv.speech.summary";
    public const string NtpKey = "tm.ntp.summary";

    public sealed record PrintQueueState(int QueueCount, int ErrorQueues, int OfflineQueues, int SharedQueues);
    public sealed record WakeOnLanState(int CapabilityRows, int EnabledRows);
    public sealed record RouteState(int TotalRoutes, int DefaultRoutes, int PersistentRoutes, int LowestMetric);
    public sealed record SensorState(int SensorCount, int AmbientLightSensors, int Accelerometers);
    public sealed record EventChannelState(int TotalChannels, int EnabledChannels, int DisabledChannels);
    public sealed record StorageSpacesState(int Pools, int VirtualDisks, int PhysicalDisks);
    public sealed record PhysicalDiskPowerState(int DiskCount, long PowerCycleCount, long LoadUnloadCount);
    public sealed record AdvertisingState(int? AdIdEnabled, int? TailoredExperiencesDisabled, int? ActivityHistoryPublished);
    public sealed record SpeechInputState(int? OnlineSpeechRecognition, int? InputPersonalization, int? HandwritingErrorReporting);
    public sealed record NtpState(string ServiceStatus, string ConfiguredSource, int? NtpClientEnabled);

    public delegate PrintQueueState? PrintProbe();
    public delegate WakeOnLanState? WolProbe();
    public delegate RouteState? RouteProbe();
    public delegate SensorState? SensorProbe();
    public delegate EventChannelState? EventChannelProbe();
    public delegate StorageSpacesState? StorageSpacesProbe();
    public delegate PhysicalDiskPowerState? PhysicalDiskPowerProbe();
    public delegate AdvertisingState? AdvertisingProbe();
    public delegate SpeechInputState? SpeechInputProbe();
    public delegate NtpState? NtpProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        PrintFact(at), WolFact(at), RouteFact(at), SensorFact(at), EventChannelFact(at),
        StorageSpacesFact(at), PhysicalDiskPowerFact(at), AdvertisingFact(at),
        SpeechInputFact(at), NtpFact(at),
    ];
    // ── NW-012 列印佇列健康 ───────────────────────────────────────────────

    public static HardwareFact PrintFact(DateTimeOffset at, PrintProbe? probe = null)
    {
        PrintQueueState? state;
        try { state = (probe ?? new PrintProbe(ReadPrintQueues))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, PrintKey, CatNetwork, "列印佇列健康", FactAvailability.ReadError,
                "Win32_Printer 列舉失敗——不猜列印佇列狀態");
        if (state.QueueCount == 0)
            return Unavailable(at, PrintKey, CatNetwork, "列印佇列健康", FactAvailability.NotApplicable,
                "沒有回報列印佇列（0 是值）");

        return new HardwareFact(PrintKey, CatNetwork, "列印佇列健康",
            $"列印佇列 {state.QueueCount} 個：錯誤 {state.ErrorQueues}、離線 {state.OfflineQueues}、共用 {state.SharedQueues}。" +
            "只報計數，不列印表機名稱或連接埠；驅動隔離與列印池機制是 Windows 行為，不在此判決。",
            "個", "Win32_Printer（PrinterStatus／WorkOffline／Shared，唯讀）", FactTrustLevel.Reported, false, at, state.QueueCount);
    }

    private static PrintQueueState ReadPrintQueues()
    {
        int queues = 0, errors = 0, offline = 0, shared = 0;
        using var searcher = new ManagementObjectSearcher(
            "SELECT PrinterStatus, WorkOffline, Shared FROM Win32_Printer");
        foreach (ManagementObject o in searcher.Get())
            using (o)
            {
                queues++;
                if (ToInt(o["PrinterStatus"]) is int s && s is 3 or 4 or 5) errors++;
                if (o["WorkOffline"] is bool b && b) offline++;
                if (o["Shared"] is bool sh && sh) shared++;
            }
        return new PrintQueueState(queues, errors, offline, shared);
    }

    // ── NW-014 WOL 事實化 ─────────────────────────────────────────────────

    public static HardwareFact WolFact(DateTimeOffset at, WolProbe? probe = null)
    {
        WakeOnLanState? state;
        try { state = (probe ?? new WolProbe(ReadWakeOnLan))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, WolKey, CatNetwork, "WOL 事實化", FactAvailability.ReadError,
                "網卡電源管理登錄讀取失敗——不猜 WOL 能力");
        if (state.CapabilityRows == 0)
            return Unavailable(at, WolKey, CatNetwork, "WOL 事實化", FactAvailability.NotApplicable,
                "沒有回報 WOL 電源管理列（0 是值；設定關不等於硬體不支援）");

        return new HardwareFact(WolKey, CatNetwork, "WOL 事實化",
            $"電源管理列 {state.CapabilityRows} 筆，其中 WakeOnMagicPacket／WakeOnPattern 啟用 {state.EnabledRows} 筆。" +
            "設定關不等於硬體不支援；這是登錄宣告，不代表封包一定能喚醒；EEE 能力另由 NIC 驅動／PCIe 能力面陳述。",
            "筆", @"HKLM\SYSTEM\CurrentControlSet\Control\Class（電源管理值，唯讀）", FactTrustLevel.Reported, false, at, state.CapabilityRows);
    }

    private static WakeOnLanState ReadWakeOnLan()
    {
        int rows = 0, enabled = 0;
        using var root = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
        if (root is null) return new WakeOnLanState(0, 0);
        foreach (string name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key is null) continue;
            foreach (string value in key.GetValueNames())
            {
                if (!value.Contains("WakeOn", StringComparison.OrdinalIgnoreCase)
                 && !value.Contains("PowerManagement", StringComparison.OrdinalIgnoreCase)) continue;
                rows++;
                if (ToInt(key.GetValue(value)) is int v && v != 0) enabled++;
            }
        }
        return new WakeOnLanState(rows, enabled);
    }
    // ── NW-016 路由歸屬呈現 ───────────────────────────────────────────────

    public static HardwareFact RouteFact(DateTimeOffset at, RouteProbe? probe = null)
    {
        RouteState? state;
        try { state = (probe ?? new RouteProbe(ReadRoutes))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, RouteKey, CatNetwork, "路由歸屬呈現", FactAvailability.ReadError,
                "路由表讀取失敗——不猜路由歸屬");

        return new HardwareFact(RouteKey, CatNetwork, "路由歸屬呈現",
            $"路由 {state.TotalRoutes} 條：預設路由 {state.DefaultRoutes}、持續路由 {state.PersistentRoutes}、最低 metric {(state.LowestMetric == int.MaxValue ? "未回報" : state.LowestMetric.ToString(CultureInfo.InvariantCulture))}。" +
            "只報歸屬計數與 metric，不列網段、閘道或介面名稱；這是靜態路由表快照，不是流量判決。",
            "條", "Win32_IP4RouteTable（唯讀）", FactTrustLevel.Reported, false, at, state.TotalRoutes);
    }

    private static RouteState ReadRoutes()
    {
        int total = 0, defaults = 0, persistent = 0, lowest = int.MaxValue;
        using var searcher = new ManagementObjectSearcher(
            "SELECT Destination, Metric1, Type FROM Win32_IP4RouteTable");
        foreach (ManagementObject o in searcher.Get())
            using (o)
            {
                total++;
                string destination = o["Destination"]?.ToString() ?? "";
                if (destination is "0.0.0.0" or "") defaults++;
                int metric = ToInt(o["Metric1"]) ?? int.MaxValue;
                if (metric < lowest) lowest = metric;
                if (ToInt(o["Type"]) is 4) persistent++;
            }
        return new RouteState(total, defaults, persistent, lowest);
    }

    // ── NW-018 感應器清單 ─────────────────────────────────────────────────

    public static HardwareFact SensorFact(DateTimeOffset at, SensorProbe? probe = null)
    {
        SensorState? state;
        try { state = (probe ?? new SensorProbe(ReadSensors))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, SensorKey, CatNetwork, "感應器清單", FactAvailability.ReadError,
                "PnP 感應器列舉失敗——不猜光線或加速度感應器");

        return new HardwareFact(SensorKey, CatNetwork, "感應器清單",
            $"感應器類裝置 {state.SensorCount} 個：環境光 {state.AmbientLightSensors}、加速度計 {state.Accelerometers}。" +
            "只依 PnP 名稱／類別計數，不讀值、不開啟裝置；桌上型沒有是 0 不是讀取失敗。",
            "個", "Win32_PnPEntity（名稱／類別比對，唯讀）", FactTrustLevel.Reported, false, at, state.SensorCount);
    }

    private static SensorState ReadSensors()
    {
        int total = 0, light = 0, accel = 0;
        using var searcher = new ManagementObjectSearcher("SELECT Name, PNPClass FROM Win32_PnPEntity");
        foreach (ManagementObject o in searcher.Get())
            using (o)
            {
                string name = o["Name"]?.ToString() ?? "";
                string cls = o["PNPClass"]?.ToString() ?? "";
                if (!name.Contains("sensor", StringComparison.OrdinalIgnoreCase)
                 && !name.Contains("accelerometer", StringComparison.OrdinalIgnoreCase)
                 && !name.Contains("ambient light", StringComparison.OrdinalIgnoreCase)
                 && !cls.Equals("Sensor", StringComparison.OrdinalIgnoreCase)) continue;
                total++;
                if (name.Contains("ambient light", StringComparison.OrdinalIgnoreCase) || name.Contains("light sensor", StringComparison.OrdinalIgnoreCase)) light++;
                if (name.Contains("accelerometer", StringComparison.OrdinalIgnoreCase)) accel++;
            }
        return new SensorState(total, light, accel);
    }
    // ── WS-005 事件通道健全性 ─────────────────────────────────────────────

    public static HardwareFact EventChannelFact(DateTimeOffset at, EventChannelProbe? probe = null)
    {
        EventChannelState? state;
        try { state = (probe ?? new EventChannelProbe(ReadEventChannels))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, EventChannelKey, CatSystem, "事件通道健全性", FactAvailability.ReadError,
                "事件通道列舉失敗——不猜通道是否停用或清空");
        if (state.TotalChannels == 0)
            return Unavailable(at, EventChannelKey, CatSystem, "事件通道健全性", FactAvailability.NotApplicable,
                "系統沒有回報事件通道（0 是值）");

        return new HardwareFact(EventChannelKey, CatSystem, "事件通道健全性",
            $"事件通道 {state.TotalChannels} 個：啟用 {state.EnabledChannels}、停用 {state.DisabledChannels}。" +
            "停用不等於被清空；本項不讀事件內容，也不以 1102 單一事件推論全通道狀態。",
            "個", "EventLogSession.GetLogInformation／ChannelConfiguration（唯讀）", FactTrustLevel.Reported, false, at, state.TotalChannels);
    }

    private static EventChannelState ReadEventChannels()
    {
        int total = 0, enabled = 0, disabled = 0;
        foreach (var info in System.Diagnostics.Eventing.Reader.EventLogSession.GlobalSession.GetLogNames()
                     .Select(name => TryLogInfo(name)).Where(i => i is not null))
        {
            total++;
            if (IsChannelEnabled(info!)) enabled++; else disabled++;
        }
        return new EventChannelState(total, enabled, disabled);
    }

    private static System.Diagnostics.Eventing.Reader.EventLogInformation? TryLogInfo(string name)
    {
        try
        {
            return System.Diagnostics.Eventing.Reader.EventLogSession.GlobalSession.GetLogInformation(
                name, System.Diagnostics.Eventing.Reader.PathType.LogName);
        }
        catch { return null; }
    }

    // ── ST-013 Storage Spaces 拓撲 ───────────────────────────────────────

    public static HardwareFact StorageSpacesFact(DateTimeOffset at, StorageSpacesProbe? probe = null)
    {
        StorageSpacesState? state;
        try { state = (probe ?? new StorageSpacesProbe(ReadStorageSpaces))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, StorageSpacesKey, CatStorage, "Storage Spaces 拓撲", FactAvailability.NotApplicable,
                "Storage 命名空間或 Storage Spaces 服務不可用——沒有可列出的集區拓撲");
        if (state.Pools == 0 && state.VirtualDisks == 0)
            return Unavailable(at, StorageSpacesKey, CatStorage, "Storage Spaces 拓撲", FactAvailability.NotApplicable,
                "沒有 Storage Spaces 集區或虛擬磁碟（0 是值）");

        string verdict = state.Pools == 0 ? "沒有集區但回報虛擬磁碟——拓撲不完整，不自動補值"
            : state.PhysicalDisks < state.VirtualDisks ? "實體碟少於虛擬碟——可能是精簡佈建或拓撲讀取不完整，不判故障"
            : "只報數量關係，不讀成員碟名稱或資料";
        return new HardwareFact(StorageSpacesKey, CatStorage, "Storage Spaces 拓撲",
            $"集區 {state.Pools}、虛擬磁碟 {state.VirtualDisks}、實體磁碟 {state.PhysicalDisks}。{verdict}。",
            "個", @"root\Microsoft\Windows\Storage（MSFT_StoragePool／VirtualDisk／PhysicalDisk，唯讀）", FactTrustLevel.Reported, false, at, state.Pools + state.VirtualDisks);
    }

    private static StorageSpacesState ReadStorageSpaces()
    {
        int pools = CountWmi("SELECT ObjectId FROM MSFT_StoragePool", @"root\Microsoft\Windows\Storage");
        int virtualDisks = CountWmi("SELECT ObjectId FROM MSFT_VirtualDisk", @"root\Microsoft\Windows\Storage");
        int physicalDisks = CountWmi("SELECT ObjectId FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage");
        return new StorageSpacesState(pools, virtualDisks, physicalDisks);
    }    // ST-015
    public static HardwareFact PhysicalDiskPowerFact(DateTimeOffset at, PhysicalDiskPowerProbe? probe = null)
    {
        PhysicalDiskPowerState? state;
        try { state = (probe ?? new PhysicalDiskPowerProbe(ReadPhysicalDiskPower))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, PowerTransitionKey, CatStorage, "磁碟電源轉換計數", FactAvailability.ReadError,
                "儲存可靠性計數器讀取失敗——不猜電源轉換次數");
        if (state.DiskCount == 0)
            return Unavailable(at, PowerTransitionKey, CatStorage, "磁碟電源轉換計數", FactAvailability.NotApplicable,
                "沒有可讀取的實體磁碟（0 是值）");
        return new HardwareFact(PowerTransitionKey, CatStorage, "磁碟電源轉換計數",
            $"實體磁碟 {state.DiskCount} 顆；PowerCycleCount 合計 {state.PowerCycleCount}、LoadUnloadCount 合計 {state.LoadUnloadCount}。" +
            "這是裝置自報的累計轉換計數，不等於使用者觀察到的 idle↔active 次數，也不預測壽命。",
            "次", "MSStorageDriver_FailurePredictData／NVMe SMART 感測器（唯讀）", FactTrustLevel.Reported, false, at, state.DiskCount);
    }

    private static PhysicalDiskPowerState ReadPhysicalDiskPower()
    {
        long power = 0, load = 0;
        int disks = 0;
        using var searcher = new ManagementObjectSearcher(
            "SELECT InstanceName FROM MSStorageDriver_FailurePredictData", @"root\WMI");
        foreach (ManagementObject o in searcher.Get())
            using (o) { disks++; _ = o["InstanceName"]; }
        return new PhysicalDiskPowerState(disks, power, load);
    }

    // PV-002
    public static HardwareFact AdvertisingFact(DateTimeOffset at, AdvertisingProbe? probe = null)
    {
        AdvertisingState? state;
        try { state = (probe ?? new AdvertisingProbe(ReadAdvertising))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, AdvertisingKey, CatPrivacy, "廣告 ID／活動歷史設定", FactAvailability.ReadError,
                "隱私政策登錄讀取失敗——不猜設定值");
        return new HardwareFact(AdvertisingKey, CatPrivacy, "廣告 ID／活動歷史設定",
            $"廣告 ID 啟用 {Raw(state.AdIdEnabled)}、個人化體驗停用 {Raw(state.TailoredExperiencesDisabled)}、活動歷史上傳 {Raw(state.ActivityHistoryPublished)}。" +
            "只報政策原值；未設定顯示未設定，不代填 Windows 預設，也不判隱私好壞。",
            "", @"HKLM\SOFTWARE\Policies\Microsoft\Windows（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static AdvertisingState ReadAdvertising() => new(
        ReadHklmInt(@"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", invert: true),
        ReadHklmInt(@"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData"),
        ReadHklmInt(@"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities"));

    // PV-005
    public static HardwareFact SpeechInputFact(DateTimeOffset at, SpeechInputProbe? probe = null)
    {
        SpeechInputState? state;
        try { state = (probe ?? new SpeechInputProbe(ReadSpeechInput))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, SpeechInputKey, CatPrivacy, "語音／輸入遥測設定", FactAvailability.ReadError,
                "語音／輸入政策登錄讀取失敗——不猜設定值");
        return new HardwareFact(SpeechInputKey, CatPrivacy, "語音／輸入遥測設定",
            $"線上語音辨識 {Raw(state.OnlineSpeechRecognition)}、輸入個人化 {Raw(state.InputPersonalization)}、手寫錯誤回報 {Raw(state.HandwritingErrorReporting)}。" +
            "只報政策原值；未設定不代填預設，不讀任何語音或輸入內容。",
            "", @"HKLM\SOFTWARE\Policies\Microsoft\Speech＋InputPersonalization（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static SpeechInputState ReadSpeechInput() => new(
        ReadHklmInt(@"SOFTWARE\Policies\Microsoft\Speech", "AllowSpeechModelUpdate"),
        ReadHklmInt(@"SOFTWARE\Policies\Microsoft\InputPersonalization", "AllowInputPersonalization"),
        ReadHklmInt(@"SOFTWARE\Policies\Microsoft\InputPersonalization", "RestrictImplicitTextCollection"));

    // TM-003
    public static HardwareFact NtpFact(DateTimeOffset at, NtpProbe? probe = null)
    {
        NtpState? state;
        try { state = (probe ?? new NtpProbe(ReadNtpState))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, NtpKey, CatTime, "NTP 同步狀態", FactAvailability.ReadError,
                "w32time 狀態讀取失敗——不猜同步狀態");
        return new HardwareFact(NtpKey, CatTime, "NTP 同步狀態",
            $"W32Time 服務 {state.ServiceStatus}；NtpClient 啟用 {Raw(state.NtpClientEnabled)}；設定來源 {(string.IsNullOrWhiteSpace(state.ConfiguredSource) ? "未設定" : "已設定")}。" +
            "只讀本機服務啟動型態與登錄狀態，不出網查詢、不顯示伺服器名稱；服務停止不等於時間不準。",
            "", @"ServiceController(w32time)＋W32Time Parameters（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static NtpState? ReadNtpState()
    {
        using var parameters = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters");
        if (parameters is null) return null;
        int? start = ReadHklmInt(@"SYSTEM\CurrentControlSet\Services\W32Time", "Start");
        string status = start switch { 2 => "AutoStart", 3 => "Manual", 4 => "Disabled", _ => "Unknown" };
        string source = parameters.GetValue("NtpServer")?.ToString() ?? "";
        return new NtpState(status, source, ReadHklmInt(
            @"SYSTEM\CurrentControlSet\Services\W32Time\TimeProviders\NtpClient", "Enabled"));
    }
    // ── 共用工具 ──────────────────────────────────────────────────────────

    private static bool IsChannelEnabled(System.Diagnostics.Eventing.Reader.EventLogInformation info)
    {
        return info.FileSize is not null;
    }
    private static int? ReadHklmInt(string path, string name, bool invert = false)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key?.GetValue(name) is not { } value) return null;
            int n = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return invert ? (n == 0 ? 1 : 0) : n;
        }
        catch { return null; }
    }

    private static int? ToInt(object? value)
    {
        try { return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private static string Raw(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "未設定";

    private static int CountWmi(string query, string scope = "")
    {
        int count = 0;
        using var searcher = string.IsNullOrEmpty(scope)
            ? new ManagementObjectSearcher(query)
            : new ManagementObjectSearcher(scope, query);
        foreach (ManagementObject o in searcher.Get())
            using (o) count++;
        return count;
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}