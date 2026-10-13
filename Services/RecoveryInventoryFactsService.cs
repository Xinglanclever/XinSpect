using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>Vol2 第 223–232 項：LiveKernelReports、WER、懸掛、PendingFileRename、WinSxS、還原點、備份、資源耗盡、逐行程 I/O、連結審計。</summary>
/// <remarks>全部唯讀；不刪除、不修復、不觸發還原或備份；只列計數與分類，不列使用者檔名或路徑。</remarks>
public static class RecoveryInventoryFactsService
{
    private const string CatRecovery = "恢復";
    private const string CatSystem = "Windows 系統";
    private const string CatStorage = "儲存";

    public const string LiveKernelKey = "rc.lkr.summary";
    public const string WerKey = "rc.wer.summary";
    public const string HangKey = "rc.hang.summary";
    public const string PendingKey = "rc.pending";
    public const string WinSxsKey = "rc.winsxs.summary";
    public const string RestorePointKey = "rc.rp.summary";
    public const string BackupKey = "rc.backup.summary";
    public const string ExhaustKey = "rc.exhaust.summary";
    public const string ProcessIoKey = "rc.io.summary";
    public const string LinkKey = "rc.link.summary";

    public sealed record LiveKernelState(int Reports, int Minidumps, int Other);
    public sealed record WerState(int Reports, int AppCrash, int KernelCrash);
    public sealed record HangState(int HangEvents, int LastDays);
    public sealed record PendingState(int PendingFileRenameValues, int PendingSessionValues);
    public sealed record WinSxsState(int PendingPackages, int CorruptPackages, int SupersededPackages);
    public sealed record RestorePointState(int RestorePoints, DateTimeOffset? Latest);
    public sealed record BackupState(int? FileHistoryEnabled, int? SystemRestoreEnabled, int VolumeShadowCopies);
    public sealed record ExhaustState(int ResourceExhaustionEvents, int MemoryEvents, int HandleEvents);
    public sealed record ProcessIoState(int Samples, int ProcessCount, long TotalBytes);
    public sealed record LinkState(int ReparsePoints, int Junctions, int Symlinks, int Broken);

    public delegate LiveKernelState? LiveKernelProbe();
    public delegate WerState? WerProbe();
    public delegate HangState? HangProbe();
    public delegate PendingState? PendingProbe();
    public delegate WinSxsState? WinSxsProbe();
    public delegate RestorePointState? RestorePointProbe();
    public delegate BackupState? BackupProbe();
    public delegate ExhaustState? ExhaustProbe();
    public delegate ProcessIoState? ProcessIoProbe();
    public delegate LinkState? LinkProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        LiveKernelFact(at), WerFact(at), HangFact(at), PendingFact(at), WinSxsFact(at),
        RestorePointFact(at), BackupFact(at), ExhaustFact(at), ProcessIoFact(at), LinkFact(at),
    ];

    public static HardwareFact LiveKernelFact(DateTimeOffset at, LiveKernelProbe? probe = null)
    {
        LiveKernelState? s; try { s = (probe ?? new LiveKernelProbe(ReadLiveKernel))(); } catch { s = null; }
        if (s is null) return Unavailable(at, LiveKernelKey, CatRecovery, "LiveKernelReports", FactAvailability.ReadError, "LiveKernelReports 讀取失敗——不猜核心報告");
        return new HardwareFact(LiveKernelKey, CatRecovery, "LiveKernelReports", $"LiveKernel 報告 {s.Reports}：minidump {s.Minidumps}、其他 {s.Other}。只列計數與類型；不讀 dump 內容。", "個", @"%SystemRoot%\LiveKernelReports（只讀 metadata）", FactTrustLevel.Reported, false, at, s.Reports);
    }

    private static LiveKernelState ReadLiveKernel()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "LiveKernelReports");
        if (!Directory.Exists(dir)) return new(0, 0, 0);
        int total = 0, dump = 0, other = 0;
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            total++;
            if (f.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)) dump++; else other++;
        }
        return new(total, dump, other);
    }

    public static HardwareFact WerFact(DateTimeOffset at, WerProbe? probe = null)
    {
        WerState? s; try { s = (probe ?? new WerProbe(ReadWer))(); } catch { s = null; }
        if (s is null) return Unavailable(at, WerKey, CatRecovery, "WER 崩潰史聚合", FactAvailability.ReadError, "WER 目錄讀取失敗——不猜崩潰史");
        return new HardwareFact(WerKey, CatRecovery, "WER 崩潰史聚合", $"WER 報告 {s.Reports}：應用程式崩潰 {s.AppCrash}、核心崩潰 {s.KernelCrash}。只列計數與分類，不列程式名稱或使用者路徑。", "份", "WER ReportArchive／ReportQueue（metadata）", FactTrustLevel.Reported, false, at, s.Reports);
    }

    private static WerState ReadWer()
    {
        int total = 0, app = 0, kernel = 0;
        foreach (string root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportArchive"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportQueue"),
        })
        {
            if (!Directory.Exists(root)) continue;
            foreach (string d in Directory.EnumerateDirectories(root))
            {
                total++;
                string name = Path.GetFileName(d);
                if (name.Contains("Kernel", StringComparison.OrdinalIgnoreCase)) kernel++; else app++;
            }
        }
        return new(total, app, kernel);
    }

    public static HardwareFact HangFact(DateTimeOffset at, HangProbe? probe = null)
    {
        HangState? s; try { s = (probe ?? new HangProbe(ReadHang))(); } catch { s = null; }
        if (s is null) return Unavailable(at, HangKey, CatRecovery, "懸掛歷史", FactAvailability.ReadError, "懸掛事件讀取失敗——不猜懸掛次數");
        return new HardwareFact(HangKey, CatRecovery, "懸掛歷史", $"近 {s.LastDays} 天 Event 1002 懸掛事件 {s.HangEvents} 筆。事件是痕跡不是根因；不讀事件內容。", "筆", "Application 1002（只讀計數）", FactTrustLevel.Reported, false, at, s.HangEvents);
    }

    private static HangState ReadHang()
    {
        int n = 0;
        try
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("Application", System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[(EventID=1002)]]") { ReverseDirection = true };
            using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
            for (int i = 0; i < 500; i++) { using var e = r.ReadEvent(); if (e is null) break; n++; }
        }
        catch { return new(0, 30); }
        return new(n, 30);
    }

    public static HardwareFact PendingFact(DateTimeOffset at, PendingProbe? probe = null)
    {
        PendingState? s; try { s = (probe ?? new PendingProbe(ReadPending))(); } catch { s = null; }
        if (s is null) return Unavailable(at, PendingKey, CatRecovery, "PendingFileRename 審計", FactAvailability.ReadError, "PendingFileRename 讀取失敗——不猜重啟現場");
        return new HardwareFact(PendingKey, CatRecovery, "PendingFileRename 審計", $"PendingFileRenameOperations {s.PendingFileRenameValues} 項、PendingSessionManager {s.PendingSessionValues} 項。只報計數，不列檔名或路徑。", "項", @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager", FactTrustLevel.Reported, false, at, s.PendingFileRenameValues);
    }

    private static PendingState ReadPending()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        int p = k?.GetValue("PendingFileRenameOperations") is string[] a ? a.Length : 0;
        int s = k?.GetValue("PendingSessionManagerOperations") is string[] b ? b.Length : 0;
        return new(p, s);
    }

    public static HardwareFact WinSxsFact(DateTimeOffset at, WinSxsProbe? probe = null)
    {
        WinSxsState? s; try { s = (probe ?? new WinSxsProbe(ReadWinSxs))(); } catch { s = null; }
        if (s is null) return Unavailable(at, WinSxsKey, CatRecovery, "WinSxS 健康", FactAvailability.ReadError, "WinSxS 狀態讀取失敗——不猜元件健康");
        return new HardwareFact(WinSxsKey, CatRecovery, "WinSxS 健康", $"CBS pending {s.PendingPackages}、損壞 {s.CorruptPackages}、被取代 {s.SupersededPackages}。只讀 CBS 狀態，不清理、不修復。", "項", @"CBS.log／WinSxS（只讀計數）", FactTrustLevel.Reported, false, at, s.PendingPackages);
    }

    private static WinSxsState ReadWinSxs()
    {
        string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Logs\CBS\CBS.log");
        if (!File.Exists(log)) return new(0, 0, 0);
        int pending = 0, corrupt = 0, superseded = 0;
        try
        {
            foreach (string line in File.ReadLines(log).Take(20000))
            {
                if (line.Contains("pending", StringComparison.OrdinalIgnoreCase)) pending++;
                else if (line.Contains("corrupt", StringComparison.OrdinalIgnoreCase)) corrupt++;
                else if (line.Contains("superseded", StringComparison.OrdinalIgnoreCase)) superseded++;
            }
        }
        catch { }
        return new(pending, corrupt, superseded);
    }

    public static HardwareFact RestorePointFact(DateTimeOffset at, RestorePointProbe? probe = null)
    {
        RestorePointState? s; try { s = (probe ?? new RestorePointProbe(ReadRestorePoints))(); } catch { s = null; }
        if (s is null) return Unavailable(at, RestorePointKey, CatRecovery, "還原點清單", FactAvailability.NotApplicable, "系統還原未啟用或無法列出——沒有可列出的還原點");
        return new HardwareFact(RestorePointKey, CatRecovery, "還原點清單", $"還原點 {s.RestorePoints} 個" + (s.Latest is null ? "；最近時間未回報。" : $"；最近 {s.Latest:yyyy-MM-dd HH:mm}。") + "只讀清單，不建立或還原。", "個", "SystemRestore WMI（唯讀）", FactTrustLevel.Reported, false, at, s.RestorePoints);
    }

    private static RestorePointState? ReadRestorePoints()
    {
        try
        {
            int n = 0; DateTimeOffset? latest = null;
            using var s = new ManagementObjectSearcher(@"root\default", "SELECT CreationTime FROM SystemRestore");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                n++;
                if (o["CreationTime"] is string t && DateTimeOffset.TryParse(t, out var dt) && (latest is null || dt > latest)) latest = dt;
            }
            return new(n, latest);
        }
        catch { return null; }
    }

    public static HardwareFact BackupFact(DateTimeOffset at, BackupProbe? probe = null)
    {
        BackupState? s; try { s = (probe ?? new BackupProbe(ReadBackup))(); } catch { s = null; }
        if (s is null) return Unavailable(at, BackupKey, CatRecovery, "備份狀態事實", FactAvailability.ReadError, "備份設定讀取失敗——不猜備份狀態");
        return new HardwareFact(BackupKey, CatRecovery, "備份狀態事實", $"檔案歷程 {Raw(s.FileHistoryEnabled)}、系統還原 {Raw(s.SystemRestoreEnabled)}、VSS 陰影 {s.VolumeShadowCopies}。只讀開關與數量，不執行備份或還原。", "", "FileHistory／SystemRestore／VSS（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static BackupState ReadBackup()
    {
        int? fh = ReadInt(@"SOFTWARE\Microsoft\Windows\CurrentVersion\FileHistory", "Enabled");
        int? sr = ReadInt(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "RPSessionInterval");
        int vss = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT ID FROM Win32_ShadowCopy");
            foreach (ManagementObject o in s.Get()) using (o) vss++;
        }
        catch { }
        return new(fh, sr, vss);
    }

    public static HardwareFact ExhaustFact(DateTimeOffset at, ExhaustProbe? probe = null)
    {
        ExhaustState? s; try { s = (probe ?? new ExhaustProbe(ReadExhaust))(); } catch { s = null; }
        if (s is null) return Unavailable(at, ExhaustKey, CatRecovery, "資源耗盡事件", FactAvailability.ReadError, "資源耗盡事件讀取失敗——不猜耗盡");
        return new HardwareFact(ExhaustKey, CatRecovery, "資源耗盡事件", $"Resource-Exhaustion 事件 {s.ResourceExhaustionEvents}：記憶體 {s.MemoryEvents}、控制代碼 {s.HandleEvents}。事件是痕跡不是根因。", "筆", "System Resource-Exhaustion-Detector（只讀計數）", FactTrustLevel.Reported, false, at, s.ResourceExhaustionEvents);
    }

    private static ExhaustState ReadExhaust()
    {
        int total = 0, mem = 0, handle = 0;
        try
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector']]]") { ReverseDirection = true };
            using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
            for (int i = 0; i < 500; i++)
            {
                using var e = r.ReadEvent(); if (e is null) break;
                total++;
                string? msg = e.Properties.Count > 0 ? e.Properties[0].Value?.ToString() : null;
                if (msg?.Contains("memory", StringComparison.OrdinalIgnoreCase) == true) mem++;
                else if (msg?.Contains("handle", StringComparison.OrdinalIgnoreCase) == true) handle++;
            }
        }
        catch { return new(0, 0, 0); }
        return new(total, mem, handle);
    }

    public static HardwareFact ProcessIoFact(DateTimeOffset at, ProcessIoProbe? probe = null)
    {
        ProcessIoState? s; try { s = (probe ?? new ProcessIoProbe(ReadProcessIo))(); } catch { s = null; }
        if (s is null) return Unavailable(at, ProcessIoKey, CatRecovery, "逐行程 I/O 排行", FactAvailability.ReadError, "行程 I/O 讀取失敗——不猜排行");
        return new HardwareFact(ProcessIoKey, CatRecovery, "逐行程 I/O 排行", $"樣本 {s.Samples}、程序 {s.ProcessCount}、累計 I/O {s.TotalBytes} bytes。只報聚合，不列程序名稱；這是 since-boot 計數，非即時取樣。", "bytes", "Win32_PerfFormattedData（唯讀）", FactTrustLevel.Reported, false, at, s.TotalBytes);
    }

    private static ProcessIoState ReadProcessIo()
    {
        int samples = 0, processes = 0; long total = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT ReadTransferCount, WriteTransferCount FROM Win32_PerfFormattedData_PerfProc_Process");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                samples++; processes++;
                long r = ToLong(o["ReadTransferCount"]), w = ToLong(o["WriteTransferCount"]);
                total += Math.Max(0, r) + Math.Max(0, w);
            }
        }
        catch { return new(0, 0, 0); }
        return new(samples, processes, total);
    }

    public static HardwareFact LinkFact(DateTimeOffset at, LinkProbe? probe = null)
    {
        LinkState? s; try { s = (probe ?? new LinkProbe(ReadLinks))(); } catch { s = null; }
        if (s is null) return Unavailable(at, LinkKey, CatRecovery, "連結審計", FactAvailability.ReadError, "連結審計讀取失敗——不猜孤兒連結");
        return new HardwareFact(LinkKey, CatRecovery, "連結審計", $"重解析點 {s.ReparsePoints}、junction {s.Junctions}、symlink {s.Symlinks}、破損 {s.Broken}。只讀 metadata，不列目標路徑；破損是風險面不是判決。", "個", @"C:\ 根目錄與常見程式目錄（metadata）", FactTrustLevel.Reported, false, at, s.ReparsePoints);
    }

    private static LinkState ReadLinks()
    {
        int reparse = 0, junctions = 0, symlinks = 0, broken = 0;
        foreach (string root in new[] { @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData" })
        {
            if (!Directory.Exists(root)) continue;
            foreach (string d in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var info = new DirectoryInfo(d);
                    if ((info.Attributes & FileAttributes.ReparsePoint) == 0) continue;
                    reparse++;
                    if (info.LinkTarget is null) broken++;
                    else if (info.LinkTarget.StartsWith(@"\??\")) junctions++;
                    else symlinks++;
                }
                catch { }
            }
        }
        return new(reparse, junctions, symlinks, broken);
    }

    private static long ToLong(object? v) { try { return v is null ? 0 : Convert.ToInt64(v); } catch { return 0; } }
    private static int? ReadInt(string path, string name) { try { using var k = Registry.LocalMachine.OpenSubKey(path); return k?.GetValue(name) is { } v ? Convert.ToInt32(v) : null; } catch { return null; } }
    private static string Raw(int? v) => v?.ToString() ?? "未設定";
    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name, FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}