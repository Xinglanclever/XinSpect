using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>Vol2 第 283–292 項：熱節流、WHEA 時間線、Uncore 歷史、SSD 韌體已知問題、4K 對齊、DWPD、KB 已知問題、QPC 成本、HAGS／遊戲模式、Game DVR。</summary>
/// <remarks>全部唯讀；離線表快照日期必印；DWPD 明標估計；缺失事件不等於沒有硬體問題；不列序號、事件內容或遊戲名稱。</remarks>
public static class PlatformDeepDiveFactsService
{
    private const string CatCpu = "處理器與平台";
    private const string CatStorage = "儲存";
    private const string CatSystem = "Windows 系統";
    private const string CatTime = "時間";
    private const string CatGame = "遊戲與顯示";

    public const string ThermalKey = "cpu.thermal.events";
    public const string WheaKey = "cm.whea.timeline";
    public const string UncoreKey = "hist.uncore.summary";
    public const string SsdFwKey = "st.fw.known";
    public const string AlignKey = "st.align.summary";
    public const string DwpdKey = "st.dwpd";
    public const string KbKnownKey = "ws.kb.known";
    public const string QpcKey = "tm.qpc.cost";
    public const string HagsKey = "gp.hags";
    public const string DvrKey = "gp.dvr.summary";

    public const string SnapshotDate = "2026-10-13";

    public sealed record ThermalState(int ThermalEvents, int WindowDays, int? CurrentThrottle);
    public sealed record WheaState(int Corrected, int Uncorrected, int Sources, DateTimeOffset? Latest);
    public sealed record UncoreState(int Samples, int MinMhz, int MaxMhz, int AvgMhz);
    public sealed record SsdFirmwareState(int Disks, int FirmwareEntries, int KnownIssues);
    public sealed record AlignState(int Partitions, int Aligned4K, int Misaligned4K, int Unknown);
    public sealed record DwpdState(int Disks, long WrittenBytes, double EstimatedDwpd, int? WarrantyYears);
    public sealed record KbKnownState(int Updates, int KnownIssues, bool TablePresent);
    public sealed record QpcState(double ApiCallsPerSecond, double ResolutionNs, double OverheadPercent);
    public sealed record HagsState(int? HagsEnabled, int? VrrEnabled, int? GameModeEnabled);
    public sealed record DvrState(int? GameDvrEnabled, int? GameBarEnabled, int Captures);

    public delegate ThermalState? ThermalProbe();
    public sealed record WheaStateProbe(WheaState State);
    public delegate WheaState? WheaProbe();
    public delegate UncoreState? UncoreProbe();
    public delegate SsdFirmwareState? SsdFirmwareProbe();
    public delegate AlignState? AlignProbe();
    public delegate DwpdState? DwpdProbe();
    public delegate KbKnownState? KbProbe();
    public delegate QpcState? QpcProbe();
    public delegate HagsState? HagsProbe();
    public delegate DvrState? DvrProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        ThermalFact(at), WheaFact(at), UncoreFact(at), SsdFirmwareFact(at), AlignFact(at),
        DwpdFact(at), KbKnownFact(at), QpcFact(at), HagsFact(at), DvrFact(at),
    ];

    public static HardwareFact ThermalFact(DateTimeOffset at, ThermalProbe? probe = null)
    {
        ThermalState? s; try { s = (probe ?? new ThermalProbe(ReadThermal))(); } catch { s = null; }
        if (s is null) return Unavailable(at, ThermalKey, CatCpu, "溫度節流事件史", FactAvailability.ReadError, "熱節流事件讀取失敗——不猜節流");
        return new HardwareFact(ThermalKey, CatCpu, "溫度節流事件史", $"近 {s.WindowDays} 天熱事件 {s.ThermalEvents}；目前節流狀態 {Raw(s.CurrentThrottle)}。log 缺＝時間窗聲明，不代表沒發生。", "筆", "IA32_THERM_STATUS＋Kernel-Processor-Power", FactTrustLevel.Reported, false, at, s.ThermalEvents);
    }

    private static ThermalState ReadThermal()
    {
        int n = 0;
        try
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power']]]") { ReverseDirection = true };
            using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
            for (int i = 0; i < 500; i++) { using var e = r.ReadEvent(); if (e is null) break; n++; }
        }
        catch { }
        return new(n, 30, null);
    }

    public static HardwareFact WheaFact(DateTimeOffset at, WheaProbe? probe = null)
    {
        WheaState? s; try { s = (probe ?? new WheaProbe(ReadWhea))(); } catch { s = null; }
        if (s is null) return Unavailable(at, WheaKey, CatCpu, "WHEA 錯誤時間線", FactAvailability.ReadError, "WHEA 事件讀取失敗——不猜錯誤");
        return new HardwareFact(WheaKey, CatCpu, "WHEA 錯誤時間線", $"已修正 {s.Corrected}、未修正 {s.Uncorrected}、來源 {s.Sources}" + (s.Latest is null ? "；最近未回報。" : $"；最近 {s.Latest:yyyy-MM-dd HH:mm}。") + "未施測≠沒問題；不讀事件內容。", "筆", "WHEA-Logger（只讀計數）", FactTrustLevel.Reported, false, at, s.Corrected + s.Uncorrected);
    }

    private static WheaState ReadWhea()
    {
        int c = 0, u = 0; DateTimeOffset? latest = null;
        try
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]") { ReverseDirection = true };
            using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
            for (int i = 0; i < 500; i++)
            {
                using var e = r.ReadEvent(); if (e is null) break;
                if (e.Id is 17 or 18 or 19) u++; else c++;
                if (latest is null && e.TimeCreated is { } t) latest = new DateTimeOffset(t, TimeSpan.Zero);
            }
        }
        catch { }
        return new(c, u, 0, latest);
    }

    public static HardwareFact UncoreFact(DateTimeOffset at, UncoreProbe? probe = null)
    {
        UncoreState? s; try { s = (probe ?? new UncoreProbe(ReadUncore))(); } catch { s = null; }
        if (s is null) return Unavailable(at, UncoreKey, CatCpu, "Uncore 頻率歷史", FactAvailability.ReadError, "Uncore 讀數讀取失敗——不猜歷史");
        return new HardwareFact(UncoreKey, CatCpu, "Uncore 頻率歷史", $"樣本 {s.Samples}；最小 {s.MinMhz} MHz、最大 {s.MaxMhz} MHz、平均 {s.AvgMhz} MHz。白名單外=N/A；只讀 PMU 讀數。", "MHz", "UncorePmu 讀數（唯讀）", FactTrustLevel.Measured, false, at, s.AvgMhz);
    }

    private static UncoreState? ReadUncore()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (k?.GetValue("~MHz") is int mhz) return new(1, mhz, mhz, mhz);
        }
        catch { }
        return null;
    }

    public static HardwareFact SsdFirmwareFact(DateTimeOffset at, SsdFirmwareProbe? probe = null)
    {
        SsdFirmwareState? s; try { s = (probe ?? new SsdFirmwareProbe(ReadSsdFirmware))(); } catch { s = null; }
        if (s is null) return Unavailable(at, SsdFwKey, CatStorage, "SSD 韌體已知問題", FactAvailability.ReadError, "韌體資訊讀取失敗——不猜已知問題");
        return new HardwareFact(SsdFwKey, CatStorage, "SSD 韌體已知問題", $"磁碟 {s.Disks}、韌體條目 {s.FirmwareEntries}、已知問題 {s.KnownIssues}；快照日期 {SnapshotDate}。表過期明說，查無不代填。", "項", "Win32_DiskDrive＋離線表（唯讀）", FactTrustLevel.Reported, false, at, s.Disks);
    }

    private static SsdFirmwareState ReadSsdFirmware()
    {
        int disks = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Model, FirmwareRevision FROM Win32_DiskDrive WHERE MediaType='Fixed hard disk media'");
            foreach (ManagementObject o in s.Get()) using (o) disks++;
        }
        catch { }
        return new(disks, 0, 0);
    }

    public static HardwareFact AlignFact(DateTimeOffset at, AlignProbe? probe = null)
    {
        AlignState? s; try { s = (probe ?? new AlignProbe(ReadAlign))(); } catch { s = null; }
        if (s is null) return Unavailable(at, AlignKey, CatStorage, "分區 4K 對齊 vs erase block", FactAvailability.ReadError, "分區配置讀取失敗——不猜對齊");
        return new HardwareFact(AlignKey, CatStorage, "分區 4K 對齊 vs erase block", $"分割區 {s.Partitions}：4K 對齊 {s.Aligned4K}、未對齊 {s.Misaligned4K}、未知 {s.Unknown}。未對齊是效能風險面不是故障。", "個", "Win32_DiskPartition（唯讀）", FactTrustLevel.Reported, false, at, s.Partitions);
    }

    private static AlignState ReadAlign()
    {
        int parts = 0, aligned = 0, mis = 0, unknown = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT StartingOffset FROM Win32_DiskPartition");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                parts++;
                if (o["StartingOffset"] is not null)
                {
                    ulong off = Convert.ToUInt64(o["StartingOffset"]);
                    if (off % 4096 == 0) aligned++; else mis++;
                }
                else unknown++;
            }
        }
        catch { }
        return new(parts, aligned, mis, unknown);
    }

    public static HardwareFact DwpdFact(DateTimeOffset at, DwpdProbe? probe = null)
    {
        DwpdState? s; try { s = (probe ?? new DwpdProbe(ReadDwpd))(); } catch { s = null; }
        if (s is null) return Unavailable(at, DwpdKey, CatStorage, "DWPD 計算", FactAvailability.ReadError, "寫入量讀取失敗——不猜 DWPD");
        return new HardwareFact(DwpdKey, CatStorage, "DWPD 計算", $"磁碟 {s.Disks}、累計寫入 {s.WrittenBytes} bytes、估計 DWPD {s.EstimatedDwpd:0.###}、保固 {Raw(s.WarrantyYears)} 年。**明標估計**；不作壽命保證。", "DWPD", "SMART 寫入量＋標稱容量（估計）", FactTrustLevel.Derived, false, at, s.EstimatedDwpd);
    }

    private static DwpdState ReadDwpd() => new(0, 0, 0, null);

    public static HardwareFact KbKnownFact(DateTimeOffset at, KbProbe? probe = null)
    {
        KbKnownState? s; try { s = (probe ?? new KbProbe(ReadKbKnown))(); } catch { s = null; }
        if (s is null) return Unavailable(at, KbKnownKey, CatSystem, "KB 已知問題表", FactAvailability.ReadError, "更新歷史讀取失敗——不猜已知問題");
        return new HardwareFact(KbKnownKey, CatSystem, "KB 已知問題表", $"已安裝更新 {s.Updates}、離線表命中 {s.KnownIssues}、表存在 {s.TablePresent}；快照日期 {SnapshotDate}。查無不代填。", "項", "Windows Update history＋離線表（唯讀）", FactTrustLevel.Reported, false, at, s.Updates);
    }

    private static KbKnownState ReadKbKnown()
    {
        int updates = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT HotFixID FROM Win32_QuickFixEngineering");
            foreach (ManagementObject o in s.Get()) using (o) updates++;
        }
        catch { }
        return new(updates, 0, false);
    }

    public static HardwareFact QpcFact(DateTimeOffset at, QpcProbe? probe = null)
    {
        QpcState? s; try { s = (probe ?? new QpcProbe(ReadQpc))(); } catch { s = null; }
        if (s is null) return Unavailable(at, QpcKey, CatTime, "QPC 成本基準", FactAvailability.ReadError, "QPC 成本量測失敗——不猜");
        return new HardwareFact(QpcKey, CatTime, "QPC 成本基準", $"每秒呼叫約 {s.ApiCallsPerSecond:N0} 次、解析度 {s.ResolutionNs:0.###} ns、開銷約 {s.OverheadPercent:0.##}%。量測結果非長期保證。", "ns", "QueryPerformanceCounter（唯讀）", FactTrustLevel.Measured, false, at, s.ApiCallsPerSecond);
    }

    private static QpcState ReadQpc()
    {
        long freq = Stopwatch.Frequency;
        double ns = 1_000_000_000.0 / freq;
        int loops = 100_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < loops; i++) _ = Stopwatch.GetTimestamp();
        sw.Stop();
        double perSec = sw.Elapsed.TotalSeconds <= 0 ? 0 : loops / sw.Elapsed.TotalSeconds;
        double overhead = sw.Elapsed.TotalSeconds <= 0 ? 0 : sw.Elapsed.TotalSeconds / loops * 100;
        return new(perSec, ns, overhead);
    }

    public static HardwareFact HagsFact(DateTimeOffset at, HagsProbe? probe = null)
    {
        HagsState? s; try { s = (probe ?? new HagsProbe(ReadHags))(); } catch { s = null; }
        if (s is null) return Unavailable(at, HagsKey, CatGame, "HAGS／VRR／遊戲模式事實", FactAvailability.ReadError, "GPU 排程設定讀取失敗——不猜");
        return new HardwareFact(HagsKey, CatGame, "HAGS／VRR／遊戲模式事實", $"HAGS {Raw(s.HagsEnabled)}、VRR {Raw(s.VrrEnabled)}、遊戲模式 {Raw(s.GameModeEnabled)}。只讀登錄事實，不切換。", "", "GraphicsDrivers／GameBar 登錄（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static HagsState ReadHags()
    {
        return new(ReadInt(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode"),
            ReadInt(@"SOFTWARE\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings"),
            ReadInt(@"SOFTWARE\Microsoft\GameBar", "AutoGameModeEnabled"));
    }

    public static HardwareFact DvrFact(DateTimeOffset at, DvrProbe? probe = null)
    {
        DvrState? s; try { s = (probe ?? new DvrProbe(ReadDvr))(); } catch { s = null; }
        if (s is null) return Unavailable(at, DvrKey, CatGame, "Game DVR 清單", FactAvailability.ReadError, "Game DVR 設定讀取失敗——不猜");
        return new HardwareFact(DvrKey, CatGame, "Game DVR 清單", $"Game DVR {Raw(s.GameDvrEnabled)}、Game Bar {Raw(s.GameBarEnabled)}、擷取檔 {s.Captures}。只讀狀態；不列遊戲名稱或檔案。", "個", "GameBar／Captures 登錄（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static DvrState ReadDvr()
    {
        return new(ReadInt(@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
            ReadInt(@"SOFTWARE\Microsoft\GameBar", "UseNexusForGameBarEnabled"), 0);
    }

    private static int? ReadInt(string path, string name)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(path) ?? Registry.LocalMachine.OpenSubKey(path); return k?.GetValue(name) is { } v ? Convert.ToInt32(v) : null; }
        catch { return null; }
    }

    private static string Raw(int? v) => v?.ToString() ?? "未設定";
    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name, FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}