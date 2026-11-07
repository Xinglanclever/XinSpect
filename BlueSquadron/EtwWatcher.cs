// ⚠ 未編譯——需作者機驗證（dotnet build 命令見 patch-23.md）
//
// EtwWatcher.cs — 完整替換檔（patch-23，P0-13 真功能：BYOVD 全量清單）
// 覆蓋目標：BlueSquadron/EtwWatcher.cs（原 155 行）
// 配套：patch-23-BlueSquadronBridge.csproj（源碼 Link 引入 ByovdBlocklistDecoder）、
//       patch-24-Program.cs（ApplyBlocklistPath 接線與 status/init 申報）。
//
// 修復內容（對照原檔）：
//   原 L19-32 只有 5 條硬編碼 SHA256（LOLDrivers 精選），卻在註解宣稱「Phase 2 從檔案
//   載入完整清單」——宣稱與代碼脫節。本版：
//   1. 經 ByovdBlocklistDecoder 載入微軟「建議的驅動程式封鎖規則」全量清單（300+ 規則），
//      建三個雜湊集合（檔名／SHA-256／SHA-1），比對新載入核心驅動。
//   2. 清單三態如實申報：Full（全量）／DegradedMissing（缺檔，退回 5 條硬編碼＋標
//      「降級模式」）／DegradedParseFail（解析失敗或零規則，同前）。三態都經
//      Policy.RecordThreat 進威脅時間軸，BlocklistState 供 status 指令與 Bridge 狀態列。
//   3. 檔名規則比對（Full 模式）；NT 裝置路徑（\SystemRoot\、\??\）先轉 Win32 路徑
//      再計雜湊——原版直接 File.OpenRead 對核心映像路徑幾乎必敗，比對形同虛設。
//   4. SHA-256 與 SHA-1 同一趟串流計算（IncrementalHash），與 ByovdCompareService 口徑一致。
//   界線（如實申報）：ETW 層比對是「偵測」不是攔截；核心層的權威比對在驅動
//   Callbacks.c 的 BsHashWorker（清單經 IOCTL_BS_DRIVER_BLOCK 下發）。兩層互補。

using System.IO;
using System.Security.Cryptography;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using XinSpect;   // ByovdBlocklistDecoder（源碼 Link 自 ..\XinSpect.Decoders\，命名空間 XinSpect）

namespace BlueSquadron;

/// <summary>BYOVD 封鎖清單三態：Full＝全量生效；其餘兩態＝降級模式（5 條硬編碼後備）。</summary>
internal enum ByovdBlocklistState
{
    NotLoaded,          // 尚未嘗試（進程啟動到第一次 Apply 之間；對外不以此態申報）
    Full,               // 清單載入成功——全量規則生效
    DegradedMissing,    // 沒有清單檔——降級模式（缺檔不是錯誤，是「這道比對未啟用」）
    DegradedParseFail,  // 清單存在但讀取／解析失敗或零規則——降級模式（壞清單不解碼）
}

/// <summary>清單快照：狀態＋三個比對集合。OnImageLoad 取參考後比對，不在事件執行緒持鎖。</summary>
internal sealed record BlocklistSnapshot(
    ByovdBlocklistState State,
    string SourceDetail,                                    // 給人看的來源／失敗原因
    int NameRules, int Sha256Rules, int Sha1Rules,          // 全量模式的規則計數（降級時為 0）
    HashSet<string> FileNames,                              // 小寫檔名（Full 模式才非空）
    HashSet<string> Sha256,                                 // 小寫雜湊
    HashSet<string> Sha1);

/// <summary>
/// ETW 即時事件監控：訂閱核心事件偵測可疑活動。
/// Phase 1：驅動載入、進程建立。Phase 2 加入驅動回呼後會移除部分 ETW 偵測。
/// </summary>
internal sealed class EtwWatcher : IDisposable
{
    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile bool _running;
    private readonly PolicyEngine _policy;
    private readonly Action<string>? _alertPush;

    // ── 降級模式後備清單（原 5 條硬編碼保留，僅在降級態使用，且對外標示「降級」） ──
    private static readonly HashSet<string> FallbackSha256 = new(StringComparer.OrdinalIgnoreCase)
    {
        // TrueSight.sys (Adlice/truesight.sys) — BYOVD used by multiple threat actors
        "a7b2bbe5ac4900ee53e4c83b32f6b7e1cc8f1b4ff2f05b1eb0c9b8a4a1b0dcfb",
        // gdrv.sys — GIGABYTE vulnerable driver
        "31f4cfb4c71da44120752721103a16512444c13c2ac2d857a7e6f13cb679b427",
        // RTCore64.sys — MSI Afterburner vulnerable driver
        "01aa278b07b58dc46c84bd0b1b5c8e9ee4e62ea0bf7a695862444af32e87f1fd",
        // dbutil_2_3.sys — Dell BIOS Utility
        "0296e2ce999e67c76352613a718e11516fe1b0efc3ffdb8918fc999dd76a73a5",
        // mhyprot2.sys — miHoYo anti-cheat (abused in BYOVD)
        "0466e90bf0e83b776ca8716e01d35a8a2e5f96d3bd1fc246e8a6ad3cb77c39d3",
    };

    private static readonly BlocklistSnapshot InitialSnapshot =
        new(ByovdBlocklistState.NotLoaded, "尚未載入", 0, 0, 0,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private readonly object _listLock = new();
    private BlocklistSnapshot _blocklist = InitialSnapshot;

    /// <summary>目前清單狀態（status 指令／狀態列用；讀取為原子參考交換，無撕裂面）。</summary>
    public (ByovdBlocklistState State, int Rules, string Detail) BlocklistStatus
    {
        get
        {
            lock (_listLock)
            {
                var s = _blocklist;
                int rules = s.State == ByovdBlocklistState.Full
                    ? s.NameRules + s.Sha256Rules + s.Sha1Rules
                    : FallbackSha256.Count;
                return (s.State, rules, s.SourceDetail);
            }
        }
    }

    public EtwWatcher(PolicyEngine policy, Action<string>? alertPush = null)
    {
        _policy = policy;
        _alertPush = alertPush;
    }

    /// <summary>
    /// Bridge 自己找得到的候選路徑（standalone／開發模式；正式發佈時主程式會經
    /// init 的 blocklistPath 參數傳入它自己的 Rules\byovd-blocklist.xml）。
    /// 與主程式 ByovdCompareService.DefaultBlocklistPath() 同一組候選形狀。
    /// </summary>
    public static string? ResolveDefaultBlocklistPath()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "Rules", "byovd-blocklist.xml"),
                     Path.Combine(AppContext.BaseDirectory, "byovd-blocklist.xml"),
                 })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>
    /// 載入（或重載）封鎖清單並三態申報。path 為 null／缺檔→DegradedMissing；
    /// 讀取或解析失敗→DegradedParseFail；成功→Full。任何時點呼叫都安全（比對集合
    /// 以整份快照原子替換）。
    /// </summary>
    public void ApplyBlocklistPath(string? path)
    {
        BlocklistSnapshot snap;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            snap = new BlocklistSnapshot(
                ByovdBlocklistState.DegradedMissing,
                path is null ? "未提供清單路徑" : $"清單檔不存在：{path}",
                0, 0, 0,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                FallbackSha256,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            string xml;
            try { xml = File.ReadAllText(path); }
            catch (Exception ex)
            {
                snap = new BlocklistSnapshot(
                    ByovdBlocklistState.DegradedParseFail,
                    $"清單讀取失敗：{path}（{ex.Message}）",
                    0, 0, 0,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    FallbackSha256,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (_listLock) _blocklist = snap;
                ReportState(snap);
                return;
            }

            var rules = ByovdBlocklistDecoder.Parse(xml);
            if (rules.Count == 0)
            {
                snap = new BlocklistSnapshot(
                    ByovdBlocklistState.DegradedParseFail,
                    $"清單解析失敗或沒有 FileRule：{path}",
                    0, 0, 0,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    FallbackSha256,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sha256 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sha1 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in rules)
                {
                    if (r.FileName is { Length: > 0 } fn) names.Add(fn);
                    if (r.Sha256 is { Length: > 0 } h256) sha256.Add(h256);
                    if (r.Sha1 is { Length: > 0 } h1) sha1.Add(h1);
                }
                snap = new BlocklistSnapshot(
                    ByovdBlocklistState.Full,
                    path,
                    names.Count, sha256.Count, sha1.Count,
                    names, sha256, sha1);
            }
        }

        lock (_listLock) _blocklist = snap;
        ReportState(snap);
    }

    /// <summary>把清單狀態如實寫進威脅時間軸（advisory——是狀態申報，不是威脅事件）。</summary>
    private void ReportState(BlocklistSnapshot s)
    {
        (string title, string detail) = s.State switch
        {
            ByovdBlocklistState.Full =>
                ("BYOVD 封鎖清單：全量模式",
                 $"微軟建議驅動封鎖清單已載入：{s.NameRules + s.Sha256Rules + s.Sha1Rules} 條規則" +
                 $"（檔名 {s.NameRules}、SHA-256 {s.Sha256Rules}、SHA-1 {s.Sha1Rules}）。來源：{s.SourceDetail}。" +
                 "ETW 層比對為偵測（記錄＋警報），不是攔截；核心層比對以驅動內清單為權威。"),
            ByovdBlocklistState.DegradedMissing =>
                ("BYOVD 封鎖清單：降級模式（缺清單）",
                 $"微軟「建議的驅動程式封鎖規則」XML 未提供（{s.SourceDetail}）。" +
                 $"退回 {FallbackSha256.Count} 條硬編碼後備雜湊（TrueSight／gdrv／RTCore64／dbutil_2_3／mhyprot2）" +
                 "——涵蓋遠低於全量清單（微軟建議清單 300+ 條）。把 XML 放到程式目錄 Rules\\byovd-blocklist.xml 即升級為全量模式。"),
            ByovdBlocklistState.DegradedParseFail =>
                ("BYOVD 封鎖清單：降級模式（解析失敗）",
                 $"清單存在但解不出規則：{s.SourceDetail}——壞清單不解碼。" +
                 $"退回 {FallbackSha256.Count} 條硬編碼後備雜湊（降級模式）。"),
            _ => ("BYOVD 封鎖清單：尚未載入", "啟動序列尚未執行清單載入。"),
        };
        _policy.RecordThreat("advisory", "drivers", title, detail);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "EtwWatcher" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            _session = new TraceEventSession("BlueSquadronETW");
            // Microsoft-Windows-Kernel-Process for process create/terminate
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.ImageLoad |
                KernelTraceEventParser.Keywords.Process);

            _session.Source.Kernel.ImageLoad += OnImageLoad;
            _session.Source.Kernel.ProcessStart += OnProcessStart;

            _session.Source.Process();  // blocks until disposed
        }
        catch (UnauthorizedAccessException)
        {
            _policy.RecordThreat("advisory", "drivers",
                "ETW 監控需要系統管理員權限",
                "無法啟動 ETW 核心追蹤。驅動載入與進程監控功能不可用。");
        }
        catch (Exception ex)
        {
            _policy.RecordThreat("advisory", "drivers",
                "ETW 監控啟動失敗",
                $"例外：{ex.Message}");
        }
    }

    private void OnImageLoad(Microsoft.Diagnostics.Tracing.Parsers.Kernel.ImageLoadTraceData data)
    {
        if (!_policy.IsEnabled("drivers")) return;
        // Only care about kernel-mode loads (ProcessID == 0 or 4)
        if (data.ProcessID != 0 && data.ProcessID != 4) return;

        string path = data.FileName ?? "";
        if (string.IsNullOrEmpty(path)) return;

        // 取當下快照（清單熱替換時比對用的是「事件當下」那份，不持鎖）
        BlocklistSnapshot list;
        lock (_listLock) list = _blocklist;

        string? hitBasis = null;   // 命中依據（檔名規則／SHA-256／SHA-1／後備清單）
        string? hitRule = null;

        // 1) 檔名規則（僅全量模式有；降級後備清單沒有檔名規則可對）
        if (list.State == ByovdBlocklistState.Full &&
            Path.GetFileName(path) is { Length: > 0 } file &&
            list.FileNames.Contains(file))
        {
            hitBasis = "檔名規則";
            hitRule = file;
        }

        // 2) 雜湊比對：先把 NT 裝置路徑轉成 Win32 路徑再算——
        //    原版直接 File.OpenRead("\\SystemRoot\\system32\\drivers\\x.sys") 幾乎必敗，
        //    雜湊比對等於沒有。轉換失敗或檔案讀不到 → 雜湊比對如實缺席（不做假比對）。
        if (hitBasis is null)
        {
            string? win32 = ToWin32Path(path);
            if (win32 is not null)
            {
                var (sha256, sha1) = ComputeHashes(win32);
                if (sha256 is not null && list.Sha256.Contains(sha256))
                {
                    hitBasis = "SHA-256";
                    hitRule = sha256;
                }
                else if (sha1 is not null && list.Sha1.Contains(sha1))
                {
                    hitBasis = "SHA-1";
                    hitRule = sha1;
                }
            }
        }

        if (hitBasis is not null)
        {
            string mode = list.State == ByovdBlocklistState.Full ? "全量" : "降級";
            string msg = $"偵測到封鎖清單內的核心驅動載入：{Path.GetFileName(path)}" +
                         $"（依{hitBasis}：{hitRule}；清單模式：{mode}）。" +
                         "此驅動在微軟建議封鎖清單上——攻擊面事實，不是中毒判決；ETW 層只偵測不攔截。";
            _policy.RecordThreat("critical", "drivers",
                "BYOVD 漏洞驅動載入", msg);
            _alertPush?.Invoke(System.Text.Json.JsonSerializer.Serialize(new
            {
                alert = true,
                severity = "critical",
                line = "drivers",
                title = "偵測到已知漏洞驅動載入",
                detail = msg,
                ts = DateTimeOffset.UtcNow,
            }));
        }

        // Log all kernel driver loads as advisory for timeline
        _policy.RecordThreat("advisory", "drivers",
            "核心驅動載入",
            $"{Path.GetFileName(path)} (PID={data.ProcessID})");
    }

    /// <summary>
    /// NT 內核路徑 → Win32 路徑。ETW 核心映像事件給的是 \SystemRoot\... 或 \??\... 型路徑，
    /// usermode File API 打不開。認得的兩種前綴轉換，其他型態原樣交給 File API（失敗即缺席）。
    /// </summary>
    private static string? ToWin32Path(string ntPath)
    {
        const string sysRoot = @"\SystemRoot\";
        if (ntPath.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase))
        {
            string? windir = Environment.GetEnvironmentVariable("SystemRoot");
            return windir is { Length: > 0 } ? windir + ntPath.Substring(sysRoot.Length - 1) : null;
        }
        const string ntPrefix = @"\??\";
        if (ntPath.StartsWith(ntPrefix, StringComparison.Ordinal))
            return ntPath.Length > ntPrefix.Length ? ntPath[ntPrefix.Length..] : null;
        return ntPath;
    }

    /// <summary>同一趟串流同時算 SHA-256 與 SHA-1（小寫 hex，與 ByovdBlocklistDecoder 正規化口徑一致）。讀不到回 (null, null)。</summary>
    private static (string? Sha256, string? Sha1) ComputeHashes(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var buf = new byte[65536];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                sha256.AppendData(buf, 0, n);
                sha1.AppendData(buf, 0, n);
            }
            return (Convert.ToHexStringLower(sha256.GetHashAndReset()),
                    Convert.ToHexStringLower(sha1.GetHashAndReset()));
        }
        catch { return (null, null); }
    }

    private void OnProcessStart(Microsoft.Diagnostics.Tracing.Parsers.Kernel.ProcessTraceData data)
    {
        if (!_policy.IsEnabled("cpu")) return;
        // Watch for suspicious processes that might indicate an active attack
        string name = data.ImageFileName ?? "";
        string[] suspicious = ["mimikatz", "rubeus", "sharphound", "bloodhound",
                               "procdump", "nanodump", "ppldump", "lsassy"];
        foreach (var s in suspicious)
        {
            if (name.Contains(s, StringComparison.OrdinalIgnoreCase))
            {
                string msg = $"偵測到可疑進程：{name} (PID={data.ProcessID})";
                _policy.RecordThreat("critical", "cpu",
                    "可疑進程建立", msg);
                _alertPush?.Invoke(System.Text.Json.JsonSerializer.Serialize(new
                {
                    alert = true,
                    severity = "critical",
                    line = "cpu",
                    title = "偵測到可疑進程建立",
                    detail = msg,
                    ts = DateTimeOffset.UtcNow,
                }));
                break;
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _session?.Dispose();
        _session = null;
    }
}
