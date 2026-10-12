using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 自動化與情境指引（Vol2 推進第 133–142 項）：AG-001 headless、AG-002 YAML 檢查組合、
/// AG-003 退出碼語意、AG-004 stdin 批次、AG-006 輸出輪轉、AG-007 遠端查詢（明文不做）、
/// SG-001 開機慢引導、SG-003 記憶體佔用摘要、MU-002 HEVC/AV1 硬解、XT-007 Sysmon 格式對照。
/// </summary>
/// <remarks>
/// <para><b>零網路</b>：AG-007 只是把「不做遠端查詢」做成可稽核事實；所有產生器與解析器都在本機完成。</para>
/// <para><b>YAML 是子集</b>：只支援檢查清單的 <c>checks:</c>／<c>exclude:</c> 與 <c>- id:</c> 行；
/// 其他 YAML 語法會進 errors，不猜。</para>
/// </remarks>
public static class AutomationAndGuidanceFactsService
{
    private const string CatAg = "代理與自動化";
    private const string CatSg = "情境引導";
    private const string CatMu = "多媒體與編解碼";
    private const string CatXt = "擴展介面";

    public const string HeadlessKey = "ag.headless";
    public const string ComposeKey = "ag.compose";
    public const string ExitCodeKey = "ag.exitcode";
    public const string StdinKey = "ag.stdin";
    public const string RotateKey = "ag.rotate";
    public const string RemoteKey = "ag.remote";
    public const string BootKey = "sg.boot.summary";
    public const string MemoryKey = "sg.mem.summary";
    public const string HardwareDecodeKey = "mu.hwdec.summary";
    public const string SysmonMapKey = "xt.sysmon.map";

    public delegate IReadOnlyList<(string Adapter, IReadOnlyList<string> Profiles)>? HwDecodeProbe();
    public delegate IReadOnlyList<ProcessMemorySample>? ProcessProbe();
    public delegate uint? BootDurationProbe();
    public delegate int? StartupItemProbe();

    public sealed record ProcessMemorySample(string Name, double WorkingSetMebibytes);

        private static readonly JsonSerializerOptions ReadableJsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        Feature(at, HeadlessKey, CatAg, "CLI headless 模式",
            "可輸出本機 CLI 模式目錄（JSON／compare／verify／baseline／SBOM／disk／errors／stdin）；零網路、不啟動 WPF。界線：這是本程式自己的命令面，不是遠端 API。",
            "CliService.GenerateHeadlessModesJson"),
        Feature(at, ComposeKey, CatAg, "YAML 檢查組合",
            "可合併多份檢查清單 YAML 子集，依 id 去重、排除 exclude，并把不支援語法列入 errors；零執行、不寫設定。界線：只驗證清單組合，不宣稱能跑檢查。",
            "AutomationAndGuidanceFactsService.ComposeChecklists"),
        Feature(at, ExitCodeKey, CatAg, "CLI 退出碼對照",
            "可匯出 0/1/2 退出碼語意：0 全 Present，1 致命錯誤，2 部分讀不到或未完成；自動化管線不必解析人話。界線：退出碼不是安全判決。",
            "AutomationAndGuidanceFactsService.BuildExitCodeMapJson"),
        Feature(at, StdinKey, CatAg, "stdin 批次查詢",
            $"CLI <c>--stdin-batch</c> 從 stdin 每行讀一個 key 前綴，逐查詢輸出 JSON；空輸入回退出碼 2。界線：只查已收集事實，不啟動新驅動、不讀網路。",
            "CliService.RunStdinBatch"),
        Feature(at, RotateKey, CatAg, "排程輸出輪轉",
            "可把呼叫端指定的本機輸出檔輪轉為 .1～.N，並刪除最舊一份；不隱式建立目錄、不碰未指定路徑。界線：這是檔案搬移，不保證内容完整性。",
            "AutomationAndGuidanceFactsService.RotateOutput"),
        new(RemoteKey, CatAg, "遠端查詢（明文不做）", "", "", "零網路政策",
            FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
            "遠端查詢違反零網路紅線；本工具不會监听端口、不自動上報或接受外部查詢"),
        BootFact(at), MemoryFact(at), HardwareDecodeFact(at), SysmonMapFact(at),
    ];

    // ── AG-001／AG-003 CLI 目錄與退出碼 ─────────────────────────────────────

    [SpecRef("本程式 CLI 命令面約定（非外部規格）：模式目錄直接讀 CliService 常數，避免文件與實作漂移。")]
    public static string GenerateHeadlessModesJson() => CliService.GenerateHeadlessModesJson();

    [SpecRef("本程式 CLI 退出碼約定（非外部規格）：0 成功且全部 Present；1 致命；2 部分讀不到或未完成。")]
    public static string BuildExitCodeMapJson()
    {
        var payload = new[]
        {
            new { code = CliService.ExitOk, meaning = "成功", detail = "收集／查詢完成，而且所有輸出事實都是 Present。" },
            new { code = CliService.ExitError, meaning = "致命錯誤", detail = "參數錯誤、收集拋例外、寫檔失敗等；自動化應視為管線失敗。" },
            new { code = CliService.ExitPartial, meaning = "部分完成", detail = "收集或查詢完成，但至少一筆非 Present／沒有命中；三態細節在輸出裡。" },
        };
        return JsonSerializer.Serialize(payload, ReadableJsonOptions);
    }

    // ── AG-002 YAML checks compose ──────────────────────────────────────────

    [SpecRef("XinSpect checklist YAML 子集：只支援 checks:/exclude: 序列下的 - id: <lowercase key> 行；未知語法如實進 errors。")]
    public static string ComposeChecklists(IEnumerable<string> yamlDocuments)
    {
        var checks = new List<string>();
        var excluded = new List<string>();
        var errors = new List<string>();
        string section = "";
        foreach (string doc in yamlDocuments ?? [])
        {
            if (string.IsNullOrWhiteSpace(doc)) continue;
            foreach (string raw in doc.Replace("\r\n", "\n", StringComparison.Ordinal)
                                   .Replace('\r', '\n').Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
                string trimmed = line.Trim();
                if (trimmed == "checks:") { section = "checks"; continue; }
                if (trimmed == "exclude:" || trimmed == "excludes:") { section = "exclude"; continue; }
                if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    if (!char.IsWhiteSpace(line[0])) errors.Add("只支援 checks:/exclude: 與 - id: 子集：" + trimmed);
                    continue;
                }
                string item = trimmed[2..].Trim();
                int colon = item.IndexOf(':', StringComparison.Ordinal);
                if (colon < 0 || !item[..colon].Trim().Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("項目必須是 '- id: <key>'：" + trimmed);
                    continue;
                }
                string id = item[(colon + 1)..].Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-z0-9](?:[a-z0-9._-]{0,159})$"))
                {
                    errors.Add("非法 id：" + id);
                    continue;
                }
                if (section == "checks") checks.Add(id);
                else if (section == "exclude") excluded.Add(id);
                else errors.Add("- id 沒有位於 checks: 或 exclude: 段落：" + id);
            }
        }
        var excludeSet = new HashSet<string>(excluded, StringComparer.Ordinal);
        var duplicateSet = checks.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var included = checks.Distinct(StringComparer.Ordinal).Where(x => !excludeSet.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return JsonSerializer.Serialize(new
        {
            included,
            excluded = excluded.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            duplicates = duplicateSet.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            errors,
            boundary = "只組合 id 清單；不執行檢查、不寫設定、零網路。",
        }, ReadableJsonOptions);
    }

    // ── AG-004 stdin batch wrapper ──────────────────────────────────────────

    public static int RunStdinBatch(Func<IReadOnlyList<HardwareFact>> collect, TextReader? stdin,
        TextWriter stdout, TextWriter stderr) => CliService.RunStdinBatch(collect, stdin, stdout, stderr);

    // ── AG-006 output rotation ──────────────────────────────────────────────

    public static bool RotateOutput(string outputPath, int keepBackups)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("輪轉目標不可空白。", nameof(outputPath));
        if (keepBackups < 1) throw new ArgumentOutOfRangeException(nameof(keepBackups), "keepBackups 至少為 1");
        string full = Path.GetFullPath(outputPath);
        if (!File.Exists(full)) return false;
        for (int i = keepBackups; i >= 1; i--)
        {
            string oldBackup = Suffix(full, i);
            if (!File.Exists(oldBackup)) continue;
            if (i == keepBackups) File.Delete(oldBackup);
            else File.Move(oldBackup, Suffix(full, i + 1), overwrite: true);
        }

        File.Move(full, Suffix(full, 1), overwrite: true);
        return true;
    }

    private static string Suffix(string path, int index) => $"{path}.{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    // ── SG-001 boot guidance ─────────────────────────────────────────────────

    public static string BuildBootGuidance(uint? bootMs, int? startupItems)
        => $"開機事件回報 {((bootMs ?? 0) / 1000.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} 秒"
         + (startupItems is { } n ? $"；啟動登錄項約 {n} 筆" : "；啟動項讀不到（不假裝 0）")
         + "。界線：這是導航不是診斷，快慢由使用者與服務明細判斷。";

    public static HardwareFact BootFact(DateTimeOffset at, BootDurationProbe? boot = null, StartupItemProbe? startup = null)
    {
        uint? ms = null;
        bool bootError = false;
        try { ms = (boot ?? BootTimingFactsService.FetchLastBootDuration)(); }
        catch { bootError = true; }
        int? items = null;
        bool startupError = false;
        try { items = (startup ?? (() => CountRunStartupItems()))(); }
        catch { startupError = true; }

        if (ms is null && items is null)
            return new HardwareFact(BootKey, CatSg, "開機為什麼慢（引導）", "", "",
                "Diagnostics-Performance Event 100＋Run 啟動登錄計數", FactTrustLevel.Unknown, false, at, null,
                bootError || startupError ? FactAvailability.ReadError : FactAvailability.NotSupported,
                bootError || startupError ? "開機事件或啟動登錄讀取失敗——讀不到就是不猜" : "尚無完整開機事件，且啟動登錄不可讀");

        string value = BuildBootGuidance(ms, items);
        return new HardwareFact(BootKey, CatSg, "開機為什麼慢（引導）", value, "秒",
            "Diagnostics-Performance Event 100＋Run 啟動登錄計數", FactTrustLevel.Reported, false, at,
            ms is null ? null : ms.Value / 1000.0);
    }

    private static int CountRunStartupItems()
    {
        int count = 0;
        string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using (var hkcu = Registry.CurrentUser.OpenSubKey(run)) count += hkcu?.ValueCount ?? 0;
        using (var hklm = Registry.LocalMachine.OpenSubKey(run)) count += hklm?.ValueCount ?? 0;
        using (var hklm32 = Registry.LocalMachine.OpenSubKey(run + "32")) count += hklm32?.ValueCount ?? 0;
        return count;
    }

    // ── SG-003 memory guidance ───────────────────────────────────────────────

    [SpecRef("Windows Process.WorkingSet64：目前處理序的可分頁工作集位元組數；本摘要除以 1024×1024 轉 MiB。")]
    public static string SummarizeProcessMemory(IEnumerable<ProcessMemorySample> samples, int top = 5)
    {
        var list = (samples ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.WorkingSetMebibytes >= 0 && double.IsFinite(x.WorkingSetMebibytes))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Mib: g.Sum(x => x.WorkingSetMebibytes)))
            .OrderByDescending(x => x.Mib).Take(Math.Max(1, top)).ToList();
        if (list.Count == 0) return "程序記憶體摘要沒有可列舉樣本（0 是值，不是沒跑）";
        return string.Join("、", list.Select(x => $"{x.Name} {x.Mib.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} MiB"));
    }

    public static HardwareFact MemoryFact(DateTimeOffset at, ProcessProbe? probe = null)
    {
        IReadOnlyList<ProcessMemorySample>? samples;
        try { samples = (probe ?? new ProcessProbe(CollectProcessSamples))(); }
        catch { samples = null; }
        if (samples is null)
            return new HardwareFact(MemoryKey, CatSg, "誰在佔記憶體（聚合）", "", "", "Process.WorkingSet64",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, "程序工作集列舉失敗——不以 0 冒充");
        string value = SummarizeProcessMemory(samples);
        return new HardwareFact(MemoryKey, CatSg, "誰在佔記憶體（聚合）", value, "MiB", "Process.WorkingSet64",
            FactTrustLevel.Reported, false, at, samples.Count,
            samples.Count == 0 ? FactAvailability.NotApplicable : FactAvailability.Present,
            samples.Count == 0 ? "沒有可列舉的程序樣本" : null);
    }

    private static IReadOnlyList<ProcessMemorySample> CollectProcessSamples()
    {
        var result = new List<ProcessMemorySample>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                result.Add(new ProcessMemorySample(p.ProcessName, p.WorkingSet64 / (1024.0 * 1024.0)));
            }
            catch { /* access denied：跳過單一程序，不把整輪改成 0 */ }
            finally { p.Dispose(); }
        }
        return result;
    }

    // ── MU-002 hardware decode ───────────────────────────────────────────────

    [SpecRef("D3D11 video decoder profile GUIDs（Windows SDK d3d11.h）；本摘要依 GpuCodecService.Profile 名稱辨識 HEVC/AV1，不跑影片、不量速度。")]
    public static string BuildHardwareDecodeSummary(IEnumerable<(string Adapter, IEnumerable<string> Profiles)> adapters)
    {
        int hevc = 0, av1 = 0;
        foreach (var a in adapters ?? [])
            foreach (var profile in a.Profiles ?? [])
            {
                if (profile.Contains("HEVC", StringComparison.OrdinalIgnoreCase)) hevc++;
                if (profile.Contains("AV1", StringComparison.OrdinalIgnoreCase)) av1++;
            }
        return $"偵測到 HEVC 解碼 profile {hevc} 筆、AV1 解碼 profile {av1} 筆。界線：能力宣告不等於實測播放速度。";
    }

    public static HardwareFact HardwareDecodeFact(DateTimeOffset at, HwDecodeProbe? probe = null)
    {
        IReadOnlyList<(string Adapter, IReadOnlyList<string> Profiles)>? list;
        try { list = (probe ?? new HwDecodeProbe(CollectHardwareDecodeProfiles))(); }
        catch { list = null; }
        if (list is null)
            return new HardwareFact(HardwareDecodeKey, CatMu, "HEVC／AV1 硬解", "", "", "D3D11 VideoDevice＋MFTEnumEx",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, "GPU 編解碼能力列舉失敗——不猜");
        var value = BuildHardwareDecodeSummary(list.Select(x => (x.Adapter, (IEnumerable<string>)x.Profiles)));
        return new HardwareFact(HardwareDecodeKey, CatMu, "HEVC／AV1 硬解", value, "筆", "D3D11 VideoDevice＋MFTEnumEx",
            FactTrustLevel.Reported, false, at, list.Count);
    }

    private static IReadOnlyList<(string, IReadOnlyList<string>)> CollectHardwareDecodeProfiles()
    {
        var result = GpuCodecService.Probe();
        return result.Adapters.Where(a => a.Error is null).Select(a => (a.Name, a.DecoderProfiles)).ToList();
    }

    // ── XT-007 Sysmon mapping ────────────────────────────────────────────────

    [SpecRef("Microsoft Sysmon 事件 ID 對照（Sysmon schema：1=Process Create, 3=Network, 11=FileCreate, 12/13/14=Registry, 22=DNS, 23=FileDelete；本工具不對本機執行 Sysmon 或讀其記錄）。")]
    public static string BuildSysmonEventMap()
    {
        var payload = new[]
        {
            new { sysmonEventId = 1, name = "Process Create", localCounterpart = "程序建立事件／Process 列舉", boundary = "本工具不做事件攔截" },
            new { sysmonEventId = 3, name = "Network connection", localCounterpart = "既有 listen port／net adapter 事實", boundary = "零網路被動觀察不等於連線歷史" },
            new { sysmonEventId = 11, name = "File Create", localCounterpart = "檔案時間戳／快照 diff", boundary = "不讀跳過清單（MRU／縮圖／Jump lists）" },
            new { sysmonEventId = 12, name = "Registry object add/delete", localCounterpart = "登錄讀取事實", boundary = "唯讀，不寫登錄" },
            new { sysmonEventId = 13, name = "Registry value set", localCounterpart = "登錄值比對", boundary = "不讀產品金鑰" },
            new { sysmonEventId = 22, name = "DNS query", localCounterpart = "未實作", boundary = "不做網路流量擷取" },
            new { sysmonEventId = 23, name = "File delete", localCounterpart = "未實作", boundary = "不讀回收桶／跳過清單" },
        };
        return JsonSerializer.Serialize(payload, ReadableJsonOptions);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static HardwareFact Feature(DateTimeOffset at, string key, string category, string name, string value, string source)
        => new(key, category, name, value, "", source, FactTrustLevel.Derived, false, at);

    private static HardwareFact SysmonMapFact(DateTimeOffset at)
        => Feature(at, SysmonMapKey, CatXt, "Sysmon 格式對照",
            "可匯出 Sysmon Event ID 與本地對應能力的對照表；未實作項如實標示。界線：這是格式對照，不宣稱本工具已安裝或讀取 Sysmon 記錄。",
            "AutomationAndGuidanceFactsService.BuildSysmonEventMap");
}
