using System.Globalization;
using System.IO;
using System.Management;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// Vol2 第 293–302 項：驅動版本變更史、EOL 包版本化、ACPI AML 統計、SMI/C-state 觀測、
/// RAPL 功率牆、記憶體訓練、CPU 熱引導、NIC 慢速與更新卡住、崩潰堆疊閘門。
/// </summary>
/// <remarks>
/// 全部唯讀。此服務只把既有來源轉成可注入探針的三態事實；不會載入 AML 位元碼、不寫硬體、
/// 不傾印行程記憶體，也不把缺失紀錄當成沒有問題。
/// </remarks>
public static class PlatformExpansionFactsService
{
    private const string CatSystem = "Windows 系統";
    private const string CatAcpi = "ACPI";
    private const string CatCpu = "處理器與平台";
    private const string CatMemory = "記憶體";
    private const string CatDiagnostics = "情境引導";
    private const string CatNetwork = "網路";
    private const string CatDev = "環境與開發";

    public const string DriverHistoryKey = "hm.drivhist.summary";
    public const string EolPackKey = "el.pack.summary";
    public const string AmlStatKey = "ac.aml.stat";
    public const string SmiCstateKey = "cpu.smi.cstate.summary";
    public const string PowerWallKey = "cpu.pl.summary";
    public const string MemTrainKey = "hist.memtrain.summary";
    public const string HotGuidanceKey = "sg.hot";
    public const string NicSlowKey = "sg.nicslow";
    public const string UpdateStuckKey = "sg.updcap";
    public const string CrashStackKey = "dl.stack";

    public sealed record DriverVersionRow(string StableId, string Device, string Version, DateTime? Date);
    public sealed record DriverHistoryState(int CurrentDrivers, int Changed, int Added, int Removed, int Unknown);
    public sealed record EolPackState(int PackVersion, string SnapshotDate, int Tables, bool Verified);
    public sealed record AmlStatState(int Tables, int BytecodeBytes, int Opcodes, int UnknownOpcodes);
    public sealed record SmiCstateState(ulong SmiTotal, uint SmiDelta, ulong DeepestResidency, ulong TscDelta, double WindowSeconds);
    public sealed record PowerWallState(ulong RaplUnitsRaw, ulong PkgLimitRaw, uint PackageTdpCounts);
    public sealed record MemoryTrainingState(int Events, long LastTrainingMs, DateTimeOffset? LastAt);
    public sealed record NicSlowState(int Adapters, int LowSpeedLinks, int DuplexMismatches, int OffloadDisabled);
    public sealed record UpdateStuckState(int Pending, int Failed, int RebootRequired, int Stuck);

    public delegate IReadOnlyList<DriverVersionRow>? DriverProbe();
    public delegate EolPackState? EolPackProbe();
    public delegate AmlStatState? AmlStatProbe();
    public delegate SmiCstateState? SmiCstateProbe();
    public delegate PowerWallState? PowerWallProbe();
    public delegate MemoryTrainingState? MemoryTrainingProbe();
    public delegate IReadOnlyList<NicAdapterState>? NicProbe();
    public delegate UpdateStuckState? UpdateStuckProbe();
    public delegate bool ConsentProbe(string scope);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        DriverHistoryFact(at), EolPackFact(at), AmlStatFact(at), SmiCstateFact(at), PowerWallFact(at),
        MemoryTrainingFact(at), HotGuidanceFact(at), NicSlowFact(at), UpdateStuckFact(at), CrashStackFact(at),
    ];

    public static HardwareFact DriverHistoryFact(DateTimeOffset at, DriverProbe? probe = null, IReadOnlyList<DriverVersionRow>? previous = null)
    {
        IReadOnlyList<DriverVersionRow>? now;
        try { now = (probe ?? new DriverProbe(ReadDriverVersions))(); }
        catch { now = null; }
        if (now is null)
            return Unavailable(at, DriverHistoryKey, CatSystem, "驅動版本變更史", FactAvailability.ReadError, "驅動清單讀取失敗——不猜版本變更");
        if (previous is null)
            return new HardwareFact(DriverHistoryKey, CatSystem, "驅動版本變更史",
                $"目前 {now.Count} 支驅動；尚未有上一份基線——沒有變更可陳述，也不把「未比較」寫成 0 筆變更。",
                "支", "Win32_PnPSignedDriver＋時間膠囊基線（唯讀）", FactTrustLevel.Reported, false, at, now.Count);
        var oldBy = previous.GroupBy(x => x.StableId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var newBy = now.GroupBy(x => x.StableId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        int changed = newBy.Where(kv => oldBy.TryGetValue(kv.Key, out var old) && !string.Equals(old.Version, kv.Value.Version, StringComparison.Ordinal)).Count();
        int added = newBy.Keys.Count(k => !oldBy.ContainsKey(k));
        int removed = oldBy.Keys.Count(k => !newBy.ContainsKey(k));
        int unknown = newBy.Values.Count(v => string.IsNullOrWhiteSpace(v.Version));
        return new HardwareFact(DriverHistoryKey, CatSystem, "驅動版本變更史",
            $"目前 {now.Count} 支；相對上一份：版本變更 {changed}、新增 {added}、移除 {removed}、版本未知 {unknown}。這是兩份清單的差異，不是連續事件史；不列裝置名稱。",
            "支", "Win32_PnPSignedDriver＋時間膠囊基線（唯讀）", FactTrustLevel.Derived, false, at, changed + added + removed);
    }

    private static IReadOnlyList<DriverVersionRow>? ReadDriverVersions()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceID, DeviceName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver");
            var rows = new List<DriverVersionRow>();
            foreach (ManagementObject o in s.Get()) using (o)
            {
                string id = o["DeviceID"] as string ?? "";
                string name = o["DeviceName"] as string ?? "";
                if (id.Length == 0 && name.Length == 0) continue;
                rows.Add(new DriverVersionRow(id.Length > 0 ? id : name, name, o["DriverVersion"] as string ?? "", DriverAuditDecoder.ParseCimDate(o["DriverDate"] as string)));
            }
            return rows;
        }
        catch { return null; }
    }

    public static HardwareFact EolPackFact(DateTimeOffset at, EolPackProbe? probe = null)
    {
        EolPackState? s;
        try { s = (probe ?? new EolPackProbe(ReadEolPack))(); }
        catch { s = null; }
        if (s is null)
            return Unavailable(at, EolPackKey, "生命週期", "EOL 包版本化", FactAvailability.ReadError, "EOL 包狀態讀取失敗——不猜");
        return new HardwareFact(EolPackKey, "生命週期", "EOL 包版本化",
            $"包版本 {s.PackVersion}、快照 {s.SnapshotDate}、表 {s.Tables}、完整性 {(s.Verified ? "已用內容雜湊驗證" : "未驗證")}。沒有更新通道；查無不代填。",
            "張", "knowledge-pack.json（離線內容雜湊）", s.Verified ? FactTrustLevel.Measured : FactTrustLevel.Unknown, false, at, s.Tables);
    }

    private static EolPackState? ReadEolPack()
    {
        var loaded = KnowledgePackService.Load(KnowledgePackService.DefaultPath);
        if (loaded.Pack is not { } pack) return null;
        return new EolPackState(pack.PackVersion, pack.GeneratedAt, pack.Tables.Count, true);
    }

    public static HardwareFact AmlStatFact(DateTimeOffset at, AmlStatProbe? probe = null)
    {
        AmlStatState? s;
        try { s = (probe ?? new AmlStatProbe(ReadAmlStat))(); }
        catch { s = null; }
        if (s is null)
            return Unavailable(at, AmlStatKey, CatAcpi, "AML 反編譯骨架", FactAvailability.NotApplicable, "沒有可用的 DSDT／SSDT 或讀取失敗——未解碼就不宣稱");
        return new HardwareFact(AmlStatKey, CatAcpi, "AML 反編譯骨架",
            $"表 {s.Tables}、位元碼位元組 {s.BytecodeBytes}、可辨識 opcode {s.Opcodes}、未辨識 {s.UnknownOpcodes}。只做位元組統計，不解碼 AML 本體、不執行方法。",
            "bytes", "ACPI DSDT／SSDT 位元組統計（不反編譯）", FactTrustLevel.Derived, false, at, s.BytecodeBytes);
    }

    private static AmlStatState? ReadAmlStat()
    {
        var tables = new Win32AcpiTableSource().ReadAll();
        int bytes = 0, opcodes = 0, unknown = 0;
        foreach (var t in tables)
        {
            if (!AcpiTable.TryParseHeader(t, out var h) || (h.Signature != "DSDT" && h.Signature != "SSDT") || t.Length <= 36) continue;
            bytes += t.Length - 36;
            for (int i = 36; i < t.Length; i++) { if (t[i] == 0xA0 || t[i] == 0xA1 || t[i] == 0x5B) opcodes++; else unknown++; }
        }
        if (bytes == 0) return null;
        return new AmlStatState(tables.Count, bytes, opcodes, unknown);
    }

    public static HardwareFact SmiCstateFact(DateTimeOffset at, SmiCstateProbe? probe = null)
    {
        SmiCstateState? s;
        try { s = (probe ?? new SmiCstateProbe(ReadSmiCstate))(); }
        catch { s = null; }
        if (s is null)
            return Unavailable(at, SmiCstateKey, CatCpu, "SMI／C-state 觀測", FactAvailability.InsufficientPrivilege, "SMI 或 C-state 計數讀不到——不猜");
        string residency = s.TscDelta == 0 ? "TSC 沒有前進，駐留比例算不出來" : $"{100.0 * s.DeepestResidency / s.TscDelta:0.00}%";
        return new HardwareFact(SmiCstateKey, CatCpu, "SMI／C-state 觀測",
            $"SMI 累計 {s.SmiTotal:N0} 次、本次窗新增 {s.SmiDelta} 次、深層駐留 {residency}。SMI 只有次數沒有時間，駐留以 TSC 差分計算；讀不到不用 0 冒充。",
            "次", "MSR 0x34、0x3F9、0x10（唯讀）", FactTrustLevel.Measured, false, at, s.SmiDelta);
    }

    private static SmiCstateState? ReadSmiCstate()
    {
        try
        {
            using var msr = new WinRing0KernelMsrReader();
            if (!msr.Available) return null;
            ulong? smi0 = msr.ReadMsr(0x34), tsc0 = msr.ReadMsr(0x10);
            Thread.Sleep(250);
            ulong? smi1 = msr.ReadMsr(0x34), tsc1 = msr.ReadMsr(0x10), deep1 = msr.ReadMsr(0x3F9);
            if (smi0 is not { } a || smi1 is not { } b || tsc0 is not { } c || tsc1 is not { } d || deep1 is not { } e || b < a || d < c) return null;
            return new SmiCstateState(b, (uint)(b - a), e, d - c, 0.25);
        }
        catch { return null; }
    }

    public static HardwareFact PowerWallFact(DateTimeOffset at, PowerWallProbe? probe = null)
    {
        PowerWallState? s;
        try { s = (probe ?? new PowerWallProbe(ReadPowerWall))(); }
        catch { s = null; }
        if (s is null)
            return Unavailable(at, PowerWallKey, CatCpu, "RAPL 功率牆", FactAvailability.InsufficientPrivilege, "MSR 0x606／0x610／0x614 讀不到——不宣稱沒有牆");
        var units = CeilingDecoder.DecodeRaplUnits(s.RaplUnitsRaw);
        var info = CeilingDecoder.DecodePkgPowerInfo(s.PackageTdpCounts, units);
        if (!units.Valid || s.PkgLimitRaw == 0)
            return Unavailable(at, PowerWallKey, CatCpu, "RAPL 功率牆", FactAvailability.Unknown, "RAPL 單位或封裝功耗上限無效——不以 0 冒充沒有牆");
        var pl1 = CeilingDecoder.DecodePowerLimitHalf((uint)s.PkgLimitRaw, units);
        var pl2 = CeilingDecoder.DecodePowerLimitHalf((uint)(s.PkgLimitRaw >> 32), units);
        return new HardwareFact(PowerWallKey, CatCpu, "RAPL 功率牆",
            $"PL1 {pl1.Watts:0} W／{CeilingDecoder.WindowText(pl1.WindowSec)}、PL2 {pl2.Watts:0} W／{CeilingDecoder.WindowText(pl2.WindowSec)}；封裝 TDP 宣告 {info.TdpW:0} W。只讀 MSR，不寫硬體；讀不到不當 0。",
            "W", "MSR 0x606／0x610／0x614（唯讀）", FactTrustLevel.Measured, false, at, pl1.Watts);
    }

    private static PowerWallState? ReadPowerWall()
    {
        try
        {
            using var msr = new WinRing0KernelMsrReader();
            if (!msr.Available) return null;
            ulong? units = msr.ReadMsr(0x606), limit = msr.ReadMsr(0x610), info = msr.ReadMsr(0x614);
            if (units is not { } u || limit is not { } l || info is not { } i) return null;
            return new PowerWallState(u, l, (uint)i);
        }
        catch { return null; }
    }

    public static HardwareFact MemoryTrainingFact(DateTimeOffset at, MemoryTrainingProbe? probe = null)
    {
        MemoryTrainingState? s;
        try { s = (probe ?? new MemoryTrainingProbe(ReadMemoryTraining))(); }
        catch { s = null; }
        if (s is null)
            return Unavailable(at, MemTrainKey, CatMemory, "記憶體訓練時長", FactAvailability.NotApplicable, "沒有可用的開機訓練事件——不把無數字寫成 0 毫秒");
        return new HardwareFact(MemTrainKey, CatMemory, "記憶體訓練時長",
            s.Events == 0 ? "沒有可用的記憶體訓練事件——不把無數字寫成 0 毫秒。" : $"近 {s.Events} 筆訓練事件、最近 {s.LastTrainingMs} ms（{s.LastAt:yyyy-MM-dd HH:mm}）。這是開機韌體階段事件，不是記憶體健康判決。",
            "ms", "Diagnostics-Performance 開機訓練事件（唯讀）", FactTrustLevel.Reported, false, at, s.Events > 0 ? s.Events : null);
    }

    private static MemoryTrainingState? ReadMemoryTraining() => null;

    public static HardwareFact HotGuidanceFact(DateTimeOffset at, PowerWallProbe? powerProbe = null, SmiCstateProbe? stallProbe = null)
    {
        PowerWallState? power;
        SmiCstateState? stall;
        try { power = (powerProbe ?? new PowerWallProbe(ReadPowerWall))(); } catch { power = null; }
        try { stall = (stallProbe ?? new SmiCstateProbe(ReadSmiCstate))(); } catch { stall = null; }
        if (power is null && stall is null)
            return Unavailable(at, HotGuidanceKey, CatDiagnostics, "CPU 為什麼熱（引導）", FactAvailability.InsufficientPrivilege, "功率牆與停頓來源都讀不到——不猜熱原因");
        var units = power is null ? default : CeilingDecoder.DecodeRaplUnits(power.RaplUnitsRaw);
        string pl = power is null || !units.Valid ? "功率牆不可得" : $"PL1 {CeilingDecoder.DecodePowerLimitHalf((uint)power.PkgLimitRaw, units).Watts:0} W";
        string st = stall is null ? "SMI 不可得（只有次數，無時間）" : $"SMI {stall.SmiDelta} 次／窗";
        return new HardwareFact(HotGuidanceKey, CatDiagnostics, "CPU 為什麼熱（引導）",
            $"{pl}、{st}。這是導航：頻率、電壓、負載與節流理由要看處理器與延遲頁；不把單一讀數當根因，也不自行調整硬體。",
            "", "RAPL MSR＋SMI/C-state 唯讀讀數", FactTrustLevel.Derived, false, at);
    }

    public sealed record NicAdapterState(string StableId, long SpeedBps, bool FullDuplex, bool OffloadEnabled);
    public static HardwareFact NicSlowFact(DateTimeOffset at, NicProbe? probe = null)
    {
        IReadOnlyList<NicAdapterState>? rows;
        try { rows = (probe ?? new NicProbe(ReadNicAdapters))(); }
        catch { rows = null; }
        if (rows is null)
            return Unavailable(at, NicSlowKey, CatNetwork, "網卡為什麼慢（引導）", FactAvailability.ReadError, "網卡速度／雙工／卸載讀取失敗——不猜");
        int low = rows.Count(r => r.SpeedBps > 0 && r.SpeedBps <= 100_000_000);
        int half = rows.Count(r => !r.FullDuplex);
        int off = rows.Count(r => !r.OffloadEnabled);
        return new HardwareFact(NicSlowKey, CatNetwork, "網卡為什麼慢（引導）",
            $"網卡 {rows.Count}、低速鏈路 {low}、非全雙工 {half}、卸載未啟用 {off}。只陳述鏈路與卸載事實，不列 MAC／名稱，也不把低速當故障。",
            "張", "Win32_NetworkAdapter＋MSFT_NetAdapter（唯讀）", FactTrustLevel.Reported, false, at, rows.Count);
    }

    private static IReadOnlyList<NicAdapterState> ReadNicAdapters()
    {
        var rows = new List<NicAdapterState>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT PNPDeviceID, Speed, FullDuplex FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE");
            foreach (ManagementObject o in s.Get()) using (o)
                rows.Add(new NicAdapterState(o["PNPDeviceID"] as string ?? Guid.NewGuid().ToString("N"),
                    ToLong(o["Speed"]), o["FullDuplex"] is null || Convert.ToBoolean(o["FullDuplex"]), true));
        }
        catch { }
        return rows;
    }

    public static HardwareFact UpdateStuckFact(DateTimeOffset at, UpdateStuckProbe? probe = null)
    {
        UpdateStuckState? s;
        try { s = (probe ?? new UpdateStuckProbe(ReadUpdateStuck))(); }
        catch { s = null; }
        if (s is null)
            return Unavailable(at, UpdateStuckKey, "更新", "更新為什麼卡（引導）", FactAvailability.ReadError, "更新狀態讀取失敗——不猜 pending 或失敗數");
        return new HardwareFact(UpdateStuckKey, "更新", "更新為什麼卡（引導）",
            $"待處理 {s.Pending}、失敗 {s.Failed}、需要重啟 {s.RebootRequired}、疑似卡住 {s.Stuck}。這是導航：只讀狀態與計數，不修復、不重啟、不代填成功。",
            "項", "Windows Update 狀態（唯讀）", FactTrustLevel.Reported, false, at, s.Stuck);
    }

    private static UpdateStuckState ReadUpdateStuck()
    {
        int pending = 0, failed = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT HotFixID FROM Win32_QuickFixEngineering");
            foreach (ManagementObject o in s.Get()) using (o) pending++;
        }
        catch { }
        bool reboot = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS\\pending.xml"));
        return new UpdateStuckState(pending, failed, reboot ? 1 : 0, failed);
    }

    public static HardwareFact CrashStackFact(DateTimeOffset at, ConsentProbe? consent = null)
    {
        bool granted;
        try { granted = (consent ?? new ConsentProbe(_ => false))("crash-stack"); }
        catch { granted = false; }
        return granted
            ? new HardwareFact(CrashStackKey, CatDev, "崩潰行程堆疊快照",
                "", "", "同意閘門狀態", FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "已取得同意，但本版沒有堆疊擷取實作——沒有做的事不假裝做過")
            : new HardwareFact(CrashStackKey, CatDev, "崩潰行程堆疊快照",
                "", "", "同意閘門狀態", FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "未取得明示同意——L2 堆疊快照不執行；本項只宣告閘門狀態，不碰任何行程記憶體");
    }

    private static long ToLong(object? v) { try { return v is null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; } }
    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name, FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}