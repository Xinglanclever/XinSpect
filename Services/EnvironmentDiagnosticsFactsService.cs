using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 環境與診斷補充（Vol2 推進第 153–162 項）：PW-003 睡眠能力、DV-001 WSL、
/// DV-004 PATH、DV-007 容器 runtime、DV-008 工具鏈、GD-005 著色器快取、
/// GD-006 HDR 能力鏈、GD-008 繪圖板、SC-007 重啟摘要、SC-009 網路中斷事件。
/// </summary>
/// <remarks>
/// <para><b>讀取邊界</b>：登錄、PATH、WMI、事件記錄、檔案 metadata 全部唯讀；不啟停服務、不呼叫網路、不開啟裝置。</para>
/// <para><b>0 是值</b>：沒有發行版／服務／裝置是事實，不用 NotApplicable 代替 0；通路讀不到才用 Unknown／ReadError。</para>
/// </remarks>
public static class EnvironmentDiagnosticsFactsService
{
    private const string CatPower = "電源與恢復";
    private const string CatEnv = "環境與開發";
    private const string CatDisplay = "顯示卡";
    private const string CatDiag = "情境引導";

    public const string SleepKey = "pw.sleep.cap";
    public const string WslKey = "dv.wsl.summary";
    public const string PathKey = "dv.env.path";
    public const string ContainerKey = "dv.container.summary";
    public const string ToolchainKey = "dv.toolchain.summary";
    public const string ShaderCacheKey = "gd.shcache.summary";
    public const string HdrKey = "gd.hdr.summary";
    public const string TabletKey = "gd.tablet.count";
    public const string UptimeKey = "sc.uptime.summary";
    public const string NetDropKey = "sc.netdrop.summary";

    public sealed record ContainerState(string Name, string State);
    public sealed record ToolchainVersion(string Tool, string Version);
    public sealed record ShaderCacheState(int DirectoryCount, int FileCount, long Bytes);
    public sealed record NetworkDropEvent(DateTimeOffset When, int EventId);

    public delegate IReadOnlyList<PowerPolicyRow>? SleepProbe();
    public delegate int? WslProbe();
    public delegate string? EnvProbe(string name);
    public delegate IReadOnlyList<ContainerState>? ContainerProbe();
    public delegate IReadOnlyList<ToolchainVersion>? ToolchainProbe();
    public delegate ShaderCacheState? ShaderCacheProbe();
    public delegate IReadOnlyList<HdrCapabilityRow>? HdrProbe();
    public delegate int? TabletProbe();
    public delegate long? UptimeProbe();
    public delegate IReadOnlyList<NetworkDropEvent>? NetDropProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        SleepFact(at), WslFact(at), PathFact(at), ContainerFact(at), ToolchainFact(at),
        ShaderCacheFact(at), HdrFact(at), TabletFact(at), UptimeFact(at), NetDropFact(at),
    ];

    // ── PW-003 睡眠能力矩陣 ────────────────────────────────────────────────

    public static HardwareFact SleepFact(DateTimeOffset at, SleepProbe? probe = null)
    {
        IReadOnlyList<PowerPolicyRow>? rows;
        try { rows = (probe ?? new SleepProbe(PowerPolicyService.ReadSleepStateRowsForFacts))(); }
        catch { rows = null; }
        if (rows is null)
            return Unavailable(at, SleepKey, CatPower, "睡眠能力矩陣", FactAvailability.ReadError, "CallNtPowerInformation(SystemPowerCapabilities) 讀取失敗——不猜睡眠狀態");
        if (rows.Count == 0)
            return Unavailable(at, SleepKey, CatPower, "睡眠能力矩陣", FactAvailability.NotSupported, "平台沒有回報任何睡眠能力列");
        var s1 = rows.FirstOrDefault(r => r.Name.StartsWith("S1", StringComparison.Ordinal));
        var s3 = rows.FirstOrDefault(r => r.Name.StartsWith("S3", StringComparison.Ordinal));
        var s4 = rows.FirstOrDefault(r => r.Name.StartsWith("S4", StringComparison.Ordinal));
        return new HardwareFact(SleepKey, CatPower, "睡眠能力矩陣",
            $"S1 {s1?.Value ?? "未回報"}；S3 {s3?.Value ?? "未回報"}；S4 {s4?.Value ?? "未回報"}。界線：平台宣告能力不等於目前電源計畫一定可用；現代待命可能以 S0ix 取代 S3。",
            "", "CallNtPowerInformation(SystemPowerCapabilities)", FactTrustLevel.Reported, false, at, null);
    }

    // ── DV-001 WSL 盤點 ─────────────────────────────────────────────────────

    public static HardwareFact WslFact(DateTimeOffset at, WslProbe? probe = null)
    {
        int? count;
        try { count = (probe ?? new WslProbe(ReadWslDistroCount))(); }
        catch { count = null; }
        if (count is null)
            return Unavailable(at, WslKey, CatEnv, "WSL 發行版盤點", FactAvailability.NotApplicable, "沒有 WSL 安裝記錄或登錄不可讀——不把未安裝當成 0 個發行版");
        return new HardwareFact(WslKey, CatEnv, "WSL 發行版盤點",
            $"{count} 個已註冊發行版（0 是值；只計數，不列發行版名稱或路徑）。界線：登錄盤點不等於 WSL 核心可執行或能啟動。",
            "個", "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Lxss", FactTrustLevel.Reported, false, at, count);
    }

    private static int? ReadWslDistroCount()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        return key?.GetSubKeyNames().Length;
    }

    // ── DV-004 PATH 審計 ───────────────────────────────────────────────────

    public static HardwareFact PathFact(DateTimeOffset at, EnvProbe? env = null)
    {
        string? machine;
        string? user;
        try
        {
            var p = env ?? new EnvProbe(name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User));
            machine = p("PATH"); user = p("USER_PATH");
        }
        catch { return Unavailable(at, PathKey, CatEnv, "PATH 重複與衝突審計", FactAvailability.ReadError, "PATH 讀取失敗——不猜重複數"); }
        if (machine is null && user is null)
            return Unavailable(at, PathKey, CatEnv, "PATH 重複與衝突審計", FactAvailability.ReadError, "尚未取得 PATH；不把空字串當成 0 段");
        var segments = (machine ?? "").Split(';').Concat((user ?? "").Split(';')).Where(x => x.Length > 0).ToList();
        int duplicates = segments.GroupBy(NormalizePath, StringComparer.OrdinalIgnoreCase).Count(g => g.Count() > 1);
        return new HardwareFact(PathKey, CatEnv, "PATH 重複與衝突審計",
            $"PATH 共 {segments.Count} 段，重複（不分大小寫）{duplicates} 組。只報計數，不列本機路徑；存在性與優先順序不由本項判決。",
            "段", "Machine／User PATH（只取計數）", FactTrustLevel.Reported, false, at, segments.Count);
    }

    private static string NormalizePath(string value) => value.Trim().TrimEnd('\\');

    // ── DV-007 容器 runtime ────────────────────────────────────────────────

    public static HardwareFact ContainerFact(DateTimeOffset at, ContainerProbe? probe = null)
    {
        IReadOnlyList<ContainerState>? list;
        try { list = (probe ?? new ContainerProbe(ReadContainerStates))(); }
        catch { list = null; }
        if (list is null)
            return Unavailable(at, ContainerKey, CatEnv, "容器 runtime 狀態", FactAvailability.ReadError, "容器服務列舉失敗——不猜未安裝");
        var running = list.Count(x => x.State.Equals("Running", StringComparison.OrdinalIgnoreCase));
        return new HardwareFact(ContainerKey, CatEnv, "容器 runtime 狀態",
            list.Count == 0
                ? "0 個 Docker／containerd／Podman 服務。界線：這是服務清單事實，不代表一定不能安裝容器。"
                : $"{list.Count} 個容器相關服務，其中 {running} 個執行中（" + string.Join("、", list.Select(x => $"{x.Name}:{x.State}")) + "）。界線：服務存在不等於映像或容器可用。",
            "個", "WMI Win32_Service", list.Count == 0 ? FactTrustLevel.Reported : FactTrustLevel.Measured, false, at, list.Count);
    }

    private static IReadOnlyList<ContainerState>? ReadContainerStates()
    {
        var list = new List<ContainerState>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, State FROM Win32_Service WHERE Name='docker' OR Name='com.docker.service' OR Name='containerd' OR Name='podman'");
        foreach (ManagementObject o in searcher.Get())
            using (o) list.Add(new ContainerState(o["Name"]?.ToString() ?? "", o["State"]?.ToString() ?? ""));
        return list;
    }

    // ── DV-008 工具鏈 ──────────────────────────────────────────────────────

    public static HardwareFact ToolchainFact(DateTimeOffset at, ToolchainProbe? probe = null)
    {
        IReadOnlyList<ToolchainVersion>? list;
        try { list = (probe ?? new ToolchainProbe(ReadToolchainVersions))(); }
        catch { list = null; }
        if (list is null)
            return Unavailable(at, ToolchainKey, CatEnv, "開發工具鏈版本", FactAvailability.ReadError, "PATH 或檔案版本讀取失敗——不猜版本");
        return new HardwareFact(ToolchainKey, CatEnv, "開發工具鏈版本",
            list.Count == 0
                ? "PATH 上沒有找到 dotnet／git／pwsh／node／python。0 是值，不是讀取失敗。"
                : string.Join("；", list.Select(x => $"{x.Tool} {x.Version}")) + "。界線：PATH 上找到的工具只是候選，不代表專案使用該版本。",
            "項", "PATH 搜尋＋FileVersionInfo", list.Count == 0 ? FactTrustLevel.Reported : FactTrustLevel.Measured, false, at, list.Count);
    }

    private static IReadOnlyList<ToolchainVersion>? ReadToolchainVersions()
    {
        var result = new List<ToolchainVersion>();
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;
        var dirs = path.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (string tool in new[] { "dotnet", "git", "pwsh", "node", "python" })
        {
            string? found = null;
            foreach (string dir in dirs)
            {
                try
                {
                    string candidate = Path.Combine(dir, tool + ".exe");
                    if (File.Exists(candidate)) { found = candidate; break; }
                }
                catch { }
            }
            if (found is null) continue;
            string version;
            try { version = FileVersionInfo.GetVersionInfo(found).FileVersion ?? "版本未回報"; }
            catch { version = "版本讀不到"; }
            result.Add(new ToolchainVersion(tool, version));
        }
        return result;
    }

    // ── GD-005 著色器快取 ─────────────────────────────────────────────────

    public static HardwareFact ShaderCacheFact(DateTimeOffset at, ShaderCacheProbe? probe = null)
    {
        ShaderCacheState? state;
        try { state = (probe ?? new ShaderCacheProbe(ReadShaderCache))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, ShaderCacheKey, CatDisplay, "著色器快取狀態", FactAvailability.ReadError, "快取目錄掃描失敗——不猜大小");
        if (state.DirectoryCount == 0)
            return new HardwareFact(ShaderCacheKey, CatDisplay, "著色器快取狀態",
                "沒有找到常見 D3D/NVIDIA/AMD 著色器快取目錄。0 是值，不是讀取失敗。", "個",
                "LOCALAPPDATA 常見快取目錄", FactTrustLevel.Reported, false, at, 0);
        return new HardwareFact(ShaderCacheKey, CatDisplay, "著色器快取狀態",
            $"{state.DirectoryCount} 個常見快取目錄、{state.FileCount} 個檔案、合計 {state.Bytes / 1024.0 / 1024.0:F1} MiB。只列路徑類別與大小，不列完整路徑。",
            "MiB", "LOCALAPPDATA 常見快取目錄", FactTrustLevel.Measured, false, at, state.Bytes / 1024.0 / 1024.0);
    }

    private static ShaderCacheState? ReadShaderCache()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(root, "D3DSCache"),
            Path.Combine(root, "NVIDIA", "DXCache"),
            Path.Combine(root, "NVIDIA", "GLCache"),
            Path.Combine(root, "AMD", "DxCache"),
        ];
        int dirs = 0, files = 0; long bytes = 0;
        foreach (string dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;
            dirs++;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(100_000))
                {
                    try { bytes += new FileInfo(f).Length; files++; } catch { }
                }
            }
            catch { }
        }
        return new ShaderCacheState(dirs, files, bytes);
    }

    // ── GD-006 HDR 能力鏈 ──────────────────────────────────────────────────

    public static HardwareFact HdrFact(DateTimeOffset at, HdrProbe? probe = null)
    {
        IReadOnlyList<HdrCapabilityRow>? rows;
        try { rows = (probe ?? new HdrProbe(() => HdrCapabilityService.Read()))(); }
        catch { rows = null; }
        if (rows is null || rows.Count == 0)
            return Unavailable(at, HdrKey, CatDisplay, "HDR 能力鏈", FactAvailability.ReadError, "EDID／DisplayConfig 讀取失敗——不猜支援");
        string summary = string.Join("；", rows.Select(r => $"{r.Label}={r.Value}"));
        return new HardwareFact(HdrKey, CatDisplay, "HDR 能力鏈",
            summary + "。界線：EDID 宣告與 Windows 開關是兩層事實，合併呈現但不代為判定畫質。",
            "", "EDID CTA-861＋DisplayConfig", FactTrustLevel.Reported, false, at, null);
    }

    // ── GD-008 繪圖板盤點 ─────────────────────────────────────────────────

    public static HardwareFact TabletFact(DateTimeOffset at, TabletProbe? probe = null)
    {
        int? count;
        try { count = (probe ?? new TabletProbe(ReadTabletCount))(); }
        catch { count = null; }
        if (count is null)
            return Unavailable(at, TabletKey, CatDisplay, "繪圖板／數位筆裝置", FactAvailability.ReadError, "PnP 列舉失敗——不猜未連接");
        return new HardwareFact(TabletKey, CatDisplay, "繪圖板／數位筆裝置",
            $"{count} 個名稱含 tablet／digitizer／pen 的 PnP 裝置。只列數量，不開啟裝置、不讀筆壓資料。",
            "個", "WMI Win32_PnPEntity（名稱比對）", FactTrustLevel.Reported, false, at, count,
            count > 0 ? FactAvailability.Present : FactAvailability.NotApplicable,
            count > 0 ? null : "沒有偵測到繪圖板或數位筆裝置（0 是值）");
    }

    private static int? ReadTabletCount()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity");
        int count = 0;
        foreach (ManagementObject o in searcher.Get())
            using (o)
            {
                string name = o["Name"]?.ToString() ?? "";
                if (name.Contains("tablet", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("digitizer", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("pen", StringComparison.OrdinalIgnoreCase))
                    count++;
            }
        return count;
    }

    // ── SC-007 重啟摘要 ───────────────────────────────────────────────────

    public static HardwareFact UptimeFact(DateTimeOffset at, UptimeProbe? probe = null)
    {
        long? ms;
        try { ms = (probe ?? new UptimeProbe(() => Environment.TickCount64))(); }
        catch { ms = null; }
        if (ms is null || ms < 0)
            return Unavailable(at, UptimeKey, CatDiag, "多久沒重啟摘要", FactAvailability.ReadError, "uptime 讀取失敗——不把未知讀成 0");
        TimeSpan up = TimeSpan.FromMilliseconds(ms.Value);
        return new HardwareFact(UptimeKey, CatDiag, "多久沒重啟摘要",
            $"系統自啟動以來約 {up.Days} 天 {up.Hours} 小時 {up.Minutes} 分鐘。界線：這是系統 uptime，不等於更新是否已套用或是否需要重啟。",
            "天", "Environment.TickCount64", FactTrustLevel.Measured, false, at, up.TotalDays);
    }

    // ── SC-009 網路中斷事件 ───────────────────────────────────────────────

    public static HardwareFact NetDropFact(DateTimeOffset at, NetDropProbe? probe = null)
    {
        IReadOnlyList<NetworkDropEvent>? events;
        try { events = (probe ?? new NetDropProbe(ReadNetworkDropEvents))(); }
        catch { events = null; }
        if (events is null)
            return Unavailable(at, NetDropKey, CatDiag, "網路中斷事件", FactAvailability.NotSupported, "NetworkProfile／System 事件記錄頻道不可用——不猜斷線次數");
        int disconnects = events.Count(e => e.EventId == 10001 || e.EventId == 4202 || e.EventId == 6008);
        var last = events.OrderByDescending(e => e.When).FirstOrDefault();
        return new HardwareFact(NetDropKey, CatDiag, "網路中斷事件",
            $"近 30 天事件樣本 {events.Count} 筆，其中中斷類 {disconnects} 筆" +
            (last is null ? "；沒有可列出的時間。" : $"；最近一筆 {last.When:yyyy-MM-dd HH:mm}。") +
            "界線：事件是痕跡，不等於根因；不同網卡提供者的代碼可能不同。",
            "筆", "Microsoft-Windows-NetworkProfile／System 事件記錄", FactTrustLevel.Reported, false, at, events.Count);
    }

    private static IReadOnlyList<NetworkDropEvent>? ReadNetworkDropEvents()
    {
        try
        {
            var since = DateTime.Now.AddDays(-30);
            var query = new EventLogQuery("Microsoft-Windows-NetworkProfile/Operational", PathType.LogName,
                "*[System[TimeCreated[@SystemTime>='{since.ToUniversalTime():o}']]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            var list = new List<NetworkDropEvent>();
            for (int i = 0; i < 400; i++)
            {
                using var evt = reader.ReadEvent();
                if (evt is null) break;
                if (evt.TimeCreated is { } t) list.Add(new NetworkDropEvent(new DateTimeOffset(t, TimeSpan.Zero), evt.Id));
            }
            return list;
        }
        catch { return null; }
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
