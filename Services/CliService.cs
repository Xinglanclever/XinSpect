using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>
/// CLI 模式（V7 WP32／A42）：帶引數啟動時不走 WPF，headless 收集事實後以 JSON 輸出。
/// <b>退出碼語意</b>：0＝全部 Present；2＝收集完成但部分事實讀不到（三態如實反映在輸出裡）；
/// 1＝致命錯誤（參數錯、收集拋例外）。自動化管線靠退出碼分岔，不必解析人話。
/// 誠實界線：本模式輸出驅動相依證據組（韌體安全稽核範圍）＋免管理員的 usermode 組
/// （儲存可靠性計數器）；其餘全機快照（SMBIOS／GPU／Windows Update 歷史等）依賴 WPF 服務層，
/// 未納入——幫助文字如實標明，不假裝全機都掃了。
/// </summary>
public static class CliService
{
    /// <summary>U10-PF-0017 追蹤鍵：PowerShell 模組逐域 cmdlet 的缺口申報——
    /// XinSpect.psm1 的 11 個逐域 cmdlet 都包裝本類的 headless 呼叫；追蹤鍵留在此處
    /// 讓鍵目錄掃描器看得到（模組本身的守門在 Tests/PowerShellModuleTests）。</summary>
    public const string PsModulesGapKey = "ps.modules.gap";

    public const string JsonArg = "--json";
    public const string EvidenceScope = "evidence";
    public const string QueryArg = "--query";
    public const string OutArg = "--out";
    public const string HelpArg = "--help";
    public const string CompareArg = "--compare-flash";
    public const string VerifyAuditArg = "--verify-audit";
    public const string IntegrityBaselineArg = "--integrity-baseline";
    public const string SbomArg = "--sbom";
    public const string DiskUsageArg = "--disk-usage";
    public const string ErrorsArg = "--errors";
    public const string DeepArg = "--deep";
    public const string StdinBatchArg = "--stdin-batch";

    public const int ExitOk = 0;
    public const int ExitPartial = 2;
    public const int ExitError = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions StdinJsonOptions = new(JsonOptions) { WriteIndented = false };

    public static int Run(string[] args, Func<IReadOnlyList<HardwareFact>> collect, TextWriter stdout, TextWriter stderr,
        Func<byte[], HardwareFact>? compare = null, TextReader? stdin = null)
    {
        if (args.Length == 0 || args.Contains(HelpArg))
        {
            PrintHelp(stdout);
            return ExitOk;
        }
        if (args[0] == CompareArg)
            return RunCompare(args, compare, stdout, stderr);
        if (args[0] == VerifyAuditArg)
            return RunVerifyAudit(args, stdout, stderr);
        if (args[0] == IntegrityBaselineArg)
            return RunIntegrityBaseline(args, stdout, stderr);
        if (args[0] == SbomArg)
            return RunSbom(args, stdout, stderr);
        if (args[0] == DiskUsageArg)
            return RunDiskUsage(args, stdout, stderr);
        if (args[0] == ErrorsArg)
            return RunErrors(args, collect, stdout, stderr);
        if (args[0] == StdinBatchArg)
            return RunStdinBatch(collect, stdin, stdout, stderr);
        if (args[0] != JsonArg)
        {
            stderr.WriteLine($"未知引數「{args[0]}」。用 --help 看用法。");
            return ExitError;
        }
        if (args.Length < 2 || args[1] != EvidenceScope)
        {
            stderr.WriteLine($"--json 之後要接範圍，目前唯一支援：「{EvidenceScope}」。");
            return ExitError;
        }
        string? query = OptionValue(args, QueryArg);
        string? outPath = OptionValue(args, OutArg);

        WriteGate.Reset();
        IReadOnlyList<HardwareFact> facts;
        try
        {
            facts = collect();
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"收集失敗：{ex.GetType().Name}：{ex.Message}");
            return ExitError;
        }

        if (query is not null)
            facts = facts.Where(f => f.Key.StartsWith(query, StringComparison.Ordinal)).ToList();

        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            scope = EvidenceScope,
            count = facts.Count,
            allPresent = facts.All(f => f.Availability == FactAvailability.Present),
            writeAudit = WriteGate.DescribeSession(),   // 本次執行寫了哪些暫存器／埠——可回答，不是黑箱
            facts = facts.Select(FactJson),
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);

        try
        {
            if (outPath is not null)
                File.WriteAllText(outPath, json);
            else
                stdout.WriteLine(json);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"寫出失敗：{ex.Message}");
            return ExitError;
        }

        return facts.All(f => f.Availability == FactAvailability.Present) ? ExitOk : ExitPartial;
    }

    /// <summary>
    /// BIOS 區 vs 參考映像比對模式（WP4 第二層的 CLI 面）。
    /// 退出碼：0＝一致；2＝有差異或三態（比對沒能完成／大小不符——細節在輸出）；1＝致命（讀檔失敗等）。
    /// </summary>
    /// <summary>AG-001：headless 模式目錄；由常數組成，讓說明與實作不漂移。</summary>
    public static string GenerateHeadlessModesJson()
    {
        var payload = new[]
        {
            new { mode = JsonArg, scope = EvidenceScope, output = "JSON", description = "headless 事實收集；支援 --query 前綴與 --out 檔案，零網路。" },
            new { mode = CompareArg, scope = "", output = "JSON", description = "BIOS 區與參考映像逐塊比對；只讀本機映像。" },
            new { mode = VerifyAuditArg, scope = "", output = "JSON", description = "驗證本機審計日誌雜湊鏈。" },
            new { mode = IntegrityBaselineArg, scope = "", output = "JSON", description = "明示寫入自身完整性基線；需要本機寫入權限。" },
            new { mode = SbomArg, scope = "", output = "JSON", description = "產生離線 CycloneDX SBOM。" },
            new { mode = DiskUsageArg, scope = "", output = "JSON", description = "磁碟滿的唯讀排行；--deep 是明示深掃。" },
            new { mode = ErrorsArg, scope = "", output = "JSON", description = "聚合非 Present 三態與原因。" },
            new { mode = StdinBatchArg, scope = "", output = "JSONL", description = "stdin 每行一個 key 前綴；每行輸出一個查詢結果物件。" },
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// AG-004 stdin 批次模式：每行一個 key 前綴；每行輸出一個查詢結果。
    /// 誠實界線：只過濾一次 collect() 的既有事實，不對每個前綴重新開驅動或重跑掃描。
    /// </summary>
    public static int RunStdinBatch(Func<IReadOnlyList<HardwareFact>> collect, TextReader? stdin,
        TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(collect);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (stdin is null)
        {
            stderr.WriteLine("stdin 不可用；本模式必須從管線或重導輸入查詢。");
            return ExitError;
        }
        WriteGate.Reset();
        IReadOnlyList<HardwareFact> facts;
        try { facts = collect(); }
        catch (Exception ex)
        {
            stderr.WriteLine($"收集失敗：{ex.GetType().Name}：{ex.Message}");
            return ExitError;
        }
        bool any = false;
        bool allHits = true;
        try
        {
            while (stdin.ReadLine() is { } raw)
            {
                string query = raw.Trim();
                if (query.Length == 0) continue;
                var hit = facts.Where(f => f.Key.StartsWith(query, StringComparison.Ordinal)).ToList();
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    query,
                    count = hit.Count,
                    allPresent = hit.Count > 0 && hit.All(f => f.Availability == FactAvailability.Present),
                    facts = hit.Select(FactJson),
                }, StdinJsonOptions));
                any = true;
                allHits &= hit.Count > 0;
            }
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"stdin 讀取失敗：{ex.Message}");
            return ExitError;
        }
        return any && allHits ? ExitOk : ExitPartial;
    }

    private static int RunCompare(string[] args, Func<byte[], HardwareFact>? compare, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2)
        {
            stderr.WriteLine("--compare-flash 之後要接參考映像路徑。");
            return ExitError;
        }
        string? outPath = OptionValue(args, OutArg);

        byte[] reference;
        try
        {
            reference = File.ReadAllBytes(args[1]);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"讀取參考映像失敗：{ex.Message}");
            return ExitError;
        }

        HardwareFact fact;
        try
        {
            fact = (compare ?? EvidenceCollection.CompareFlashWithReference)(reference);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"比對失敗：{ex.GetType().Name}：{ex.Message}");
            return ExitError;
        }

        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            scope = "flashcompare",
            reference = new { Path = args[1], Bytes = reference.Length },
            fact = FactJson(fact),
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        try
        {
            if (outPath is not null)
                File.WriteAllText(outPath, json);
            else
                stdout.WriteLine(json);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"寫出失敗：{ex.Message}");
            return ExitError;
        }

        return fact.Availability == FactAvailability.Present && fact.NumericValue == 0 ? ExitOk : ExitPartial;
    }

    /// <summary>
    /// 審計日誌驗證模式：能產生審計日誌，也要能在 App 外驗證它沒被改——
    /// 雜湊鏈的價值只在「可被第三方驗證」時才成立。
    /// 退出碼：0＝鏈完整（含「檔案不存在＝還沒有審計事件」，輸出如實標 fileExists）；
    /// 2＝鏈斷／損毀（第幾筆、什麼樣的斷法都在輸出裡）；1＝致命（路徑是目錄等）。
    /// </summary>
    private static int RunVerifyAudit(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string path = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1]
            : AuditLogService.DefaultPath;
        string? outPath = OptionValue(args, OutArg);

        bool fileExists;
        try { fileExists = File.Exists(path); }
        catch (Exception ex)
        {
            stderr.WriteLine($"無法檢查日誌路徑：{ex.Message}");
            return ExitError;
        }

        var verdict = AuditVerifier.VerifyFile(path);
        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            scope = "verify-audit",
            path,
            fileExists,
            chainValid = verdict.Valid,
            checkedEntries = verdict.CheckedCount,
            failureReason = verdict.FailureReason,
            note = fileExists
                ? "逐筆重算雜湊＋前綴串接檢查；改中間一筆必然讓該筆或後續 PreviousHash 不符。"
                : "檔案不存在＝還沒有任何審計事件（時間膠囊建立／比較時才會追加），不是「驗證通過」也不是錯誤。",
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        try
        {
            if (outPath is not null) File.WriteAllText(outPath, json);
            else stdout.WriteLine(json);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"寫出失敗：{ex.Message}");
            return ExitError;
        }
        return verdict.Valid ? ExitOk : ExitPartial;
    }

    /// <summary>
    /// 自我完整性基線模式（IN-001／IN-002 的唯一寫入路徑）：把自身二進位與設定檔的雜湊
    /// 記進審計日誌。刻意做成明示命令——工具可以證明自己沒被動過，但那個動作不該偷偷做。
    /// 退出碼：0＝已記錄（雜湊鏈可驗）；2＝未記錄（讀不到或寫不進去，原因在輸出）；1＝致命。
    /// </summary>
    private static int RunIntegrityBaseline(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string path = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1]
            : AuditLogService.DefaultPath;
        string? outPath = OptionValue(args, OutArg);

        var (written, summary, failure) = SelfIntegrityFactsService.RecordBaseline(
            SelfIntegrityInputs.Real(auditPath: path), path);

        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            scope = "integrity-baseline",
            path,
            written,
            summary,
            failureReason = failure,
            note = "基線存進既有審計日誌（append-only、雜湊鏈可驗）——不新增第二套基線儲存。" +
                   "雜湊證明的是「這份檔案現在是什麼」，不是防篡改保證。",
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        try
        {
            if (outPath is not null) File.WriteAllText(outPath, json);
            else stdout.WriteLine(json);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"寫出失敗：{ex.Message}");
            return ExitError;
        }
        if (!written && failure is not null) stderr.WriteLine(failure);
        return written ? ExitOk : ExitPartial;
    }

    /// <summary>
    /// 錯誤聚合模式（P0-07／UX-010 的 CLI 面，v2.64）：跑一次完整收集，把非 Present 的三態集中成清單。
    /// 退出碼：0＝全部 Present（空清單如實顯示）；2＝有三態事實（逐項帶原因）；1＝致命（收集或寫出失敗）。
    /// </summary>
    private static int RunErrors(string[] args, Func<IReadOnlyList<HardwareFact>> collect, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var facts = collect();
            var agg = ErrorAggregationService.Aggregate(facts);
            string json = JsonSerializer.Serialize(ErrorAggregationService.ToPayload(agg, DateTimeOffset.UtcNow), JsonOptions);
            string? outPath = OptionValue(args, OutArg);
            if (outPath is not null) File.WriteAllText(outPath, json);
            else stdout.WriteLine(json);
            return agg.Total == 0 ? ExitOk : ExitPartial;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"收集失敗：{ex.Message}");
            return ExitError;
        }
    }

    /// <summary>
    /// 磁碟用量模式（SG-002 深掃入口，v2.60）：<c>--disk-usage</c> 掃預設根（暫存與傾印）；
    /// <c>--disk-usage --deep</c> 加上使用者設定檔、深度 6。只讀不刪——本命令沒有任何刪除路徑。
    /// 退出碼：0＝全 Present；2＝部分三態（根讀不到會如實列出）；1＝致命（寫出失敗）。
    /// </summary>
    private static int RunDiskUsage(string[] args, TextWriter stdout, TextWriter stderr)
    {
        bool deep = args.Contains(DeepArg, StringComparer.OrdinalIgnoreCase);
        var request = deep ? DiskFullFactsService.DeepRequest() : DiskFullFactsService.DefaultRequest(2, 10);
        var facts = DiskFullFactsService.Collect(DateTimeOffset.UtcNow, request);

        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            scope = "disk-usage",
            deep,
            roots = request.Roots,
            maxDepth = request.MaxDepth,
            topCount = request.TopCount,
            allPresent = facts.All(f => f.Availability == FactAvailability.Present),
            facts,
            note = "只讀排行，不刪任何東西" + (deep ? "（含使用者設定檔，深度 6；整份遞迴要數十秒——"
                + "這就是它不進啟動路徑的原因）" : "（預設根不含使用者設定檔；--deep 明示加入）"),
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        try
        {
            string? outPath = OptionValue(args, OutArg);
            if (outPath is not null) File.WriteAllText(outPath, json);
            else stdout.WriteLine(json);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"寫出失敗：{ex.Message}");
            return ExitError;
        }
        return facts.All(f => f.Availability == FactAvailability.Present) ? ExitOk : ExitPartial;
    }

    /// <summary>
    /// SBOM 模式（RS-002）：輸出 CycloneDX 1.5 的 JSON。
    /// 退出碼：0＝元件完整（作業系統＋驅動＋本程式）；2＝部分元件讀不到（如驅動清單 WMI 失敗，
    /// 原因在 stderr，文件裡就只有讀得到的那些）；1＝致命（寫出失敗）。
    /// </summary>
    private static int RunSbom(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? outPath = OptionValue(args, OutArg);
        var (components, note) = SbomService.RealComponents();
        var doc = SbomService.Build(components, AppInfo.Name, AppInfo.Version, DateTimeOffset.UtcNow);
        string json = SbomService.ToJson(doc);
        try
        {
            if (outPath is not null) File.WriteAllText(outPath, json);
            else stdout.WriteLine(json);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"寫出失敗：{ex.Message}");
            return ExitError;
        }
        if (note is not null) stderr.WriteLine(note);
        return note is null ? ExitOk : ExitPartial;
    }

    private static object FactJson(HardwareFact f) => new
    {
        f.Key, f.Category, f.Name, f.Value, f.Unit, f.Source, f.Trust,
        availability = f.Availability.ToString(),
        f.UnavailableReason, f.NumericValue, f.MeasuredAtUtc,
    };

    private static string? OptionValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void PrintHelp(TextWriter w)
    {
        w.WriteLine("""
            曦覽 XinSpect CLI（V7 WP32）・ headless 事實收集

            用法：
              XinSpect --json evidence [--query <key 前綴>] [--out <檔案>]
              XinSpect --compare-flash <參考映像> [--out <檔案>]
              XinSpect --verify-audit [日誌路徑] [--out <檔案>]
              XinSpect --integrity-baseline [日誌路徑] [--out <檔案>]
              XinSpect --sbom [--out <檔案>]
              XinSpect --disk-usage [--deep] [--out <檔案>]
              XinSpect --errors [--out <檔案>]
              XinSpect --stdin-batch
              XinSpect --help

            範圍：
              evidence        驅動相依證據組：晶片組安全、SPI 快閃、平台安全 MSR、MCHBAR、
                              PCIe AER、後端與環境、CPU 韌體身分、I/O 埠、CMOS、SMBus、
                              UEFI 開機設定、Super I/O、PCI 裝置盤點、交叉對帳、ACPI 表清單；
                              外加免管理員的 usermode 組：儲存可靠性計數器（WMI）。
                              （其餘全機快照——SMBIOS／GPU／Windows Update 歷史——依賴 WPF
                              服務層，本模式未涵蓋；磁碟面只涵蓋可靠性計數器，不含 SMART。）
              compare-flash   BIOS 區 vs 參考映像逐 4KB 塊比對（映像＝原廠或信任來源的 BIOS 區 dump）。
              verify-audit    審計日誌雜湊鏈驗證（預設 %ProgramData%\XinSpect\Audit\audit.json；
                              可帶路徑）。輸出 fileExists／chainValid／checkedEntries 與斷點原因。
              integrity-baseline
                              把自身二進位與設定檔的 SHA-256 記進審計日誌（自我完整性的基線；
                              這是本工具唯一寫入自身基線的動作，故做成明示命令）。
              disk-usage      磁碟「為什麼滿」的唯讀排行（預設只掃暫存與傾印根）；
                              加 --deep 明示含使用者設定檔（深度 6，整份遞迴要數十秒）。
                              只讀不刪——本命令沒有任何刪除路徑。
              errors          錯誤聚合：把全部事實裡非 Present 的三態（讀取失敗／權限不足／
                              不支援／未知）集中成清單，逐項帶原因；零錯誤＝空清單如實顯示。
              sbom            產生 CycloneDX 1.5 的 SBOM（元件＝作業系統＋驅動＋本程式；
                              不含已安裝應用程式套件與授權）。

            選項：
              --query <前綴>   只輸出 key 以該前綴開頭的事實（例：--query platform.）
              --out <檔案>     寫入檔案而非 stdout

            退出碼：
              --json evidence   0＝全部 Present；2＝有非 Present 的事實（讀不到，或提供者沒提供
                                該欄位——兩者都不算「有值」，三態細節在輸出）；1＝致命錯誤。
              --compare-flash   0＝一致；2＝有差異或無法完成比對（三態細節在輸出）；1＝致命錯誤。
              --verify-audit    0＝鏈完整（或尚無日誌檔，輸出如實標 fileExists＝false）；
                                2＝鏈斷或檔案損毀（斷點在第幾筆、什麼型別，都在輸出）；1＝致命。
              --stdin-batch     每行讀取一個 key 前綴；空 stdin 回退出碼 2。零網路。
              --deep           --disk-usage 的明示深掃（含使用者設定檔）。
              --integrity-baseline
                                0＝已記錄（雜湊鏈可驗）；2＝未記錄（讀不到或寫不進去）；1＝致命。
              --sbom            0＝元件完整；2＝部分元件讀不到（原因在 stderr）；1＝致命。
              --stdin-batch     0＝至少一個非空查詢；2＝空 stdin 或未命中；1＝讀取／收集失敗。
            讀不到的事實如實帶 availability 與原因，絕不以 0／典型值頂替。
            """);
    }
}
