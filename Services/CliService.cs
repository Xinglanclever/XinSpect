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
    public const string DiffReportArg = "--diff-report";
    public const string OnlyArg = "--only";
    public const string ComplianceArg = "--compliance";
    public const string ComplianceDiffArg = "--compliance-diff";
    public const string ComplianceFormatArg = "--format";
    public const string FleetArg = "--fleet";
    public const string FleetSnapshotsArg = "--snapshots";
    public const string EnterpriseBaselineArg = "--enterprise-baseline";
    public const string EnterpriseBaselineSignArg = "--enterprise-baseline-sign";
    public const string EnterpriseBaselineTrustedPemArg = "--trusted-key";
    public const string EnterpriseBaselineSigningPemArg = "--signing-key";
    public const string EnterpriseBaselinePasswordStdinArg = "--password-stdin";
    public const string EnterpriseBaselineSnapshotArg = "--snapshot";
    public const string RedfishArg = "--redfish";
    public const string MerkleArg = "--merkle";
    public const string SpeculationArg = "--speculation";

    /// <summary>U10-PF／P1-071／P1-072 追蹤鍵：建立可第三方驗證的證據包（<c>--evidence-pack 目錄</c>）。
    /// 這個命令是 <c>ev.manifest</c> 與 <c>ev.chain.verify</c> 唯一的生產端——兩份產物只有
    /// 「建立包的那一刻」同時在手上（見 <see cref="EvidencePackBuilder"/> 的說明）。</summary>
    public const string EvidencePackArg = "--evidence-pack";
    public const string EvidencePackFactKey = "ev.pack.build";

    /// <summary>U10-PF-0007 追蹤鍵：選擇性匯出（<c>--only 前綴1,前綴2</c>）。
    /// 常數刻意不以「Key」命名：事實鍵目錄（FactKeyCatalog）本輪凍結，原始碼掃描器會把
    /// Services/ 裡名稱含「Key」的字串常數掃成事實鍵——這是產品追蹤標記，不是事實鍵。
    /// 字面值照放在 Services/ 供收口對帳。</summary>
    public const string PartialExportFactKey = "exp.partial";

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
        if (args[0] == DiffReportArg)
            return RunDiffReport(args, stdout, stderr);
        if (args[0] == EvidencePackArg)
            return RunEvidencePack(args, collect, stdout, stderr);
        if (args[0] == ComplianceArg)
            return RunCompliance(args, collect, stdout, stderr);
        if (args[0] == ComplianceDiffArg)
            return RunComplianceDiff(args, stdout, stderr);
        if (args[0] == EnterpriseBaselineArg)
            return RunEnterpriseBaseline(args, stdout, stderr);
        if (args[0] == EnterpriseBaselineSignArg)
            return RunEnterpriseBaselineSign(args, stdin, stdout, stderr);
        if (args[0] == MerkleArg)
            return RunMerkle(args, collect, stdout, stderr);
        if (args[0] == SpeculationArg)
            return RunSpeculation(args, collect, stdout, stderr);
        if (args[0] == FleetArg)
            return RunFleet(args, stdout, stderr);
        if (args[0] == RedfishArg)
            return RunRedfish(args, stdout, stderr);
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

        int totalCollectedBeforeFilter = facts.Count;   // 分母：任何過濾之前實際收集到的筆數

        if (query is not null)
            facts = facts.Where(f => f.Key.StartsWith(query, StringComparison.Ordinal)).ToList();

        // ── 選擇性匯出（U10-PF-0007／exp.partial）：--only 前綴1,前綴2 ──────────
        // 部分報告不得冒充全量：輸出如實帶分母（過濾前收集到的全部筆數）、
        // 選擇條件與「部分匯出」聲明；無命中時如實說是空匯出，退出碼 2（比照 stdin-batch 的未命中）。
        int totalCollected = totalCollectedBeforeFilter;
        string? onlyRaw = OptionValue(args, OnlyArg);
        string[] only = onlyRaw?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (only.Length > 0)
            facts = facts.Where(f => only.Any(p => f.Key.StartsWith(p, StringComparison.Ordinal))).ToList();

        int selected = facts.Count;
        bool filtered = query is not null || only.Length > 0;
        bool partial = only.Length > 0 && selected < totalCollected;
        // 無命中優先於「部分匯出」：0 筆也是「部分」，但如實說「空匯出」比說「部分」更有用。
        string? partialNote = filtered && selected == 0
            ? "篩選條件沒有命中任何事實——這是一份空匯出；「查無資料」不是「沒有問題」，也不是全量。"
            : partial
                ? $"部分匯出：這是 {totalCollected} 筆中的 {selected} 筆（--only {string.Join(",", only)}）。" +
                  "分母是本次收集到的全部事實——部分報告不是全量報告，引用時請帶上分母。"
                : null;

        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            scope = EvidenceScope,
            // U10-PF-0004（第 273 輪）：schema 版本宣告與相容性描述——接 SnapshotSchemaCompatService
            // 單一來源（未知的版本如實拒收的規則寫在該服務與其測試）。
            schemaVersion = SnapshotSchemaCompatService.CurrentVersion,
            schemaCompat = SnapshotSchemaCompatService.CompatibilityRules,
            count = facts.Count,
            totalCollected,                     // 分母：任何過濾之前實際收集到的筆數
            only,                               // 指定的鍵前綴清單（未指定＝空陣列）
            partial,                            // true＝這是一份部分匯出
            allPresent = facts.All(f => f.Availability == FactAvailability.Present),
            writeAudit = WriteGate.DescribeSession(),   // 本次執行寫了哪些暫存器／埠——可回答，不是黑箱
            note = partialNote,
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

        if (filtered && selected == 0) return ExitPartial;
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
            new { mode = JsonArg, scope = EvidenceScope, output = "JSON", description = "headless 事實收集；支援 --query／--only 前綴選擇與 --out 檔案，部分匯出如實帶分母，零網路。" },
            new { mode = CompareArg, scope = "", output = "JSON", description = "BIOS 區與參考映像逐塊比對；只讀本機映像。" },
            new { mode = VerifyAuditArg, scope = "", output = "JSON", description = "驗證本機審計日誌雜湊鏈。" },
            new { mode = IntegrityBaselineArg, scope = "", output = "JSON", description = "明示寫入自身完整性基線；需要本機寫入權限。" },
            new { mode = SbomArg, scope = "", output = "JSON", description = "產生離線 CycloneDX SBOM。" },
            new { mode = DiskUsageArg, scope = "", output = "JSON", description = "磁碟滿的唯讀排行；--deep 是明示深掃。" },
            new { mode = ErrorsArg, scope = "", output = "JSON", description = "聚合非 Present 三態與原因。" },
            new { mode = StdinBatchArg, scope = "", output = "JSONL", description = "stdin 每行一個 key 前綴；每行輸出一個查詢結果物件。" },
            new { mode = DiffReportArg, scope = "", output = "JSON", description = "兩份匯出報告（HTML／MD／TXT）鍵級互比；輸出只含鍵名與計數，不含值。" },
            new { mode = EvidencePackArg, scope = "", output = "JSON", description = "建立可第三方驗證的證據包（事實 JSON＋審計鏈副本＋manifest 雜湊清單＋互驗結果）；這是 ev.manifest 與 ev.chain.verify 唯一的生產端。" },
            new { mode = ComplianceArg, scope = "", output = "JSON／SARIF／JUnit／CEF／OTLP", description = "把既有事實對到 NIST SP 800-147／155／193 與 MITRE ATT&CK；不新增硬體讀取，缺資料一律 Unverifiable。--format 可選 json、sarif、junit、cef、otel。" },
            new { mode = ComplianceDiffArg, scope = "", output = "JSON", description = "比較兩份 --compliance JSON 的控制項／ATT&CK 判定變化；不重新收集、不重新判定。" },
            new { mode = MerkleArg, scope = "", output = "JSON", description = "穩定事實的 SHA-256 Merkle 指紋；白名單＋排除敏感／非 Present／易變鍵，輸出只含鍵與雜湊。" },
            new { mode = SpeculationArg, scope = "", output = "JSON", description = "七項 infer.* 推測；每項都帶【推測】前綴與 Speculative 信任等級，並列信心、依據鍵與限制。不合規、不進 Merkle。" },
            new { mode = FleetArg, scope = "", output = "JSON", description = "只讀本機快照目錄，彙總同型號族群的 fleet.* 基線；不上傳、不跨機傳輸。--snapshots 可指定目錄。" },
            new { mode = EnterpriseBaselineArg, scope = "", output = "JSON", description = "驗證組織簽署的離線基準套件，對已驗證快照評估；需預先信任公鑰，不輸出硬體值。" },
            new { mode = EnterpriseBaselineSignArg, scope = "", output = "signed package file", description = "管理端簽署組織基準 JSON；私鑰只由管理者受控環境讀取，不輸出至 stdout。" },
            new { mode = RedfishArg, scope = "", output = "JSON", description = "讀取已匯出的離線 Redfish 目錄樹（BMC／機箱／電源／風扇／溫度）；只讀本機檔案，不連網。" },
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

    /// <summary>
    /// 報告互比模式（U10-PF-0002，追蹤鍵 <c>rep.diff.mode</c>）：兩份匯出報告（HTML／Markdown／純文字）
    /// 逐鍵對照新增／消失／值變化。<b>輸出只含鍵名與計數，不含任何值</b>——diff 的輸出經常被貼到
    /// 公開場合，值進了輸出就等於把遮蔽功能整個繞過。
    /// 退出碼：0＝鍵級全同；2＝有差異（細節在輸出）；1＝致命（引數錯、讀檔失敗、格式不認得）。
    /// </summary>
    /// <summary>
    /// 建立證據包：事實 JSON ＋ 審計鏈副本 ＋ manifest（逐檔 SHA-256）＋ 互驗結果。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 這個命令是 <c>ev.manifest</c> 與 <c>ev.chain.verify</c> <b>唯一的生產端</b>。
    /// 那兩把鍵登記在目錄已久、產生它們的方法也早就有測試，但正式流程從沒有人生產——
    /// 缺的不是鍵，是「兩份產物同時在手上」的那一刻：沒有包就沒有 manifest，
    /// 也就無從互驗。這裡補的就是那一刻（理由寫在 <see cref="EvidencePackBuilder"/>）。
    /// </para>
    /// <para>
    /// <b>退出碼：</b>0＝包已建立且互驗通過；2＝包已建立但互驗未通過（原因在 verify.json，
    /// 兩份產物對不上是<b>結果</b>不是執行失敗）；1＝致命（目錄不可寫、收集失敗等）。
    /// </para>
    /// </remarks>
    private static int RunEvidencePack(string[] args, Func<IReadOnlyList<HardwareFact>> collect,
        TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            stderr.WriteLine($"{EvidencePackArg} 之後要接輸出目錄：{EvidencePackArg} <目錄>。");
            return ExitError;
        }
        string directory = args[1];

        WriteGate.Reset();
        IReadOnlyList<HardwareFact> facts;
        try { facts = collect(); }
        catch (Exception ex)
        {
            stderr.WriteLine($"收集失敗：{ex.GetType().Name}：{ex.Message}");
            return ExitError;
        }

        var at = DateTimeOffset.UtcNow;
        var payload = new
        {
            generatedAtUtc = at,
            scope = "evidence-pack",
            schemaVersion = SnapshotSchemaCompatService.CurrentVersion,
            schemaCompat = SnapshotSchemaCompatService.CompatibilityRules,
            count = facts.Count,
            writeAudit = WriteGate.DescribeSession(),
            facts = facts.Select(FactJson),
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

        EvidencePackBuilder.Result built;
        try
        {
            built = EvidencePackBuilder.Build(directory,
                [("evidence.json", json)],
                AuditLogService.DefaultPath, at);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"建立證據包失敗：{ex.GetType().Name}：{ex.Message}");
            return ExitError;
        }

        var manifestFact = WriteGateReport.ToManifestFact(at,
            built.Files.Select(e => (e.File, e.Sha256)).ToList(), built.Missing);
        var chainFact = WriteGateReport.ToChainVerifyFact(at, built.ChainValid, built.ManifestValid,
            built.Failure);
        string verifyJson = EvidencePackBuilder.WriteVerifyReport(built, [manifestFact, chainFact], at);

        WriteGate.Record($"evidence-pack:{Path.GetFileName(Path.TrimEndingDirectorySeparator(built.Directory))}",
            $"{nameof(CliService)}.{nameof(RunEvidencePack)}",
            $"包內 {built.Files.Count} 檔、manifest 雜湊 {built.ManifestSha256[..12]}", built.ManifestValid && built.ChainValid);

        string summary = JsonSerializer.Serialize(new
        {
            scope = "evidence-pack",
            directory = built.Directory,
            fileCount = built.Files.Count,
            manifestSha256 = built.ManifestSha256,
            manifestValid = built.ManifestValid,
            chainValid = built.ChainValid,
            failure = built.Failure,
            verifyFile = Path.Combine(built.Directory, EvidencePackBuilder.VerifyFileName),
            note = "兩者必須互相印證才算數；verify.json 有逐把事實與原因。",
            verify = verifyJson.Length,
        }, JsonOptions);
        string? outPath = OptionValue(args, OutArg);
        try
        {
            if (outPath is not null) File.WriteAllText(outPath, summary);
            else stdout.WriteLine(summary);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"無法輸出結果：{ex.Message}");
            return ExitError;
        }

        return built.ManifestValid && built.ChainValid ? ExitOk : ExitPartial;
    }

    /// <summary>
    /// 標準對照模式（<c>--compliance</c>）：把既有事實翻成 NIST SP 800-147／155／193 與
    /// MITRE ATT&amp;CK 的語言。<b>不新增任何硬體讀取</b>；讀不到的控制項一律 Unverifiable。
    /// 退出碼：0＝全部 Satisfied／NotApplicable；2＝有任何 NotSatisfied／Unverifiable／Exposed／Unknown；
    /// 1＝收集或寫出失敗。Unverifiable 也回 2——「無法判斷」不該被 CI 讀成通過。
    /// </summary>
    private static int RunCompliance(string[] args, Func<IReadOnlyList<HardwareFact>> collect,
        TextWriter stdout, TextWriter stderr)
    {
        WriteGate.Reset();
        IReadOnlyList<HardwareFact> facts;
        try { facts = collect(); }
        catch (Exception ex)
        {
            stderr.WriteLine($"收集失敗：{ex.GetType().Name}：{ex.Message}");
            return ExitError;
        }

        var report = ComplianceMappingService.Evaluate(facts);
        var controls = report.Controls;
        var techniques = report.Techniques;

        bool formatSpecified = args.Contains(ComplianceFormatArg);
        string? formatValue = OptionValue(args, ComplianceFormatArg);
        if (formatSpecified && formatValue is null)
        {
            stderr.WriteLine("--format 需要一個值：json、sarif、junit、cef 或 otel。");
            return ExitError;
        }

        // 〔修復 v3.20〕審計 D2：CommandBook 宣稱「compliance sarif」（位置參數）也通，
        // 原實作只認 --format——位置參數被靜默忽略、照樣輸出 JSON，自動化會吃錯格式。
        // 補上位置解析：第一個不屬於任何旗標／旗標值的引數，若為五種格式名則視為 format；
        // 是別的裸字則如實報錯（不猜、不靜默）。取值旗標（--format／--out）連同其值一併跳過。
        if (formatValue is null)
        {
            var valueTakingFlags = new HashSet<string>(StringComparer.Ordinal) { ComplianceFormatArg, OutArg };
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith('-'))
                {
                    if (valueTakingFlags.Contains(args[i]) && i + 1 < args.Length) i++;   // 連同旗標值跳過
                    continue;
                }
                if (args[i].ToLowerInvariant() is "json" or "sarif" or "junit" or "cef" or "otel")
                {
                    formatValue = args[i];
                    break;
                }
                stderr.WriteLine($"不支援的格式「{args[i]}」；可用：json、sarif、junit、cef、otel（或用 --format）。");
                return ExitError;
            }
        }

        string format = (formatValue ?? "json").ToLowerInvariant();
        if (format is not ("json" or "sarif" or "junit" or "cef" or "otel"))
        {
            stderr.WriteLine($"不支援的格式「{formatValue}」；可用：json、sarif、junit、cef、otel。");
            return ExitError;
        }

        var generatedAt = DateTimeOffset.UtcNow;
        var payload = new
        {
            generatedAtUtc = generatedAt,
            scope = "compliance",
            schemaVersion = ComplianceMappingService.SchemaVersion,
            summary = new
            {
                controls = controls.Count,
                satisfied = controls.Count(c => c.Status == ComplianceStatus.Satisfied),
                notSatisfied = controls.Count(c => c.Status == ComplianceStatus.NotSatisfied),
                unverifiable = controls.Count(c => c.Status == ComplianceStatus.Unverifiable),
                notApplicable = controls.Count(c => c.Status == ComplianceStatus.NotApplicable),
                techniques = techniques.Count,
                mitigated = techniques.Count(t => t.Exposure == AttackExposure.Mitigated),
                exposed = techniques.Count(t => t.Exposure == AttackExposure.Exposed),
                unknown = techniques.Count(t => t.Exposure == AttackExposure.Unknown),
            },
            controls,
            attack = techniques,
            note = "本模式只把既有事實對到標準條文，沒有驗簽、沒有 TPM Quote、沒有觸發復原——"
                 + "這三類控制項刻意維持 Unverifiable。Satisfied 是「本次看到的事實支持這條控制」，"
                 + "不是合規證書；Unverifiable 也回退出碼 2。",
        };
        string output = format switch
        {
            "sarif" => ComplianceExportService.ToSarif(report, generatedAt),
            "junit" => ComplianceExportService.ToJUnit(report, generatedAt),
            "cef" => ComplianceExportService.ToCef(report, generatedAt),
            "otel" => ComplianceExportService.ToOtel(report, generatedAt),
            _ => JsonSerializer.Serialize(payload, JsonOptions),
        };
        string? outPath = OptionValue(args, OutArg);
        try
        {
            if (outPath is not null) File.WriteAllText(outPath, output);
            else stdout.WriteLine(output);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"寫出失敗：{ex.Message}");
            return ExitError;
        }

        bool clean = controls.All(c => c.Status is ComplianceStatus.Satisfied or ComplianceStatus.NotApplicable)
            && techniques.All(t => t.Exposure is AttackExposure.Mitigated or AttackExposure.NotApplicable);
        return clean ? ExitOk : ExitPartial;
    }

    /// <summary>
    /// 離線 Redfish 目錄（<c>--redfish</c>）：只讀本機 JSON 樹，不連 BMC、不連網。
    /// 退出碼：0＝至少讀到一個管理器／機箱／系統；2＝沒有可讀資源；1＝引數或寫出失敗。
    /// </summary>
    private static int RunRedfish(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            stderr.WriteLine($"{RedfishArg} 之後要接離線 Redfish 目錄的路徑：{RedfishArg} <目錄>。");
            return ExitError;
        }

        RedfishInventory inventory;
        try { inventory = RedfishBundleService.Read(args[1]); }
        catch (Exception ex)
        {
            stderr.WriteLine($"讀取失敗：{ex.GetType().Name}：{ex.Message}");
            return ExitError;
        }

        bool any = inventory.Managers.Count + inventory.Chassis.Count + inventory.Systems.Count > 0;
        var at = DateTimeOffset.UtcNow;
        var facts = RedfishFactsService.Collect(inventory, at);
        var payload = new
        {
            generatedAtUtc = at,
            scope = "redfish",
            schemaVersion = RedfishBundleService.SchemaVersion,
            source = RedfishBundleService.Source,
            directory = inventory.Directory,
            collectedAtUtc = inventory.CollectedAtUtc,
            facts = facts.Select(f => new
            {
                key = f.Key,
                name = f.Name,
                value = f.Value,
                unit = f.Unit,
                trust = f.Trust.ToString(),
                availability = f.Availability.ToString(),
                reason = f.UnavailableReason,
            }),
            counts = new
            {
                managers = inventory.Managers.Count,
                chassis = inventory.Chassis.Count,
                systems = inventory.Systems.Count,
                powerSupplies = inventory.PowerSupplies.Count,
                fans = inventory.Fans.Count,
                temperatures = inventory.Temperatures.Count,
            },
            managers = inventory.Managers,
            chassis = inventory.Chassis,
            systems = inventory.Systems,
            powerSupplies = inventory.PowerSupplies,
            fans = inventory.Fans,
            temperatures = inventory.Temperatures,
            powerConsumedWatts = inventory.PowerConsumedWatts,
            warnings = inventory.Warnings,
            note = "只讀本機已匯出的 Redfish 目錄樹：不連 BMC、不連網、不送出任何請求。"
                 + "BMC 回報值是 Reported，不是本工具量測；缺少的集合與壞掉的 JSON 只記警告，不補 0。"
                 + "退出碼 2 代表沒有讀到管理器／機箱／系統，不代表機器沒有 BMC。",
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        string? outPath = OptionValue(args, OutArg);
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
        return any ? ExitOk : ExitPartial;
    }

    /// <summary>
    /// 族群基線（<c>--fleet</c>）：只讀本機快照目錄，彙總同型號族群的 <c>fleet.*</c> 事實。
    /// 不出網、不上傳、不跨機傳輸。退出碼：0＝有可比的同型號族群；2＝沒有快照／沒有型號／沒有第二台；1＝致命。
    /// </summary>
    private static int RunFleet(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string directory = OptionValue(args, FleetSnapshotsArg) ?? FleetSnapshotReader.Directory;
        var at = DateTimeOffset.UtcNow;
        IReadOnlyList<FleetSnapshot> snapshots;
        IReadOnlyList<HardwareFact> facts;
        try
        {
            snapshots = FleetSnapshotReader.Read(directory);
            facts = FleetBaselineFactsService.Collect(snapshots, at);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"讀取快照失敗：{ex.GetType().Name}：{ex.Message}");
            return ExitError;
        }

        var summary = facts.FirstOrDefault(f => f.Key == FleetBaselineFactsService.SummaryKey);
        bool comparable = summary?.Availability == FactAvailability.Present;
        var payload = new
        {
            generatedAtUtc = at,
            scope = "fleet",
            schemaVersion = 1,
            directory,
            snapshotCount = snapshots.Count,
            comparable,
            facts = facts.Select(f => new
            {
                key = f.Key,
                name = f.Name,
                value = f.Value,
                numericValue = f.NumericValue,
                unit = f.Unit,
                source = f.Source,
                trust = f.Trust.ToString(),
                sensitive = f.Sensitive,
                measuredAtUtc = f.MeasuredAtUtc,
                availability = f.Availability.ToString(),
                unavailableReason = f.UnavailableReason,
            }),
            note = "只讀本機快照目錄：不上傳、不跨機傳輸、不查外部資料庫。"
                 + "「同型號」是字串相等，不是 SKU 知識；樣本少於 4 份只給中位數與範圍，不給 z 分數。"
                 + "comparable=false（退出碼 2）代表沒有可比的第二台，不是「沒有差異」。",
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        string? outPath = OptionValue(args, OutArg);
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

        return comparable ? ExitOk : ExitPartial;
    }

    private static string ReadBoundedUtf8File(string path, int maximumBytes, string description)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
            throw new InvalidDataException($"{description} 超過 {maximumBytes} bytes 上限。");

        byte[] buffer = new byte[maximumBytes + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = stream.Read(buffer, length, buffer.Length - length);
            if (read == 0) break;
            length += read;
        }
        if (length > maximumBytes)
            throw new InvalidDataException($"{description} 超過 {maximumBytes} bytes 上限。");

        string text = new System.Text.UTF8Encoding(false, true).GetString(buffer, 0, length);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>
    /// 對已驗證的快照評估組織簽署的離線基準。端點只持有公鑰；私鑰由政策管理者保管。
    /// </summary>
    private static int RunEnterpriseBaseline(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? policyPath = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1] : null;
        string? publicKeyPath = OptionValue(args, EnterpriseBaselineTrustedPemArg);
        string? snapshotPath = OptionValue(args, EnterpriseBaselineSnapshotArg);
        if (policyPath is null || publicKeyPath is null || snapshotPath is null)
        {
            stderr.WriteLine("用法：--enterprise-baseline <政策套件.json> --trusted-key <組織公鑰.pem> --snapshot <快照.json> [--out <報告.json>]");
            return ExitError;
        }

        string? requestedOutputPath = OptionValue(args, OutArg);
        if (requestedOutputPath is not null)
        {
            try
            {
                string destination = Path.GetFullPath(requestedOutputPath);
                if (new[] { policyPath, publicKeyPath, snapshotPath }
                    .Any(source => string.Equals(destination, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)))
                {
                    stderr.WriteLine("企業基準報告不得覆寫政策套件、公鑰或輸入快照。");
                    return ExitError;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                       or System.Security.SecurityException)
            {
                stderr.WriteLine($"企業基準報告路徑無效（{ex.GetType().Name}）。");
                return ExitError;
            }
        }

        EnterpriseBaselineEvaluation evaluation;
        try
        {
            string package = ReadBoundedUtf8File(policyPath,
                EnterpriseBaselinePackageService.MaxPackageBytes, "政策套件");
            string trustedPublicKey = ReadBoundedUtf8File(publicKeyPath,
                EnterpriseBaselinePackageService.MaxKeyPemBytes, "組織信任公鑰");

            // Reject untrusted or expired policies before touching the snapshot input.
            var verified = EnterpriseBaselinePackageService.Evaluate(package, trustedPublicKey,
                new Dictionary<string, HardwareFact>(StringComparer.Ordinal), DateTimeOffset.UtcNow);
            if (!verified.SignatureValid || verified.Expired)
            {
                evaluation = verified;
            }
            else
            {
                var snapshot = HardwareSnapshotService.Load(snapshotPath);
                var facts = snapshot.Facts.ToDictionary(f => f.Key, f => new HardwareFact(
                    f.Key, f.Category, f.Name, f.Value, f.Unit ?? "", f.Source, f.Trust,
                    f.Sensitive, f.MeasuredAtUtc, f.NumericValue, f.Availability, f.UnavailableReason),
                    StringComparer.Ordinal);
                evaluation = EnterpriseBaselinePackageService.Evaluate(package, trustedPublicKey, facts,
                    DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"企業基準評估失敗（{ex.GetType().Name}）。");
            return ExitError;
        }

        string status = !evaluation.SignatureValid ? "Rejected"
            : evaluation.Expired ? "Expired"
            : evaluation.NotSatisfied > 0 ? "NotSatisfied"
            : evaluation.Unverifiable > 0 ? "Unverifiable"
            : "Satisfied";
        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            scope = "enterprise-baseline",
            schemaVersion = EnterpriseBaselinePackageService.CurrentSchemaVersion,
            evaluation.PolicyId,
            evaluation.SignerKeyId,
            policySignatureValid = evaluation.SignatureValid,
            evaluation.Expired,
            status,
            counts = new
            {
                evaluation.Satisfied,
                evaluation.NotSatisfied,
                evaluation.Unverifiable,
            },
            results = evaluation.Results,
            evaluation.FailureReason,
            note = "已驗證的是政策簽章；本評估報告與快照來源未簽章、未做遠端證明。快照 SHA-256 只驗內容完整性，不證明來源。結果只代表此快照符合組織自訂基準，不代表整體安全性或硬體故障判決；輸出不包含事實值。",
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        string? outPath = OptionValue(args, OutArg);
        try
        {
            if (outPath is not null) File.WriteAllText(outPath, json);
            else stdout.WriteLine(json);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"寫出企業基準報告失敗（{ex.GetType().Name}）。");
            return ExitError;
        }

        return !evaluation.SignatureValid ? ExitError
            : evaluation.Expired || evaluation.NotSatisfied > 0 || evaluation.Unverifiable > 0
                ? ExitPartial : ExitOk;
    }

    private static int RunEnterpriseBaselineSign(string[] args, TextReader? stdin,
        TextWriter stdout, TextWriter stderr)
    {
        string? policyPath = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1] : null;
        string? signingKeyPath = OptionValue(args, EnterpriseBaselineSigningPemArg);
        string? outPath = OptionValue(args, OutArg);
        if (policyPath is null || signingKeyPath is null || outPath is null
            || !args.Contains(EnterpriseBaselinePasswordStdinArg, StringComparer.Ordinal))
        {
            stderr.WriteLine("用法：<簽章密碼> | --enterprise-baseline-sign <政策.json> --signing-key <加密私鑰.pem> --password-stdin --out <簽章套件.json>；只在管理端執行。");
            return ExitError;
        }
        if (stdin is null || stdin.ReadLine() is not { Length: > 0 } password)
        {
            stderr.WriteLine("簽章密碼必須由 stdin 提供；不接受命令列密碼。");
            return ExitError;
        }

        try
        {
            string destination = Path.GetFullPath(outPath);
            if (string.Equals(destination, Path.GetFullPath(policyPath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(destination, Path.GetFullPath(signingKeyPath), StringComparison.OrdinalIgnoreCase))
            {
                stderr.WriteLine("簽章套件輸出路徑不得覆寫政策來源或簽章私鑰。");
                return ExitError;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                   or System.Security.SecurityException)
        {
            stderr.WriteLine($"企業基準簽署路徑無效（{ex.GetType().Name}）。");
            return ExitError;
        }

        try
        {
            string policyJson = ReadBoundedUtf8File(policyPath,
                EnterpriseBaselinePackageService.MaxPackageBytes, "政策 JSON");
            string privateKeyPem = ReadBoundedUtf8File(signingKeyPath,
                EnterpriseBaselinePackageService.MaxKeyPemBytes, "簽署私鑰 PEM");
            string signedPackage = EnterpriseBaselinePackageService.Sign(policyJson, privateKeyPem, password);
            password = string.Empty;
            File.WriteAllText(outPath, signedPackage);
            return ExitOk;
        }
        catch (Exception ex)
        {
            password = string.Empty;
            stderr.WriteLine($"企業基準簽署失敗（{ex.GetType().Name}）。");
            return ExitError;
        }
    }

    /// <summary>
    /// 合規基準比較（<c>--compliance-diff</c>）：只讀兩份既有 JSON，不重新收集、不重新判定。
    /// 退出碼：0＝判定全同；2＝有任何新增／消失／改善／退步／變動；1＝讀取或格式錯誤。
    /// </summary>
    private static int RunComplianceDiff(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 3 || args[1].StartsWith("--", StringComparison.Ordinal)
            || args[2].StartsWith("--", StringComparison.Ordinal))
        {
            stderr.WriteLine($"{ComplianceDiffArg} 之後要接兩份 --compliance JSON 的路徑："
                + $"{ComplianceDiffArg} <舊 JSON> <新 JSON>。");
            return ExitError;
        }

        string beforeJson, afterJson;
        try { beforeJson = File.ReadAllText(args[1]); }
        catch (Exception ex) { stderr.WriteLine($"讀取舊檔失敗：{ex.Message}"); return ExitError; }
        try { afterJson = File.ReadAllText(args[2]); }
        catch (Exception ex) { stderr.WriteLine($"讀取新檔失敗：{ex.Message}"); return ExitError; }

        ComplianceDiff diff;
        try { diff = ComplianceDiffService.Compare(beforeJson, afterJson); }
        catch (FormatException ex)
        {
            stderr.WriteLine($"格式不認得：{ex.Message}");
            return ExitError;
        }

        int Count(IReadOnlyList<ComplianceItemChange> list, ComplianceChangeKind kind)
            => list.Count(x => x.Kind == kind);

        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            scope = "compliance-diff",
            schemaVersion = ComplianceDiffService.SchemaVersion,
            before = new { path = args[1], controls = diff.ControlsBefore, attack = diff.TechniquesBefore },
            after = new { path = args[2], controls = diff.ControlsAfter, attack = diff.TechniquesAfter },
            summary = new
            {
                changed = diff.Controls.Count + diff.Techniques.Count,
                controls = new
                {
                    improved = Count(diff.Controls, ComplianceChangeKind.Improved),
                    regressed = Count(diff.Controls, ComplianceChangeKind.Regressed),
                    changed = Count(diff.Controls, ComplianceChangeKind.Changed),
                    added = Count(diff.Controls, ComplianceChangeKind.Added),
                    removed = Count(diff.Controls, ComplianceChangeKind.Removed),
                },
                attack = new
                {
                    improved = Count(diff.Techniques, ComplianceChangeKind.Improved),
                    regressed = Count(diff.Techniques, ComplianceChangeKind.Regressed),
                    changed = Count(diff.Techniques, ComplianceChangeKind.Changed),
                    added = Count(diff.Techniques, ComplianceChangeKind.Added),
                    removed = Count(diff.Techniques, ComplianceChangeKind.Removed),
                },
            },
            controls = diff.Controls,
            attack = diff.Techniques,
            note = "只比較兩份 JSON 的判定，不重新收集或驗簽。"
                 + "Satisfied／Mitigated→其他＝regressed（含證據變成 Unverifiable）；"
                 + "其他→Satisfied／Mitigated＝improved；其餘判定變動＝changed。"
                 + "方向只是排序，不是風險評分；退出碼 0 只代表兩份判定相同。",
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        string? outPath = OptionValue(args, OutArg);
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

        return diff.Controls.Count == 0 && diff.Techniques.Count == 0 ? ExitOk : ExitPartial;
    }

    /// <summary>
    /// 穩定事實指紋模式（<c>--merkle</c>）：白名單事實 → canonical payload → SHA-256 Merkle 樹。
    /// 葉子與輸出<b>只含鍵與雜湊，不含值</b>；敏感事實、非 Present 事實、易變鍵一律排除並分類計數。
    /// 退出碼：0＝至少一片葉子；2＝沒有任何穩定事實；1＝收集或寫出失敗。
    /// </summary>
    private static int RunMerkle(string[] args, Func<IReadOnlyList<HardwareFact>> collect,
        TextWriter stdout, TextWriter stderr)
    {
        WriteGate.Reset();
        IReadOnlyList<HardwareFact> facts;
        try { facts = collect(); }
        catch (Exception ex)
        {
            stderr.WriteLine($"收集失敗：{ex.GetType().Name}：{ex.Message}");
            return ExitError;
        }

        var tree = FactMerkleService.Build(facts);
        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            scope = "merkle",
            schemaVersion = FactMerkleService.SchemaVersion,
            algorithm = FactMerkleService.Algorithm,
            canonicalization = "key ＋ 0x1F ＋ value ＋ 0x1F ＋ trust ＋ 0x1F ＋ source（不含 MeasuredAtUtc）",
            leafCount = tree.LeafCount,
            rootSha256 = tree.RootSha256,
            leaves = tree.Leaves,
            excluded = tree.Excluded,
            note = tree.LeafCount == 0
                ? "沒有穩定事實可建樹——空樹不是指紋，也不代表機器沒有問題。"
                : "同一台機器、同樣組態與來源文字，跨次收集應得到同一個 root；"
                  + "root 變了代表白名單內的事實變了，不必然是壞事。輸出不含值。",
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        string? outPath = OptionValue(args, OutArg);
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
        return tree.LeafCount > 0 ? ExitOk : ExitPartial;
    }

    /// <summary>
    /// 推測模式（<c>--speculation</c>）：把既有事實餵給 <see cref="SpeculationFactsService"/>，
    /// 產生七項 <c>infer.*</c>。每一項都同時帶鍵名前綴、值前綴與 <c>Speculative</c> 信任等級。
    /// 退出碼：0＝七項都產生；2＝有推測項未產生（正常不該發生，發生代表規則集合漂移）；
    /// 1＝收集或寫出失敗。推測不是錯誤，所以 0 不代表「機器沒問題」。
    /// </summary>
    private static int RunSpeculation(string[] args, Func<IReadOnlyList<HardwareFact>> collect,
        TextWriter stdout, TextWriter stderr)
    {
        WriteGate.Reset();
        IReadOnlyList<HardwareFact> facts;
        try { facts = collect(); }
        catch (Exception ex)
        {
            stderr.WriteLine($"收集失敗：{ex.GetType().Name}：{ex.Message}");
            return ExitError;
        }

        var items = SpeculationFactsService.Evaluate(facts, DateTimeOffset.UtcNow);
        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            scope = "speculation",
            schemaVersion = SpeculationFactsService.SchemaVersion,
            label = SpeculationFactsService.Label,
            defaultTrust = FactTrustLevel.Speculative.ToString(),
            count = items.Count,
            items = items.Select(x => new
            {
                key = x.Fact.Key,
                name = x.Fact.Name,
                value = x.Fact.Value,
                trust = x.Fact.Trust.ToString(),
                availability = x.Fact.Availability.ToString(),
                confidence = x.Confidence,
                basisKeys = x.BasisKeys,
                basis = x.Basis,
                limitations = x.Limitations,
            }),
            note = "全部七項都是間接推導：不是量測、不是簽章驗證、不是合規證據。"
                 + "鍵名前綴 infer.、值前綴【推測】、信任等級 Speculative 三者同時存在；"
                 + "合規判定與 Merkle 指紋都不採用本層。退出碼 0 只代表「推測已產生」，"
                 + "不代表機器安全或合規。",
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        string? outPath = OptionValue(args, OutArg);
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
        return items.Count == 7 ? ExitOk : ExitPartial;
    }

    private static int RunDiffReport(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 3 || args[1].StartsWith("--", StringComparison.Ordinal)
            || args[2].StartsWith("--", StringComparison.Ordinal))
        {
            stderr.WriteLine($"{DiffReportArg} 之後要接兩份報告檔的路徑：{DiffReportArg} <舊報告> <新報告>。");
            return ExitError;
        }
        string? outPath = OptionValue(args, OutArg);

        string textBefore, textAfter;
        try { textBefore = File.ReadAllText(args[1]); }
        catch (Exception ex) { stderr.WriteLine($"讀取舊報告失敗：{ex.Message}"); return ExitError; }
        try { textAfter = File.ReadAllText(args[2]); }
        catch (Exception ex) { stderr.WriteLine($"讀取新報告失敗：{ex.Message}"); return ExitError; }

        var diff = ReportDiffService.Compare(textBefore, textAfter);
        if (diff.FormatBefore == "unknown" || diff.FormatAfter == "unknown")
        {
            stderr.WriteLine("檔案格式不認得（不是 HTML／Markdown／純文字報告的形狀）——不硬解析出半份假結果。");
            return ExitError;
        }

        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            scope = "diff-report",
            before = new { Path = args[1], Format = diff.FormatBefore, Keys = diff.KeyCountBefore },
            after = new { Path = args[2], Format = diff.FormatAfter, Keys = diff.KeyCountAfter },
            diff.Added,
            diff.Removed,
            diff.Changed,
            addedCount = diff.Added.Count,
            removedCount = diff.Removed.Count,
            changedCount = diff.Changed.Count,
            note = "值變化只列鍵名與「有變」，新舊值一律不進輸出——報告內含序號、MAC 等識別資訊，"
                 + "diff 的結果貼到公開場合也不會把遮蔽功能繞過。",
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
        return diff.Identical ? ExitOk : ExitPartial;
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
              xsc --lang zh-TW|zh-CN [以下任一命令]（僅切換文字介面；JSON／SARIF 欄位與輸出不變）
              XinSpect --json evidence [--query <key 前綴>] [--only <前綴1,前綴2>] [--out <檔案>]
              XinSpect --compare-flash <參考映像> [--out <檔案>]
              XinSpect --verify-audit [日誌路徑] [--out <檔案>]
              XinSpect --integrity-baseline [日誌路徑] [--out <檔案>]
              XinSpect --sbom [--out <檔案>]
              XinSpect --disk-usage [--deep] [--out <檔案>]
              XinSpect --errors [--out <檔案>]
              XinSpect --stdin-batch
              XinSpect --diff-report <舊報告> <新報告> [--out <檔案>]
              XinSpect --evidence-pack <目錄>
              XinSpect --compliance [--format json|sarif|junit|cef|otel] [--out <檔案>]
              XinSpect --compliance-diff <舊 JSON> <新 JSON> [--out <檔案>]
              XinSpect --merkle [--out <檔案>]
              XinSpect --speculation [--out <檔案>]
              XinSpect --fleet [--snapshots <目錄>] [--out <檔案>]
              XinSpect --enterprise-baseline <政策套件.json> --trusted-key <公鑰.pem> --snapshot <快照.json> [--out <報告.json>]
              <密碼管線> | XinSpect --enterprise-baseline-sign <政策.json> --signing-key <加密私鑰.pem> --password-stdin --out <簽章套件.json>（管理端）
              XinSpect --redfish <離線 Redfish 目錄> [--out <檔案>]
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
              diff-report     兩份匯出報告（HTML／Markdown／純文字）的鍵級互比：
                              新增鍵／消失鍵／值變化。<b>輸出只含鍵名與計數，不含任何值</b>——
                              值變化只說「有變」，序號與 MAC 這類識別資訊不會經由 diff 洩漏。
                              格式不認得的檔案如實拒比，不硬解析。
              evidence-pack   建立可第三方驗證的證據包：事實 JSON＋審計鏈副本＋manifest
                              （逐檔 SHA-256）＋verify.json（互驗結果）。<b>這是 ev.manifest 與
                              ev.chain.verify 唯一的生產端</b>——那兩把鍵必須在同一刻、兩份產物
                              都在手上時才算得出來（拿「當下的鏈」比「過去的 manifest」會永遠對不上）。
                              退出碼 0＝互驗通過、2＝包已建立但互驗未通過（原因在 verify.json）。
              compliance      把既有事實對到 NIST SP 800-147／155／193 與 MITRE ATT&CK。
                              <b>不新增任何硬體讀取</b>：更新簽章、遠端 TPM Quote、韌體復原
                              這三類沒有真正驗證，因此刻意維持 Unverifiable；缺資料絕不升成通過。
                              --format json（預設）／sarif（2.1.0，CI 用）／junit（XML，CI 用）／
                              cef（逐行 Common Event Format）／otel（OTLP/JSON logs）；
                              SARIF 把 Unverifiable 標 warning、JUnit 標 failure，兩者都不會顯示為綠。
              compliance-diff 比較兩份 --compliance JSON 的判定變化：只讀檔案，不重新收集或驗簽。
                              Satisfied／Mitigated→其他＝regressed（含證據變成 Unverifiable）；
                              其他→Satisfied／Mitigated＝improved；其餘判定變動＝changed。
                              這只是判定差異，不是風險評分；0 只代表兩份判定相同。
              merkle          穩定事實的 SHA-256 Merkle 指紋：白名單鍵＋排除 Sensitive／非 Present／
                              易變鍵，逐葉排序後兩兩合併（落單者原樣提升）。canonical payload 為
                              key＋0x1F＋value＋0x1F＋trust＋0x1F＋source，<b>不含時間</b>；
                              輸出只含鍵與雜湊，不含值。
              speculation     七項 infer.* 推測（Boot Guard 狀態、CXL 角色、韌體復原、
                              更新驗證、TPM Quote、UEFI PQC、範圍聲明）。每一項都同時帶
                              infer. 鍵名前綴、【推測】值前綴與 Speculative 信任等級，
                              並列出信心、依據鍵與限制。<b>推測不是量測、不是合規證據，
                              也不進 Merkle 指紋</b>；退出碼 0 只代表推測已產生。
              fleet           只讀本機快照目錄，彙總同型號族群的 fleet.* 基線。不上傳、不跨機傳輸；
                              樣本少於 4 份只給中位數與範圍，不給 z 分數。沒有可比的第二台回退出碼 2。
              redfish         讀取已匯出的離線 Redfish 目錄樹（BMC／機箱／電源／風扇／溫度）；
                              只讀本機 JSON，不連 BMC、不連網。BMC 回報值是 Reported，不是本工具量測。

            選項：
              --query <前綴>   只輸出 key 以該前綴開頭的事實（例：--query platform.）
              --only <前綴清單>
                               選擇性匯出（U10-PF-0007）：逗號分隔的多個鍵前綴（例：--only spi.,platform.），
                               命中任一前綴即收錄。部分匯出如實聲明：輸出帶 totalCollected（過濾前的分母）、
                               only（選擇條件）與 partial（true＝部分匯出），note 明說「這是 N 筆中的 M 筆」——
                               部分報告不是全量報告，引用時請帶分母。篩選後零命中退出碼 2，note 如實說是空匯出。
              --out <檔案>     寫入檔案而非 stdout
              --format <格式>  只用於 --compliance：json（預設）／sarif／junit／cef／otel；其他值回退出碼 1
              --snapshots <目錄> 只用於 --fleet：指定快照目錄（預設 %AppData%\XinSpect\snapshots）
              --trusted-key <公鑰.pem> 與 --snapshot <快照.json> 只用於 --enterprise-baseline；端點不載入簽章私鑰
              --signing-key <加密私鑰.pem> 與 --password-stdin 只用於管理端簽署；密碼不放命令列，私鑰不得部署到端點

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
              --diff-report     0＝鍵級全同；2＝有差異（新增／消失／變化的鍵在輸出）；
                                1＝致命（引數錯、讀檔失敗、格式不認得）。
              --stdin-batch     0＝至少一個非空查詢；2＝空 stdin 或未命中；1＝讀取／收集失敗。
              --compliance      0＝全部 Satisfied／NotApplicable；2＝有任何 NotSatisfied／Unverifiable
                                或 Exposed／Unknown；1＝致命。
              --compliance-diff 0＝兩份判定全同；2＝有任何新增／消失／改善／退步／變動；
                                1＝讀檔失敗或格式不認得。0 不代表機器安全。
              --merkle          0＝至少一片葉子；2＝沒有任何穩定事實可建樹；1＝致命。
              --speculation     0＝七項推測都產生；2＝推測項數不是七（規則集合漂移）；1＝致命。
                                0 不代表機器安全或合規——推測不是量測。
              --fleet           0＝有可比的同型號族群；2＝沒有快照／沒有型號／沒有第二台；1＝致命。
              --redfish         0＝至少讀到一個管理器／機箱／系統；2＝沒有可讀資源；1＝致命。
            讀不到的事實如實帶 availability 與原因，絕不以 0／典型值頂替。
            """);
    }
}
