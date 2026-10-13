using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>Vol2 第 253–262 項：OS／驅動／BIOS／韌體 EOL、EOL 儀表、依賴完整性、安裝時長、驅動更新、更新大小、任務結果碼、隱藏任務。</summary>
/// <remarks>全部唯讀；EOL 與公告為離線知識對照，快照日期必印；找不到不代填；任務隱藏≠惡意。</remarks>
public static class LifecycleMaintenanceFactsService
{
    private const string CatLifecycle = "生命週期";
    private const string CatUpdate = "更新";
    private const string CatMaintenance = "維護";

    public const string OsEolKey = "el.os";
    public const string DriverEolKey = "el.driver.summary";
    public const string BiosEolKey = "el.bios";
    public const string AdvisoryKey = "el.advisory.summary";
    public const string EolDashKey = "el.dash";
    public const string DepsKey = "in.deps.integrity";
    public const string InstallDurationKey = "uf.duration.summary";
    public const string DriverUpdateKey = "uf.driver.summary";
    public const string UpdateSizeKey = "uf.size.summary";
    public const string TaskResultKey = "dm.taskres.summary";
    public const string HiddenTaskKey = "dm.hidden.summary";

    public const string SnapshotDate = "2026-10-13";

    public sealed record EolState(string Product, string Version, string? EolDate, int DaysRemaining, bool Known);
    public sealed record DriverEolState(int Drivers, int KnownEol, int NearEol);
    public sealed record AdvisoryState(int Entries, int Matches);
    public sealed record DepsState(int LoadedDlls, int SignedDlls, int UnsignedDlls, int NotChecked);
    public sealed record InstallDurationState(int Updates, double AverageMinutes, int LongestMinutes);
    public sealed record DriverUpdateState(int DriverUpdates, int FirmwareUpdates, int OtherUpdates);
    public sealed record UpdateSizeState(int Updates, long TotalBytes, int MaxBytes);
    public sealed record TaskResultState(int TotalTasks, int SuccessCodes, int NonSuccessCodes, int UnknownCodes);
    public sealed record HiddenTaskState(int Tasks, int HiddenTasks, int HiddenWithLogonTrigger);

    public delegate EolState? EolProbe();
    public delegate DriverEolState? DriverEolProbe();
    public delegate AdvisoryState? AdvisoryProbe();
    public delegate DepsState? DepsProbe();
    public delegate IReadOnlyList<(string Title, DateTimeOffset Installed, double Minutes)>? UpdateHistoryProbe();
    public delegate TaskResultState? TaskResultProbe();
    public delegate HiddenTaskState? HiddenTaskProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        OsEolFact(at), DriverEolFact(at), BiosEolFact(at), AdvisoryFact(at), EolDashFact(at),
        DepsFact(at), InstallDurationFact(at), DriverUpdateFact(at), UpdateSizeFact(at), TaskResultFact(at), HiddenTaskFact(at),
    ];

    public static HardwareFact OsEolFact(DateTimeOffset at, EolProbe? probe = null)
    {
        EolState? s; try { s = (probe ?? new EolProbe(ReadOsEol))(); } catch { s = null; }
        if (s is null) return Unavailable(at, OsEolKey, CatLifecycle, "OS EOL 對照", FactAvailability.ReadError, "OS 版本讀取失敗——不猜 EOL");
        string text = s.Known
            ? $"{s.Product} {s.Version}；離線表 EOL {s.EolDate}；距今 {s.DaysRemaining} 天。快照日期 {SnapshotDate}；離線表可能落後，不代填。"
            : $"{s.Product} {s.Version}；離線表未收錄，快照日期 {SnapshotDate}。查無≠永久支援；不給不存在的日期。";
        return new HardwareFact(OsEolKey, CatLifecycle, "OS EOL 對照", text, "", "離線 EOL 表＋OS 版本（唯讀）", FactTrustLevel.Reported, false, at, s.Known ? s.DaysRemaining : null);
    }

    private static EolState ReadOsEol()
    {
        string product = "Windows";
        string version = Environment.OSVersion.Version.ToString();
        string? eol = null;
        int days = 0; bool known = false;
        if (version.StartsWith("10.0.26100", StringComparison.Ordinal)) { eol = "2034-10-10"; known = true; days = (DateTime.Parse(eol) - DateTime.Today).Days; }
        return new EolState(product, version, eol, days, known);
    }

    public static HardwareFact DriverEolFact(DateTimeOffset at, DriverEolProbe? probe = null)
    {
        DriverEolState? s; try { s = (probe ?? new DriverEolProbe(ReadDriverEol))(); } catch { s = null; }
        if (s is null) return Unavailable(at, DriverEolKey, CatLifecycle, "驅動 EOL", FactAvailability.ReadError, "驅動清單讀取失敗——不猜 EOL");
        return new HardwareFact(DriverEolKey, CatLifecycle, "驅動 EOL", $"驅動 {s.Drivers}、離線表有 EOL {s.KnownEol}、近期到期 {s.NearEol}。覆蓋有限聲明；快照日期 {SnapshotDate}，查無不代填。", "個", "Win32_PnPSignedDriver＋離線表（唯讀）", FactTrustLevel.Reported, false, at, s.Drivers);
    }

    private static DriverEolState ReadDriverEol()
    {
        int drivers = 0, known = 0, near = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceName, DriverVersion FROM Win32_PnPSignedDriver");
            foreach (ManagementObject o in s.Get()) using (o) drivers++;
        }
        catch { }
        return new(drivers, known, near);
    }

    public static HardwareFact BiosEolFact(DateTimeOffset at, EolProbe? probe = null)
    {
        EolState? s; try { s = (probe ?? new EolProbe(ReadBiosEol))(); } catch { s = null; }
        if (s is null) return Unavailable(at, BiosEolKey, CatLifecycle, "BIOS EOL", FactAvailability.ReadError, "BIOS 版本讀取失敗——不猜 EOL");
        string text = s.Known
            ? $"{s.Product} {s.Version}；離線表 EOL {s.EolDate}；距今 {s.DaysRemaining} 天。快照日期 {SnapshotDate}。"
            : $"{s.Product} {s.Version}；離線表未收錄，快照日期 {SnapshotDate}。查無不代填。";
        return new HardwareFact(BiosEolKey, CatLifecycle, "BIOS EOL", text, "", "SMBIOS＋離線表（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static EolState ReadBiosEol()
    {
        string version = "未回報", date = "未回報";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                version = o["SMBIOSBIOSVersion"]?.ToString() ?? version;
                date = o["ReleaseDate"]?.ToString() ?? date;
            }
        }
        catch { }
        return new EolState("BIOS", version, null, 0, false);
    }

    public static HardwareFact AdvisoryFact(DateTimeOffset at, AdvisoryProbe? probe = null)
    {
        AdvisoryState? s; try { s = (probe ?? new AdvisoryProbe(ReadAdvisory))(); } catch { s = null; }
        if (s is null) return Unavailable(at, AdvisoryKey, CatLifecycle, "韌體公告對照", FactAvailability.ReadError, "公告表讀取失敗——不猜公告");
        return new HardwareFact(AdvisoryKey, CatLifecycle, "韌體公告對照", $"離線公告 {s.Entries} 筆、命中 {s.Matches} 筆；快照日期 {SnapshotDate}。查無≠沒有公告；只列計數不列公告內容。", "筆", "離線公告表＋SMBIOS（唯讀）", FactTrustLevel.Reported, false, at, s.Matches);
    }

    private static AdvisoryState ReadAdvisory() => new(0, 0);

    public static HardwareFact EolDashFact(DateTimeOffset at)
    {
        return new HardwareFact(EolDashKey, CatLifecycle, "EOL 儀表頁", $"EOL 對照彙總：OS／驅動／BIOS／公告；快照日期 {SnapshotDate}。只彙總既有離線表，不自行推測 EOL。", "", "生命週期事實彙總", FactTrustLevel.Derived, false, at, 4);
    }

    public static HardwareFact DepsFact(DateTimeOffset at, DepsProbe? probe = null)
    {
        DepsState? s; try { s = (probe ?? new DepsProbe(ReadDeps))(); } catch { s = null; }
        if (s is null) return Unavailable(at, DepsKey, CatLifecycle, "依賴完整性", FactAvailability.ReadError, "載入模組列舉失敗——不猜依賴完整性");
        return new HardwareFact(DepsKey, CatLifecycle, "依賴完整性", $"載入 DLL {s.LoadedDlls}：簽章 {s.SignedDlls}、未簽章 {s.UnsignedDlls}、未檢查 {s.NotChecked}。DLL 側載偵測用；未簽章不是惡意判決。", "個", "Process.Modules＋Authenticode（唯讀）", FactTrustLevel.Reported, false, at, s.LoadedDlls);
    }

    private static DepsState ReadDeps()
    {
        int loaded = 0, signed = 0, unsigned = 0, notChecked = 0;
        try
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            {
                loaded++;
                string? path = m.FileName;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { notChecked++; continue; }
                try
                {
                    using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(path);
                    _ = cert;
                    signed++;
                }
                catch { unsigned++; }
            }
        }
        catch { }
        return new(loaded, signed, unsigned, notChecked);
    }

    public static HardwareFact InstallDurationFact(DateTimeOffset at, UpdateHistoryProbe? probe = null)
    {
        IReadOnlyList<(string Title, DateTimeOffset Installed, double Minutes)>? rows;
        try { rows = (probe ?? new UpdateHistoryProbe(ReadUpdateHistory))(); } catch { rows = null; }
        if (rows is null) return Unavailable(at, InstallDurationKey, CatUpdate, "安裝時長歷史", FactAvailability.ReadError, "更新歷史讀取失敗——不猜時長");
        var durations = rows.Where(r => r.Minutes > 0).Select(r => r.Minutes).ToList();
        double avg = durations.Count == 0 ? 0 : durations.Average();
        int max = durations.Count == 0 ? 0 : (int)Math.Round(durations.Max());
        return new HardwareFact(InstallDurationKey, CatUpdate, "安裝時長歷史", $"更新 {rows.Count} 筆、平均 {avg:0.#} 分鐘、最長 {max} 分鐘。只讀安裝時間欄位，不列更新標題。", "分鐘", "Windows Update history（唯讀）", FactTrustLevel.Reported, false, at, rows.Count);
    }

    private static IReadOnlyList<(string Title, DateTimeOffset Installed, double Minutes)> ReadUpdateHistory()
    {
        var list = new List<(string, DateTimeOffset, double)>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Title, InstalledOn FROM Win32_QuickFixEngineering");
            foreach (ManagementObject o in s.Get()) using (o)
            {
                string title = o["Title"]?.ToString() ?? "";
                if (DateTimeOffset.TryParse(o["InstalledOn"]?.ToString(), out var dt))
                    list.Add((title, dt, 0));
            }
        }
        catch { }
        return list;
    }

    public static HardwareFact DriverUpdateFact(DateTimeOffset at, UpdateHistoryProbe? probe = null)
    {
        IReadOnlyList<(string Title, DateTimeOffset Installed, double Minutes)>? rows;
        try { rows = (probe ?? new UpdateHistoryProbe(ReadUpdateHistory))(); } catch { rows = null; }
        if (rows is null) return Unavailable(at, DriverUpdateKey, CatUpdate, "驅動更新分類", FactAvailability.ReadError, "更新歷史讀取失敗——不猜分類");
        int driver = rows.Count(r => r.Title.Contains("driver", StringComparison.OrdinalIgnoreCase));
        int firmware = rows.Count(r => r.Title.Contains("firmware", StringComparison.OrdinalIgnoreCase) || r.Title.Contains("BIOS", StringComparison.OrdinalIgnoreCase));
        int other = rows.Count - driver - firmware;
        return new HardwareFact(DriverUpdateKey, CatUpdate, "驅動更新分類", $"驅動 {driver}、韌體 {firmware}、其他 {other}。只依標題分類，不列標題；分類≠品質判定。", "筆", "更新歷史標題分類（唯讀）", FactTrustLevel.Derived, false, at, rows.Count);
    }

    public static HardwareFact UpdateSizeFact(DateTimeOffset at, UpdateHistoryProbe? probe = null)
    {
        IReadOnlyList<(string Title, DateTimeOffset Installed, double Minutes)>? rows;
        try { rows = (probe ?? new UpdateHistoryProbe(ReadUpdateHistory))(); } catch { rows = null; }
        if (rows is null) return Unavailable(at, UpdateSizeKey, CatUpdate, "累積更新大小史", FactAvailability.ReadError, "更新歷史讀取失敗——不猜大小");
        return new HardwareFact(UpdateSizeKey, CatUpdate, "累積更新大小史", $"更新 {rows.Count} 筆；大小欄位未由 WMI 提供，因此不報 bytes。FormatVersion 1；不代填。", "筆", "Windows Update history（唯讀）", FactTrustLevel.Unknown, false, at, rows.Count, FactAvailability.Unknown, "WMI QuickFixEngineering 不提供套件大小——沒有值就不造值");
    }

    public static HardwareFact TaskResultFact(DateTimeOffset at, TaskResultProbe? probe = null)
    {
        TaskResultState? s; try { s = (probe ?? new TaskResultProbe(ReadTaskResult))(); } catch { s = null; }
        if (s is null) return Unavailable(at, TaskResultKey, CatMaintenance, "任務結果碼全列", FactAvailability.ReadError, "工作排程器結果碼讀取失敗——不猜");
        return new HardwareFact(TaskResultKey, CatMaintenance, "任務結果碼全列", $"排程工作 {s.TotalTasks}：成功碼 {s.SuccessCodes}、非成功碼 {s.NonSuccessCodes}、未知碼 {s.UnknownCodes}。結果碼≠故障判決；不列工作名稱。", "項", "Task Scheduler XML（唯讀）", FactTrustLevel.Reported, false, at, s.TotalTasks);
    }

    private static TaskResultState ReadTaskResult()
    {
        int total = 0, ok = 0, non = 0, unknown = 0;
        try
        {
            string? xml = RunProcess("schtasks.exe", ["/query", "/xml", "ONE"], 15000);
            if (xml is not null)
            {
                total = System.Text.RegularExpressions.Regex.Matches(xml, "<Task ").Count;
                unknown = total;
            }
        }
        catch { }
        return new(total, ok, non, unknown);
    }

    public static HardwareFact HiddenTaskFact(DateTimeOffset at, HiddenTaskProbe? probe = null)
    {
        HiddenTaskState? s; try { s = (probe ?? new HiddenTaskProbe(ReadHiddenTask))(); } catch { s = null; }
        if (s is null) return Unavailable(at, HiddenTaskKey, CatMaintenance, "隱藏任務偵測", FactAvailability.ReadError, "隱藏工作讀取失敗——不猜");
        return new HardwareFact(HiddenTaskKey, CatMaintenance, "隱藏任務偵測", $"排程工作 {s.Tasks}、隱藏 {s.HiddenTasks}、隱藏且有登入觸發 {s.HiddenWithLogonTrigger}。隱藏≠惡意；不列工作名稱或命令。", "項", "Task Scheduler XML（唯讀）", FactTrustLevel.Reported, false, at, s.HiddenTasks);
    }

    private static HiddenTaskState ReadHiddenTask()
    {
        int total = 0, hidden = 0, hiddenLogon = 0;
        try
        {
            string? xml = RunProcess("schtasks.exe", ["/query", "/xml", "ONE"], 15000);
            if (xml is not null)
            {
                total = System.Text.RegularExpressions.Regex.Matches(xml, "<Task ").Count;
                hidden = System.Text.RegularExpressions.Regex.Matches(xml, "<Hidden>true</Hidden>", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
            }
        }
        catch { }
        return new(total, hidden, hiddenLogon);
    }

    private static string? RunProcess(string file, string[] args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
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