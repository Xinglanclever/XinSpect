using System.Globalization;
using System.IO;

namespace XinSpect;

/// <summary>
/// 信任與報告事實（Vol2 推進第 57–66 項的 SE-004、RS-010、HM-007、ZR-005、DL-002）：
/// Named pipe 列舉、電子簽章報告（審計鏈頭）、硬體變更時間軸、Passkey 支援、行程傾摘同意閘門。
/// </summary>
/// <remarks>
/// <para><b>全部唯讀</b>：唯一涉及「動作」的是 DL-002 行程傾摘——它只宣告同意閘門狀態，
/// 未取得同意就永不執行（本版也不提供傾印實作）。</para>
/// <para><b>界線</b>：pipe 列舉不等於可連線（只列名、不開啟任何 pipe；名稱可能含使用者識別＝L1）；
/// 審計雜湊鏈可驗但不是數位簽章（不證明「誰」做的，也不防有權限的人整條重寫）；
/// 硬體變更只講兩份快照之間的差異，不是連續事件史；Passkey「支援」不等於「已設定」。</para>
/// <para><b>為什麼這一批沒有「更新失敗重試鏈」</b>：UF-002 的失敗／重試面<b>早已存在</b>
/// （<see cref="WindowsUpdateHistoryService"/> 的 wu.history.failed／30d／total／latest），
/// 本批改做他項，不重做第二份。</para>
/// </remarks>
public static class TrustOpsFactsService
{
    private const string CatSe = "進階安全";
    private const string CatRs = "報告輸出";
    private const string CatZr = "零信任";
    private const string CatDl = "開發者診斷";
    private const string CatHm = "硬體事件史";

    public const string PipeCountKey = "se.pipe.count";
    public const string PipeSummaryKey = "se.pipe.summary";
    public const string ReportSigKey = "rs.sig";
    public const string HwDiffKey = "hm.hwdiff";
    public const string PasskeyKey = "zr.passkey";
    public const string DumpKey = "dl.dump";

    private const string PipeSource = @"列舉 \\.\pipe\（只列名，不開啟）";

    // ── 注入接縫 ─────────────────────────────────────────────────────────

    /// <summary>pipe 名稱列舉；null＝列舉失敗（與「零條 pipe」意義完全不同）。</summary>
    public delegate IReadOnlyList<string>? PipeProbe();

    /// <summary>審計鏈狀態；null＝讀不到（與「鏈不存在」不同）。</summary>
    public sealed record AuditChainState(bool Exists, bool Valid, int Count, string? HeadHash);

    public delegate AuditChainState? AuditProbe();
    public delegate bool FileExistsProbe(string path);
    public delegate string? RegistryStringProbe(string valueName);
    public delegate bool ConsentProbe(string operation);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        PipeProbe? pipes = null,
        AuditProbe? audit = null,
        FileExistsProbe? fileExists = null,
        RegistryStringProbe? registry = null,
        ConsentProbe? consent = null,
        HardwareSnapshotDiff? snapshotDiff = null)
    {
        var facts = new List<HardwareFact>(6);
        facts.AddRange(PipeFacts(at, pipes ?? ReadPipes));
        facts.Add(ReportSignatureFact(at, (audit ?? ReadAuditChain)()));
        facts.Add(HwChangeFact(at, snapshotDiff));
        facts.Add(PasskeyFact(at, fileExists ?? File.Exists, registry ?? ReadRegistryString));
        facts.Add(DumpFact(at, consent ?? (_ => false)));
        return facts;
    }

    // ── SE-004 Named pipe 列舉 ──────────────────────────────────────────

    private static IReadOnlyList<HardwareFact> PipeFacts(DateTimeOffset at, PipeProbe probe)
    {
        IReadOnlyList<string>? names;
        try { names = probe(); }
        catch { names = null; }

        if (names is null)
        {
            const string why = "列舉 \\\\.\\pipe\\ 失敗——讀不到就是不猜（不讀成「沒有開放的 pipe」）";
            return
            [
                new HardwareFact(PipeCountKey, CatSe, "Named pipe 數", "", "", PipeSource,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, why),
                new HardwareFact(PipeSummaryKey, CatSe, "Named pipe 清單", "", "", PipeSource,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, why),
            ];
        }

        var ordered = names.Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return
        [
            new HardwareFact(PipeCountKey, CatSe, "Named pipe 數",
                ordered.Count.ToString(CultureInfo.InvariantCulture), "條", PipeSource,
                FactTrustLevel.Measured, false, at, ordered.Count),
            // 名稱可能含使用者／工作階段識別：L1，敏感旗標交給匯出遮蔽政策處理
            new HardwareFact(PipeSummaryKey, CatSe, "Named pipe 清單",
                ordered.Count == 0
                    ? "沒有列舉到 named pipe（0 是值，不是缺席）"
                    : string.Join("、", ordered.Take(12)) + (ordered.Count > 12 ? $"…等 {ordered.Count - 12} 條" : ""),
                "", PipeSource, FactTrustLevel.Measured, true, at, ordered.Count),
        ];
    }

    private static IReadOnlyList<string>? ReadPipes()
    {
        try
        {
            return Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName)
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
        }
        catch { return null; }
    }

    // ── RS-010 電子簽章報告（審計鏈頭）─────────────────────────────────

    private static HardwareFact ReportSignatureFact(DateTimeOffset at, AuditChainState? chain)
    {
        if (chain is null)
            return new HardwareFact(ReportSigKey, CatRs, "報告簽章（審計鏈）", "", "", "審計日誌（append-only、雜湊鏈）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "審計日誌讀不到——無法回答報告完整性（讀不到不等於沒被改過）");
        if (!chain.Exists)
            return new HardwareFact(ReportSigKey, CatRs, "報告簽章（審計鏈）", "", "", "審計日誌（append-only、雜湊鏈）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "還沒有審計日誌——沒有鏈頭可以引用（做過需要記錄的動作之後才會出現）");

        string head = string.IsNullOrWhiteSpace(chain.HeadHash) ? "（鏈頭未取得）" : chain.HeadHash!;
        string shortHead = head.Length > 16 ? head[..16] + "…" : head;
        return new HardwareFact(ReportSigKey, CatRs, "報告簽章（審計鏈）",
            $"審計鏈 {chain.Count} 筆・鏈頭 {shortHead}・驗證 {(chain.Valid ? "通過" : "失敗")}。" +
            "界線：這是雜湊鏈——能偵測日誌被改動，但不是數位簽章：不證明「誰」做的，也不防有權限的人整條重寫。",
            "筆", "審計日誌（append-only、雜湊鏈可驗）", FactTrustLevel.Derived, false, at, chain.Count,
            chain.Valid ? FactAvailability.Present : FactAvailability.Unknown,
            chain.Valid ? null : "審計鏈驗證失敗——日誌可能被改動或截斷，需人工檢視（不是「正常」）");
    }

    private static AuditChainState? ReadAuditChain()
    {
        try
        {
            string path = AuditLogService.DefaultPath;
            if (!File.Exists(path)) return new AuditChainState(false, false, 0, null);
            var verdict = AuditVerifier.VerifyFile(path);
            var log = AuditLogService.Load(path);
            return new AuditChainState(true, verdict.Valid, log.Count, log.Count > 0 ? log[^1].EntryHash : null);
        }
        catch { return null; }
    }

    // ── HM-007 硬體變更時間軸 ───────────────────────────────────────────

    /// <summary>把「上一次快照比較」攤成事實。沒比較過就明說沒比較過——不拿 0 冒充沒變。</summary>
    public static HardwareFact HwChangeFact(DateTimeOffset at, HardwareSnapshotDiff? diff)
    {
        if (diff is null)
            return new HardwareFact(HwDiffKey, CatHm, "硬體變更時間軸", "", "", "時間膠囊比較（上一份 vs 這一份）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "本次執行還沒做過快照比較——沒有變更可陳述（這不是「0 項變更」，也不是「硬體沒動過」）");
        if (!diff.IsSameMachine)
            return new HardwareFact(HwDiffKey, CatHm, "硬體變更時間軸", "", "", "時間膠囊比較（上一份 vs 這一份）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "兩份快照來自不同機器（匿名機識辨名不同）——跨機器比對不構成本機的變更史");

        var changed = diff.Changes.Where(c => c.Kind != SnapshotChangeKind.Unchanged)
            .Select(c => $"{c.Kind}：{c.Key}").OrderBy(s => s, StringComparer.Ordinal).ToList();
        return new HardwareFact(HwDiffKey, CatHm, "硬體變更時間軸",
            $"比較結果：新增 {diff.Added}・消失 {diff.Removed}・改變 {diff.Changed}・未變 {diff.Unchanged}" +
            (changed.Count > 0 ? "；前幾個：" + string.Join("、", changed.Take(8)) + (changed.Count > 8 ? $"…等 {changed.Count - 8} 項" : "") : "") +
            "。界線：這是兩份快照之間的差異，不是連續事件史——兩次之間發生過什麼、先後如何都不在宣稱範圍。",
            "項", "時間膠囊比較（上一份 vs 這一份）", FactTrustLevel.Derived, false, at,
            diff.Added + diff.Removed + diff.Changed);
    }

    // ── ZR-005 Passkey 支援 ─────────────────────────────────────────────

    private static HardwareFact PasskeyFact(DateTimeOffset at, FileExistsProbe fileExists, RegistryStringProbe registry)
    {
        bool webauthn;
        try { webauthn = fileExists(Path.Combine(Environment.SystemDirectory, "webauthn.dll")); }
        catch { webauthn = false; }

        string? build = registry("CurrentBuildNumber");
        bool buildOk = int.TryParse(build, NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) && b >= 22621;

        if (!webauthn)
            return new HardwareFact(PasskeyKey, CatZr, "Passkey 支援", "", "", "webauthn.dll＋CurrentBuildNumber",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                @"System32\webauthn.dll 不存在——這台機器沒有 WebAuthn／Passkey 執行環境");

        return new HardwareFact(PasskeyKey, CatZr, "Passkey 支援",
            "WebAuthn 執行環境存在（webauthn.dll）" +
            (build is null
                ? "；組建號讀不到——無法確認版本門檻（不猜）"
                : buildOk ? $"；build {build} 達 Windows 11 22H2（22621）門檻"
                          : $"；build {build} 低於 22621，不在 Passkey 支援的版本範圍") +
            "。界線：支援不等於已設定——有沒有註冊任何 passkey 要看 Windows Hello／帳戶設定，本工具不讀憑證內容。",
            "", @"webauthn.dll＋HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            FactTrustLevel.Reported, false, at);
    }

    private static string? ReadRegistryString(string valueName) => valueName switch
    {
        "CurrentBuildNumber" => Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", null) as string,
        _ => null,
    };

    // ── DL-002 行程傾摘同意閘門 ─────────────────────────────────────────

    private static HardwareFact DumpFact(DateTimeOffset at, ConsentProbe consent)
    {
        bool granted;
        try { granted = consent("process-dump"); }
        catch { granted = false; }

        return granted
            ? new HardwareFact(DumpKey, CatDl, "行程傾摘（同意閘門）", "", "", "同意閘門狀態",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "已取得同意，但本版沒有傾印實作——沒有做的事不假裝做過")
            : new HardwareFact(DumpKey, CatDl, "行程傾摘（同意閘門）", "", "", "同意閘門狀態",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "未取得使用者同意——傾印屬 L2 動作，未同意則永不執行（本項只宣告閘門狀態，不碰任何行程記憶體）");
    }
}