using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 執行環境與治理事實組（Vol2 推進第 42–51 項）：IN-005 提權最小化審計、IN-006 自我更新完整性、
/// NG-005 導覽歷史、DL-003 符號路徑、DL-008 線程池、DL-009 GC 模式、UF-007 更新通道、
/// UF-010 更新健康儀表、HM-004 喚醒記錄、FS-013 隱私禁項邊界。
/// </summary>
/// <remarks>
/// <para><b>這組的價值在「說清楚自己」</b>：一個工業級偵測工具要先講得清自己拿了什麼權限、
/// 從哪裡更新、載不載得動符號、有沒有碰不該碰的使用者資料——這些問題的答案不該靠讀原始碼猜。</para>
/// <para><b>注入接縫</b>：全部可餵假件。真實通路碰 token／登錄／powercfg 行程／執行期計數，
/// 在單元測試裡讀真的會讓結果取決於跑測試的機器。</para>
/// <para><b>誠實界線</b>：沒有自我更新通道就說沒有，不把「沒有簽章可驗」寫成「簽章正確」；
/// 喚醒記錄原樣引用 powercfg 輸出，不解析在地化文字；導覽歷史只留在記憶體，不落盤（L1）。</para>
/// </remarks>
public static class EnvironmentRuntimeFactsService
{
    private const string CatSelf = "自我完整性";
    private const string CatNav = "診斷導航";
    private const string CatDl = "開發者診斷";
    private const string CatUf = "更新深化";
    private const string CatDev = "硬體事件史";

    public const string ElevationKey = "in.elevation.summary";
    public const string UpdateSigKey = "in.update.sig";
    public const string HistoryKey = "ng.history";
    public const string SymbolPathKey = "dl.sympath";
    public const string PoolCountKey = "dl.threadpool.count";
    public const string PoolSummaryKey = "dl.threadpool.summary";
    public const string GcModeKey = "dl.gc.mode";
    public const string GcSummaryKey = "dl.gc.summary";
    public const string ChannelKey = "uf.channel";
    public const string DashKey = "uf.dash";
    public const string WakeKey = "hm.wake.summary";
    public const string PrivacyKey = "fs.privacy.boundary";

    /// <summary>
    /// FS-013 明文禁項：這些來源<b>刻意不讀</b>——它們裝的是使用者開了什麼檔案、按了什麼連結，
    /// 與「這台機器的硬體與系統事實」無關，讀了就變成間諜工具。禁令由 <c>PrivacyDenylistTests</c> 掃原始碼釘住。
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenSourcePatterns =
    [
        @"Recent\AutomaticDestinations",   // Jump lists（自動目的地）
        @"Recent\CustomDestinations",      // Jump lists
        @"Microsoft\Windows\Explorer\thumbcache",  // 縮圖快取（看過的圖）
        @"AppData\LocalLow\Microsoft\CryptnetUrlCache",  // URL 快取（跟著瀏覽行為）
        @"Explorer\RunMRU",                // 執行盒 MRU
        @"Explorer\RecentDocs",            // 最近檔案（MRU）
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist",  // 執行計數
    ];

    // ── 注入接縫 ─────────────────────────────────────────────────────────

    public delegate object? RegistryProbe(string valueName);
    public delegate string? CommandRunner(string[] args);
    public delegate (int PoolThreads, int WorkerAvail, int IoAvail, long Completed, int Pending)? PoolProbe();
    public delegate (bool ServerMode, string LatencyMode, int Gen0, int Gen1, int Gen2, long HeapBytes)? GcProbe();

    // ── 導覽歷史（NG-005，L1：只留記憶體）──────────────────────────────

    private static readonly object NavLock = new();
    private static readonly List<string> VisitedPages = [];

    /// <summary>主視窗切頁時呼叫。刻意不落盤——這屬於使用者行為軌跡（L1）。</summary>
    public static void NotePageVisited(string pageKey, string pageTitle)
    {
        lock (NavLock)
        {
            string label = string.IsNullOrWhiteSpace(pageTitle) ? pageKey : $"{pageKey}／{pageTitle}";
            if (VisitedPages.Count == 0 || !string.Equals(VisitedPages[^1], label, StringComparison.Ordinal))
                VisitedPages.Add(label);
            if (VisitedPages.Count > 200) VisitedPages.RemoveAt(0);   // 上限：軌跡不是日誌
        }
    }

    internal static IReadOnlyList<string> SnapshotVisited()
    {
        lock (NavLock) return VisitedPages.ToArray();
    }

    internal static void ResetVisitedForTests()
    {
        lock (NavLock) VisitedPages.Clear();
    }

    // ── 收集入口（非衍生項）──────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        RegistryProbe? registry = null,
        CommandRunner? wakeRunner = null,
        PoolProbe? pool = null,
        GcProbe? gc = null,
        IReadOnlyList<string>? visitedPages = null,
        Func<string?>? symbolEnv = null)
    {
        var facts = new List<HardwareFact>(8)
        {
            UpdateIntegrityFact(at),
            NavHistoryFact(at, visitedPages ?? SnapshotVisited()),
            SymbolPathFact(at, (symbolEnv ?? ReadSymbolEnv)()),
        };
        facts.AddRange(ThreadPoolFacts(at, pool ?? ReadPool));
        facts.AddRange(GcFacts(at, gc ?? ReadGc));
        facts.Add(UpdateChannelFact(at, registry ?? ReadRegistry));
        facts.Add(WakeFact(at, wakeRunner ?? RunPowercfg));
        facts.Add(PrivacyBoundaryFact(at));
        return facts;
    }

    /// <summary>
    /// 需要「前面各組事實」當輸入的三項（IN-005／UF-010 由尾端呼叫，NG-004 同位置）。
    /// 輸入刻意不含自己——彙總不把自己算進分母。
    /// </summary>
    public static IReadOnlyList<HardwareFact> CollectDerived(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
        => [ElevationFact(at, facts), UpdateDashboardFact(at, facts)];

    // ── IN-005 提權最小化審計 ────────────────────────────────────────────

    private static HardwareFact ElevationFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        var privs = TokenPrivilegeReader.Read();
        var denied = facts.Where(f => f.Availability == FactAvailability.InsufficientPrivilege)
            .Select(f => f.Key).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();

        if (privs is null)
            return new HardwareFact(ElevationKey, CatSelf, "提權最小化審計",
                "", "", "GetTokenInformation", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "讀不到本行程 token——不假裝「沒要權限」也不假裝「已提權」");

        string state = privs.Elevated ? "已提權（完整權限令牌）" : "未提權（預設標準權限）";
        string body = denied.Count == 0
            ? $"本次收集沒有任何來源因權限不足而不可得——{state}・完整性 {privs.IntegrityLevel}。"
            : $"本次有 {denied.Count} 把鍵因權限不足不可得：{string.Join("、", denied.Take(8))}"
              + (denied.Count > 8 ? $"…等 {denied.Count - 8} 項" : "") +
              $"——{state}・完整性 {privs.IntegrityLevel}。";
        return new HardwareFact(ElevationKey, CatSelf, "提權最小化審計",
            body + " 界線：本工具只在工作需要時才要求系統管理員，不自行提權、不請求 UAC、不假裝讀到了。",
            "把鍵", "GetTokenInformation ＋ 本輪事實的可用性", FactTrustLevel.Derived, false, at, denied.Count);
    }

    // ── IN-006 自我更新完整性 ────────────────────────────────────────────

    private static HardwareFact UpdateIntegrityFact(DateTimeOffset at)
        => new(UpdateSigKey, CatSelf, "自我更新完整性",
            "", "", "本專案的更新模型（零網路原則）", FactTrustLevel.Unknown, false, at, null,
            FactAvailability.NotSupported,
            "本版沒有自我更新通道，也沒有可驗的更新套件簽章——「沒有簽章可驗」不等於「驗證通過」。" +
            "現行的離線更新面只有知識表包（knowledge-pack.json）：完整性走 serialNumber 內容推導＋審計鏈，不是數位簽章；" +
            "主程式由使用者自行下載替換（XinSpectDeploy.exe 走 HTTPS，但那是瀏覽器下載層的信任，不是本工具驗的簽章）");

    // ── NG-005 導覽歷史 ──────────────────────────────────────────────────

    private static HardwareFact NavHistoryFact(DateTimeOffset at, IReadOnlyList<string> visited)
        => visited.Count == 0
            ? new HardwareFact(HistoryKey, CatNav, "導覽歷史",
                "", "", "主視窗切頁（記憶體）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable, "本次執行還沒切過頁——軌跡只留在記憶體，刻意不落盤（L1 使用者行為資料）")
            : new HardwareFact(HistoryKey, CatNav, "導覽歷史",
                $"本次執行走過 {visited.Count} 站：{string.Join(" → ", visited.Take(12))}"
                + (visited.Count > 12 ? $"…等 {visited.Count - 12} 站" : "") +
                "。界線：只在記憶體，不寫檔、不上傳、不進快照（時間膠囊不帶使用者行為軌跡）。",
                "站", "主視窗切頁（記憶體）", FactTrustLevel.Reported, Sensitive: true, at, visited.Count);

    // ── DL-003 符號路徑審計 ──────────────────────────────────────────────

    // ── DL-008 線程池 ────────────────────────────────────────────────────

    private static IReadOnlyList<HardwareFact> ThreadPoolFacts(DateTimeOffset at, PoolProbe probe)
    {
        var snap = probe();
        if (snap is null)
            return
            [
                Na(at, PoolCountKey, CatDl, "線程池執行緒數", "ThreadPool 計數讀取失敗——讀不到就是不猜", FactAvailability.ReadError),
                Na(at, PoolSummaryKey, CatDl, "線程池狀態", "ThreadPool 計數讀取失敗——讀不到就是不猜", FactAvailability.ReadError),
            ];
        var s = snap.Value;
        return
        [
            new HardwareFact(PoolCountKey, CatDl, "線程池執行緒數",
                s.PoolThreads.ToString(CultureInfo.InvariantCulture), "條", "ThreadPool.GetAvailableThreads/ThreadCount",
                FactTrustLevel.Measured, false, at, s.PoolThreads),
            new HardwareFact(PoolSummaryKey, CatDl, "線程池狀態",
                $"執行緒 {s.PoolThreads} 條・可用 worker {s.WorkerAvail}／completion {s.IoAvail}・待處理 {s.Pending}・已完成 {s.Completed}" +
                "。界線：這是<b>本工具自己這個行程</b>的執行期狀態，不是被檢測電腦上其他程序的健康度。",
                "", "System.Threading.ThreadPool", FactTrustLevel.Measured, false, at, s.Pending),
        ];
    }

    private static (int, int, int, long, int)? ReadPool()
    {
        try
        {
            ThreadPool.GetAvailableThreads(out int worker, out int io);
            ThreadPool.GetMaxThreads(out int maxWorker, out _);
            return (ThreadPool.ThreadCount, worker, io, ThreadPool.CompletedWorkItemCount, maxWorker - worker);
        }
        catch { return null; }
    }

    // ── DL-009 GC 模式 ───────────────────────────────────────────────────

    private static IReadOnlyList<HardwareFact> GcFacts(DateTimeOffset at, GcProbe probe)
    {
        var snap = probe();
        if (snap is null)
            return
            [
                Na(at, GcModeKey, CatDl, "GC 模式", "GC 設定讀取失敗——讀不到就是不猜", FactAvailability.ReadError),
                Na(at, GcSummaryKey, CatDl, "GC 回收次數與堆大小", "GC 設定讀取失敗——讀不到就是不猜", FactAvailability.ReadError),
            ];
        var s = snap.Value;
        return
        [
            new HardwareFact(GcModeKey, CatDl, "GC 模式",
                $"{(s.ServerMode ? "Server GC" : "Workstation GC")}・延遲模式 {s.LatencyMode}" +
                "。界線：GC 模式影響本工具的尾碼（長停顿與否），不是硬體事實；要改由 csproj/runtimeconfig 決定。",
                "", "System.Runtime.GCSettings", FactTrustLevel.Measured, false, at),
            new HardwareFact(GcSummaryKey, CatDl, "GC 回收次數與堆大小",
                $"Gen0 {s.Gen0}／Gen1 {s.Gen1}／Gen2 {s.Gen2} 次・活著堆 {s.HeapBytes / 1048576.0:N1} MiB" +
                "（自啟動累計，不是洩漏判決）。",
                "次", "GC.CollectionCount ＋ GC.GetTotalMemory", FactTrustLevel.Measured, false, at, s.Gen2),
        ];
    }

    private static (bool, string, int, int, int, long)? ReadGc()
    {
        try
        {
            return (System.Runtime.GCSettings.IsServerGC,
                System.Runtime.GCSettings.LatencyMode.ToString(),
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                GC.GetTotalMemory(false));
        }
        catch { return null; }
    }

    // ── UF-007 更新通道 ──────────────────────────────────────────────────

    private static HardwareFact UpdateChannelFact(DateTimeOffset at, RegistryProbe registry)
    {
        object? target = registry("TargetReleaseVersionInfo");
        object? enabled = registry("TargetReleaseVersion");
        object? display = registry("DisplayVersion");
        object? build = registry("CurrentBuildNumber");

        string here = display is string d && build is string b
            ? $"目前 {d}／build {b}"
            : "目前版本註記讀不到（DisplayVersion／CurrentBuildNumber）";
        string pin = enabled is int e && e == 1 && target is string t
            ? $"政策釘住目標版本 {t}（TargetReleaseVersion=1）——這台機器被釘在這個版本，不是它自己選的"
            : "政策未釘版本——功能更新跟隨機器的通道走（本工具讀不到 Insider 層級，也不猜）";
        return new HardwareFact(ChannelKey, CatUf, "更新通道",
            $"{here}・{pin}。界線：講「被導向哪個版本」，不講「該不該升」。",
            "", @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate（TargetReleaseVersion*）＋ CurrentVersion",
            FactTrustLevel.Reported, false, at);
    }

    private static HardwareFact UpdateDashboardFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        HardwareFact? Pick(string key) => facts.FirstOrDefault(f => f.Key == key);
        var source = Pick(SourceKeyOf);
        var channel = Pick(ChannelKey);
        var servicing = Pick(ServicingKeyOf);
        int known = new[] { source, channel, servicing }.Count(f => f is { Availability: FactAvailability.Present });
        string text = $"更新面 3 項（來源／通道／服務堆疊）已知 {known} 項：" +
            $"來源＝{Brief(source)}・通道＝{Brief(channel)}・SSU＝{Brief(servicing)}。" +
            "界線：儀表只把已有事實湊成一欄，不給「該不該更新」的建議。";
        return new HardwareFact(DashKey, CatUf, "更新健康儀表", text, "項",
            "uf.source／uf.channel／uf.servicing", FactTrustLevel.Derived, false, at, known,
            known == 0 ? FactAvailability.Unknown : FactAvailability.Present,
            known == 0 ? "更新面的三把鍵都不在場——沒有東西可以湊，不是「更新正常」" : null);
    }

    private const string SourceKeyOf = "uf.source";
    private const string ServicingKeyOf = "uf.servicing";

    private static string Brief(HardwareFact? f) => f switch
    {
        null => "尚未讀取",
        { Availability: FactAvailability.Present } p => FirstClause(p.Value),
        { Availability: FactAvailability.NotApplicable } na => "不適用：" + Short(na.UnavailableReason ?? ""),
        { Availability: FactAvailability.NotSupported } ns => "不支援：" + Short(ns.UnavailableReason ?? ""),
        { Availability: FactAvailability.InsufficientPrivilege } ip => "缺權限：" + Short(ip.UnavailableReason ?? ""),
        { Availability: FactAvailability.ReadError } err => "讀取失敗：" + Short(err.UnavailableReason ?? ""),
        _ => "未確認",
    };

    private static string FirstClause(string s)
    {
        int cut = s.IndexOf('。');
        string head = cut > 0 ? s[..cut] : s;
        return head.Length > 40 ? head[..40] + "…" : head;
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : (s.Length == 0 ? "（未附原因）" : s);

    // ── HM-004 裝置喚醒記錄 ──────────────────────────────────────────────

    private static HardwareFact WakeFact(DateTimeOffset at, CommandRunner runner)
    {
        string[] args = ["/devicequery", "wake_armed"];
        string? output = runner(args);
        if (output is null)
            return Na(at, WakeKey, CatDev, "裝置喚醒記錄",
                "powercfg.exe /devicequery wake_armed 無法執行（找不到命令或逾時）——讀不到就是不猜",
                FactAvailability.ReadError);

        string trimmed = output.Trim();
        if (trimmed.Length == 0)
            trimmed = "（沒有輸出）";
        int lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return new HardwareFact(WakeKey, CatDev, "裝置喚醒記錄",
            $"powercfg /devicequery wake_armed 原樣輸出（{lines} 行）：" +
            (trimmed.Length > 400 ? trimmed[..400] + "…" : trimmed) +
            "。界線：文字随系統語言翻譯，本工具不解析在地化文字——命令照抄，使用者可自己再跑一次核對。",
            "行", "powercfg.exe /devicequery wake_armed（OS 內建命令）", FactTrustLevel.Reported, false, at, lines);
    }

    private static string? RunPowercfg(string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powercfg.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 不指定 Encoding：讓 .NET 用系統字碼頁（Big5／GBK），強制 UTF-8 在中文 Windows 會變亂碼
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            string stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { } return null; }
            return string.IsNullOrWhiteSpace(stdout) ? "" : stdout;
        }
        catch { return null; }
    }

    // ── FS-013 隱私禁項邊界 ──────────────────────────────────────────────

    private static HardwareFact PrivacyBoundaryFact(DateTimeOffset at)
        => new(PrivacyKey, CatDl, "隱私禁項邊界",
            $"明文不讀的來源共 {ForbiddenSourcePatterns.Count} 類：" + string.Join("、", ForbiddenSourcePatterns) +
            "。這些來源裝的是使用者開了什麼檔案、跑了什麼命令、看了什麼圖（Jump lists＝自動／自訂目的地、MRU＝最近使用、縮圖快取＝看過的圖、URL 快取＝下載過的網址、UserAssist＝GUI 執行計數），與硬體／系統事實無關——" +
            "禁令由 PrivacyDenylistTests 掃 Services/ 原始碼釘住，不是靠記性。",
            "類", "本專案的禁項清單（FS-013）", FactTrustLevel.Derived, false, at, ForbiddenSourcePatterns.Count);

    // ── DL-003 符號路徑 ──────────────────────────────────────────────────

    /// <summary>符號路徑由環境變數決定——測試一定要注入，否則結果取決於跑測試的機器。</summary>
    private static string? ReadSymbolEnv()
    {
        try { return Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"); }
        catch { return null; }
    }

    private static HardwareFact SymbolPathFact(DateTimeOffset at, string? env)
    {
        if (string.IsNullOrWhiteSpace(env))
            return new HardwareFact(SymbolPathKey, CatDl, "符號路徑",
                "", "", "環境變數 _NT_SYMBOL_PATH", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable,
                "沒有設定 _NT_SYMBOL_PATH——除錯堆疊符號不會自動載入。這是設定狀態的事實，不是缺少的東西");
        return new HardwareFact(SymbolPathKey, CatDl, "符號路徑",
            $"_NT_SYMBOL_PATH＝{env}（含使用者名稱時屬 L1：匯出預設遮蔽）", "", "環境變數 _NT_SYMBOL_PATH",
            FactTrustLevel.Reported, Sensitive: true, at);
    }

    private static object? ReadRegistry(string valueName) => valueName switch
    {
        "TargetReleaseVersion" => Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "TargetReleaseVersion", null),
        "TargetReleaseVersionInfo" => Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "TargetReleaseVersionInfo", null),
        "DisplayVersion" => Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null),
        "CurrentBuildNumber" => Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", null),
        _ => null,
    };

    private static HardwareFact Na(DateTimeOffset at, string key, string category, string name,
        string reason, FactAvailability availability)
        => new(key, category, name, "", "", "執行環境與治理（唯讀）", FactTrustLevel.Unknown, false, at,
            null, availability, reason);
}