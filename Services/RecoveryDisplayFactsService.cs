using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.AccessControl;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>Vol2 第 233–242 項：系統 ACL 抽查、VSS 供應商、MSI 孤兒、喚醒來源、SleepStudy、電源計畫漂移、現代待機相容性、TDR、DirectX 矩陣、顯示器熱插拔。</summary>
public static class RecoveryDisplayFactsService
{
    private const string CatRecovery = "恢復";
    private const string CatPower = "電源與恢復";
    private const string CatDisplay = "顯示卡";

    public const string AclKey = "rc.acl.summary";
    public const string VssKey = "rc.vss.summary";
    public const string MsiOrphanKey = "rc.msi.orphan.summary";
    public const string WakeSourceKey = "pw.wake.summary";
    public const string SleepStudyKey = "pw.sleepstudy.summary";
    public const string PlanDriftKey = "pw.plan.drift";
    public const string ModernStandbyKey = "pw.ms.compat";
    public const string TdrKey = "gd.tdr.summary";
    public const string DirectXKey = "gd.dx.summary";
    public const string HotPlugKey = "gd.plug.summary";

    public sealed record AclState(int DirectoriesChecked, int WeakWriteDirectories, int WeakOwnerDirectories);
    public sealed record VssState(int Providers, int ShadowCopies, int ProviderFailures);
    public sealed record MsiOrphanState(int CachedPackages, int RegisteredProducts, int OrphanCount);
    public sealed record WakeSourceState(int WakeTimers, int WakeDevices, string? LastWakeSource);
    public sealed record SleepStudyState(int Sessions, int LowPowerSessions, int ExitReasonKinds);
    public sealed record PlanDriftState(string? ActivePlanGuid, int OverrideValues, int ActivePlanChanges);
    public sealed record ModernStandbyState(int ChecklistItems, int PassedItems, int UnknownItems);
    public sealed record TdrState(int TdrEvents, DateTimeOffset? LastEvent);
    public sealed record DirectXState(int Adapters, int HardwareFeatureLevel, int Wddm30Adapters);
    public sealed record HotPlugState(int DisplayEvents, int DockEvents, int LastDays);

    public delegate AclState? AclProbe();
    public delegate VssState? VssProbe();
    public delegate MsiOrphanState? MsiOrphanProbe();
    public delegate WakeSourceState? WakeSourceProbe();
    public delegate SleepStudyState? SleepStudyProbe();
    public delegate PlanDriftState? PlanDriftProbe();
    public delegate ModernStandbyState? ModernStandbyProbe();
    public delegate TdrState? TdrProbe();
    public delegate DirectXState? DirectXProbe();
    public delegate HotPlugState? HotPlugProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        AclFact(at), VssFact(at), MsiOrphanFact(at), WakeSourceFact(at), SleepStudyFact(at),
        PlanDriftFact(at), ModernStandbyFact(at), TdrFact(at), DirectXFact(at), HotPlugFact(at),
    ];

    public static HardwareFact AclFact(DateTimeOffset at, AclProbe? probe = null)
    {
        AclState? s; try { s = (probe ?? new AclProbe(ReadAcl))(); } catch { s = null; }
        if (s is null) return Unavailable(at, AclKey, CatRecovery, "系統目錄 ACL 抽查", FactAvailability.ReadError, "ACL 讀取失敗——不猜權限");
        return new HardwareFact(AclKey, CatRecovery, "系統目錄 ACL 抽查", $"抽查目錄 {s.DirectoriesChecked}、可寫弱權限 {s.WeakWriteDirectories}、擁有者弱 {s.WeakOwnerDirectories}。只報計數，不列路徑；弱權限是風險面不是判決。", "個", "Program Files／ProgramData ACL（只讀）", FactTrustLevel.Reported, false, at, s.DirectoriesChecked);
    }

    private static AclState ReadAcl()
    {
        int total = 0, weakWrite = 0, weakOwner = 0;
        foreach (string root in new[] { @"C:\Program Files", @"C:\ProgramData" })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var di = new DirectoryInfo(root);
                var sec = di.GetAccessControl();
                total++;
                foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                {
                    if (r.AccessControlType != AccessControlType.Allow) continue;
                    if ((r.FileSystemRights & FileSystemRights.WriteData) != 0
                     && r.IdentityReference is System.Security.Principal.SecurityIdentifier sid
                     && sid.IsWellKnown(System.Security.Principal.WellKnownSidType.WorldSid)) weakWrite++;
                    if (r.FileSystemRights == FileSystemRights.FullControl && r.IdentityReference.Value.EndsWith("-1000")) weakOwner++;
                }
            }
            catch { }
        }
        return new(total, weakWrite, weakOwner);
    }

    public static HardwareFact VssFact(DateTimeOffset at, VssProbe? probe = null)
    {
        VssState? s; try { s = (probe ?? new VssProbe(ReadVss))(); } catch { s = null; }
        if (s is null) return Unavailable(at, VssKey, CatRecovery, "VSS 供應商審計", FactAvailability.ReadError, "VSS 列舉失敗——不猜供應商");
        return new HardwareFact(VssKey, CatRecovery, "VSS 供應商審計", $"供應商 {s.Providers}、影子副本 {s.ShadowCopies}、供應商失敗 {s.ProviderFailures}。只讀清單，不建立、刪除或掛載。", "個", "Win32_ShadowProvider／ShadowCopy（唯讀）", FactTrustLevel.Reported, false, at, s.Providers);
    }

    private static VssState ReadVss()
    {
        int providers = 0, shadows = 0, failures = 0;
        try
        {
            using var p = new ManagementObjectSearcher("SELECT ID FROM Win32_ShadowProvider");
            foreach (ManagementObject o in p.Get()) using (o) providers++;
            using var s = new ManagementObjectSearcher("SELECT ID FROM Win32_ShadowCopy");
            foreach (ManagementObject o in s.Get()) using (o) shadows++;
        }
        catch { failures++; }
        return new(providers, shadows, failures);
    }


    public static HardwareFact MsiOrphanFact(DateTimeOffset at, MsiOrphanProbe? probe = null)
    {
        MsiOrphanState? s; try { s = (probe ?? new MsiOrphanProbe(ReadMsiOrphan))(); } catch { s = null; }
        if (s is null) return Unavailable(at, MsiOrphanKey, CatRecovery, "Installer 快取孤兒", FactAvailability.ReadError, "MSI 快取讀取失敗——不猜孤兒");
        return new HardwareFact(MsiOrphanKey, CatRecovery, "Installer 快取孤兒", $"快取套件 {s.CachedPackages}、已註冊產品 {s.RegisteredProducts}、可能孤兒 {s.OrphanCount}。刪除判斷留給人；只列計數。", "個", "Installer\\Products 與快取 metadata（唯讀）", FactTrustLevel.Reported, false, at, s.CachedPackages);
    }

    private static MsiOrphanState ReadMsiOrphan()
    {
        int cached = 0, registered = 0, orphan = 0;
        try
        {
            using var cache = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\Installer\Products");
            cached = cache?.SubKeyCount ?? 0;
            using var products = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            registered = products?.SubKeyCount ?? 0;
            orphan = Math.Max(0, cached - registered);
        }
        catch { }
        return new MsiOrphanState(cached, registered, orphan);
    }
    public static HardwareFact WakeSourceFact(DateTimeOffset at, WakeSourceProbe? probe = null)
    {
        WakeSourceState? s; try { s = (probe ?? new WakeSourceProbe(ReadWakeSource))(); } catch { s = null; }
        if (s is null) return Unavailable(at, WakeSourceKey, CatPower, "喚醒來源審計", FactAvailability.ReadError, "喚醒來源讀取失敗——不猜喚醒者");
        return new HardwareFact(WakeSourceKey, CatPower, "喚醒來源審計", $"喚醒計時器 {s.WakeTimers}、喚醒裝置 {s.WakeDevices}；最近喚醒來源 {(string.IsNullOrWhiteSpace(s.LastWakeSource) ? "未回報" : "已回報")}。不列裝置名稱；事件是痕跡不是根因。", "個", "powercfg（唯讀）", FactTrustLevel.Reported, false, at, s.WakeTimers);
    }

    private static WakeSourceState ReadWakeSource() => new(0, 0, null);

    public static HardwareFact SleepStudyFact(DateTimeOffset at, SleepStudyProbe? probe = null)
    {
        SleepStudyState? s; try { s = (probe ?? new SleepStudyProbe(ReadSleepStudy))(); } catch { s = null; }
        if (s is null) return Unavailable(at, SleepStudyKey, CatPower, "睡眠研究解碼", FactAvailability.NotApplicable, "沒有 SleepStudy 報告——Modern Standby 未施測");
        return new HardwareFact(SleepStudyKey, CatPower, "睡眠研究解碼", $"睡眠窗 {s.Sessions}、低功耗 {s.LowPowerSessions}、退出原因種類 {s.ExitReasonKinds}。未施測不代填；只讀報告 metadata。", "個", @"SleepStudy HTML／ETL metadata（唯讀）", FactTrustLevel.Reported, false, at, s.Sessions);
    }

    private static SleepStudyState ReadSleepStudy() => new(0, 0, 0);

    public static HardwareFact PlanDriftFact(DateTimeOffset at, PlanDriftProbe? probe = null)
    {
        PlanDriftState? s; try { s = (probe ?? new PlanDriftProbe(ReadPlanDrift))(); } catch { s = null; }
        if (s is null) return Unavailable(at, PlanDriftKey, CatPower, "電源計畫漂移", FactAvailability.ReadError, "電源計畫讀取失敗——不猜漂移");
        return new HardwareFact(PlanDriftKey, CatPower, "電源計畫漂移", $"作用中計畫 {(string.IsNullOrWhiteSpace(s.ActivePlanGuid) ? "未回報" : "已回報")}、覆寫值 {s.OverrideValues}、變更識別 {s.ActivePlanChanges}。只讀不切換；不顯示 GUID。", "", "powercfg／電源計畫登錄（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static PlanDriftState ReadPlanDrift()
    {
        string? active = null;
        try
        {
            var p = Process.Start(new ProcessStartInfo("powercfg.exe", "/getactivescheme") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
            if (p is not null) { string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); active = o; }
        }
        catch { }
        return new(active, 0, 0);
    }

    public static HardwareFact ModernStandbyFact(DateTimeOffset at, ModernStandbyProbe? probe = null)
    {
        ModernStandbyState? s; try { s = (probe ?? new ModernStandbyProbe(ReadModernStandby))(); } catch { s = null; }
        if (s is null) return Unavailable(at, ModernStandbyKey, CatPower, "現代待機相容性", FactAvailability.ReadError, "Modern Standby 檢查失敗——不猜相容性");
        return new HardwareFact(ModernStandbyKey, CatPower, "現代待機相容性", $"檢查清單 {s.ChecklistItems}、通過 {s.PassedItems}、未知 {s.UnknownItems}。未施測明確聲明，不代填支援。", "項", "powercfg /a＋平台能力（唯讀）", FactTrustLevel.Reported, false, at, s.ChecklistItems);
    }

    private static ModernStandbyState ReadModernStandby() => new(0, 0, 0);

    public static HardwareFact TdrFact(DateTimeOffset at, TdrProbe? probe = null)
    {
        TdrState? s; try { s = (probe ?? new TdrProbe(ReadTdr))(); } catch { s = null; }
        if (s is null) return Unavailable(at, TdrKey, CatDisplay, "TDR 事件史", FactAvailability.ReadError, "TDR 事件讀取失敗——不猜重置史");
        return new HardwareFact(TdrKey, CatDisplay, "TDR 事件史", $"TDR 事件 {s.TdrEvents}" + (s.LastEvent is null ? "；最近未回報。" : $"；最近 {s.LastEvent:yyyy-MM-dd HH:mm}。") + "事件是痕跡不是根因。", "筆", "Display 4101／13（只讀計數）", FactTrustLevel.Reported, false, at, s.TdrEvents);
    }

    private static TdrState ReadTdr()
    {
        int n = 0; DateTimeOffset? last = null;
        try
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[(EventID=4101)]]") { ReverseDirection = true };
            using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
            for (int i = 0; i < 500; i++)
            {
                using var e = r.ReadEvent(); if (e is null) break;
                n++; if (last is null && e.TimeCreated is { } t) last = new DateTimeOffset(t, TimeSpan.Zero);
            }
        }
        catch { }
        return new(n, last);
    }

    public static HardwareFact DirectXFact(DateTimeOffset at, DirectXProbe? probe = null)
    {
        DirectXState? s; try { s = (probe ?? new DirectXProbe(ReadDirectX))(); } catch { s = null; }
        if (s is null) return Unavailable(at, DirectXKey, CatDisplay, "DirectX 矩陣", FactAvailability.ReadError, "顯示卡矩陣讀取失敗——不猜 feature level");
        return new HardwareFact(DirectXKey, CatDisplay, "DirectX 矩陣", $"顯示卡 {s.Adapters}、硬體 feature level {s.HardwareFeatureLevel}、WDDM 3.0 {s.Wddm30Adapters}。只讀驅動回報，不跑渲染測試。", "張", "Win32_VideoController（唯讀）", FactTrustLevel.Reported, false, at, s.Adapters);
    }

    private static DirectXState ReadDirectX()
    {
        int adapters = 0, hardware = 0, wddm = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterCompatibility FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                adapters++; hardware++;
                if (o["DriverVersion"] is string v && v.StartsWith("3", StringComparison.Ordinal)) wddm++;
                _ = o["AdapterCompatibility"];
            }
        }
        catch { return new(0, 0, 0); }
        return new(adapters, hardware, wddm);
    }

    public static HardwareFact HotPlugFact(DateTimeOffset at, HotPlugProbe? probe = null)
    {
        HotPlugState? s; try { s = (probe ?? new HotPlugProbe(ReadHotPlug))(); } catch { s = null; }
        if (s is null) return Unavailable(at, HotPlugKey, CatDisplay, "熱插拔事件史", FactAvailability.ReadError, "顯示器事件讀取失敗——不猜熱插拔");
        return new HardwareFact(HotPlugKey, CatDisplay, "熱插拔事件史", $"近 {s.LastDays} 天顯示器事件 {s.DisplayEvents}、擴充座事件 {s.DockEvents}。只讀計數，不讀事件內容。", "筆", "System Display／Kernel-PnP（只讀計數）", FactTrustLevel.Reported, false, at, s.DisplayEvents);
    }

    private static HotPlugState ReadHotPlug()
    {
        int display = 0, dock = 0;
        try
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[(EventID=4101 or EventID=4100 or EventID=225)]]") { ReverseDirection = true };
            using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
            for (int i = 0; i < 500; i++)
            {
                using var e = r.ReadEvent(); if (e is null) break;
                if (e.ProviderName?.Contains("Display", StringComparison.OrdinalIgnoreCase) == true) display++;
                else if (e.ProviderName?.Contains("Kernel-PnP", StringComparison.OrdinalIgnoreCase) == true) dock++;
            }
        }
        catch { return new(0, 0, 30); }
        return new(display, dock, 30);
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name, FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
