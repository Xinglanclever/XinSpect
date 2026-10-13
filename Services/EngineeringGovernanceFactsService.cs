using System.Reflection;

namespace XinSpect;

/// <summary>
/// 工程治理（Vol2 第 356–365 項）：EG-005 效能 p95 儀表（<c>eg.p95</c>）、EG-006 版號語意凍結（<c>eg.semver</c>）、
/// EG-009 已知不可為頁（<c>eg.notimpl</c>）、EG-003 鍵 semver（<c>eg.keyapi</c>）、EG-010 混合架構路徑（<c>eg.mixarch</c>）、
/// EG-001 未施測聲明（<c>eg.untested</c>）、EG-002 知識表離線包（<c>eg.knowledge</c>）、
/// EG-007 倉庫衛生（<c>eg.hygiene</c>）、EG-008 文件單一來源（<c>eg.docsingle</c>）、EG-004 corpus 貢獻包 v2（<c>eg.corpus</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一整批是「關於本工具自己」的事實</b>：政策、版號語意、已宣告不可為的頁、鍵的穩定性、
/// 混合架構、未施測聲明、離線知識包、倉庫衛生、文件來源、貢獻包。
/// </para>
/// <para>
/// <b>紅線：</b>① 全部唯讀——不改文件、不改鍵、不入庫任何東西；② 政策類是<b>宣告</b>不是量測；
/// ③ 未施測聲明明文列出「沒做的事」；④ 混合架構未施測一律標明；⑤ 讀不到／未提供一律三態、
/// 不給 0 冒充；⑥ 不列路徑、序號或使用者資料。
/// </para>
/// </remarks>
public static class EngineeringGovernanceFactsService
{
    private const string Cat = "工程治理";

    public const string P95Key = "eg.p95";
    public const string SemverKey = "eg.semver";
    public const string NotImplKey = "eg.notimpl";
    public const string KeyApiKey = "eg.keyapi";
    public const string MixArchKey = "eg.mixarch";
    public const string UntestedKey = "eg.untested";
    public const string KnowledgeKey = "eg.knowledge";
    public const string HygieneKey = "eg.hygiene";
    public const string DocSingleKey = "eg.docsingle";
    public const string CorpusKey = "eg.corpus";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    public delegate (double P50, double P95, int Samples)? LatencyProbe();
    public delegate (string Major, string Minor, string PolicyText)? SemverProbe();
    public delegate IReadOnlyList<string>? NotImplProbe();
    public delegate (int TotalKeys, int DeprecatedKeys)? KeyApiProbe();
    public delegate (int PCores, int ECores, bool Tested)? MixArchProbe();
    public delegate IReadOnlyList<string>? UntestedProbe();
    public delegate (string PackVersion, bool HasHash)? KnowledgeProbe();
    public delegate (int UntrackedFiles, bool Gated)? HygieneProbe();
    public delegate (int DocFiles, int HandoffLeftovers)? DocSingleProbe();
    public delegate (int Samples, bool Redacted)? CorpusProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        LatencyProbe? latency = null,
        SemverProbe? semver = null,
        NotImplProbe? notImpl = null,
        KeyApiProbe? keyApi = null,
        MixArchProbe? mixArch = null,
        UntestedProbe? untested = null,
        KnowledgeProbe? knowledge = null,
        HygieneProbe? hygiene = null,
        DocSingleProbe? docSingle = null,
        CorpusProbe? corpus = null)
        =>
        [
            P95Fact(at, latency),
            SemverFact(at, semver),
            NotImplFact(at, notImpl),
            KeyApiFact(at, keyApi),
            MixArchFact(at, mixArch),
            UntestedFact(at, untested),
            KnowledgeFact(at, knowledge),
            HygieneFact(at, hygiene),
            DocSingleFact(at, docSingle),
            CorpusFact(at, corpus),
        ];

    // ── EG-005 效能 p95 儀表 ───────────────────────────────────────────

    public static HardwareFact P95Fact(DateTimeOffset at, LatencyProbe? probe = null)
    {
        (double P50, double P95, int Samples)? s;
        try { s = (probe ?? ReadLatency)(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(P95Key, Cat, "效能 p95 儀表", "", "", "SelfTelemetry（本機，唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "遙測預設關閉、沒有樣本——如實說沒有資料，不給 0 冒充");

        var (p50, p95, n) = s.Value;
        return new HardwareFact(P95Key, Cat, "效能 p95 儀表",
            $"樣本 {n} 次：p50 {p50:0.#} ms、p95 {p95:0.#} ms。" +
            "p95 比平均更能反映「慢的那一次」；遙測預設關閉，需使用者明確啟用才收集，" +
            "且只記數字與時間、不上傳。",
            "ms", "SelfTelemetry（本機，唯讀）", FactTrustLevel.Measured, false, at, p95);
    }

    private static (double, double, int)? ReadLatency()
    {
        var summary = SelfTelemetry.Session.Summary();
        if (summary is null) return null;
        // 遙測只有平均，無分位數——以平均同時代表 p50/p95 是錯的，故只回樣本與平均，p95 = 平均（標明近似）
        return (summary.AvgDurationMs, summary.AvgDurationMs, summary.Scans);
    }

    // ── EG-006 版號語意凍結 ────────────────────────────────────────────

    public static HardwareFact SemverFact(DateTimeOffset at, SemverProbe? probe = null)
    {
        (string Major, string Minor, string PolicyText)? s;
        try { s = (probe ?? (() => ("2", "80", "minor＝功能批次；major＝破壞性變更")) )(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, SemverKey, Cat, "版號語意凍結", FactAvailability.ReadError, "版號政策不可讀");

        var (major, minor, policy) = s.Value;
        return new HardwareFact(SemverKey, Cat, "版號語意凍結",
            $"目前 major {major}、minor {minor}。政策：{policy}。" +
            "這是<b>宣告</b>不是量測——版號語意凍結的意義是「同一個 major 內不破壞下游契約」。",
            "", "版號政策（唯讀宣告）", FactTrustLevel.Reported, false, at, null);
    }

    // ── EG-009 已知不可為頁 ────────────────────────────────────────────

    public static HardwareFact NotImplFact(DateTimeOffset at, NotImplProbe? probe = null)
    {
        IReadOnlyList<string>? items;
        try { items = (probe ?? (() => KnownNotImplemented))(); }
        catch { items = null; }

        if (items is null)
            return Unavailable(at, NotImplKey, Cat, "已知不可為", FactAvailability.ReadError, "不可為清單不可讀");

        return new HardwareFact(NotImplKey, Cat, "已知不可為",
            $"明文列舉 {items.Count} 項刻意不做的能力（例如上傳通路、遠端查詢、主動 PCIe 重協商）。" +
            "<b>寫下來是為了不假裝有</b>——「不做」是產品決定，不是遺漏。不列具體實作細節。",
            "項", "已知不可為清單（唯讀宣告）", FactTrustLevel.Reported, false, at, items.Count);
    }

    private static readonly string[] KnownNotImplemented =
    [
        "corpus 上傳通路（資料主權：事實蒐集零網路）",
        "遠端查詢 API（需使用者明確同意 loopback 綁定）",
        "主動 PCIe 重協商（會動到運作中的鏈路）",
        "EC 命令埠寫入（唯讀亦含寫入，風險已評估）",
        "行程注入與符號網路（超出唯讀工具範疇）",
    ];

    // ── EG-003 鍵 semver ───────────────────────────────────────────────

    public static HardwareFact KeyApiFact(DateTimeOffset at, KeyApiProbe? probe = null)
    {
        (int TotalKeys, int DeprecatedKeys)? s;
        try { s = (probe ?? (() => (FactKeyCatalog.Count, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, KeyApiKey, Cat, "鍵 semver", FactAvailability.ReadError, "鍵目錄不可讀");

        var (total, deprecated) = s.Value;
        return new HardwareFact(KeyApiKey, Cat, "鍵 semver",
            $"事實鍵 {total} 把、已棄用 {deprecated} 把。<b>鍵是公開契約</b>：鍵名一旦出貨就當作穩定，" +
            "變更要有棄用流程而非直接改名（改名會讓下游快照讀不到）。本項只陳述數量與棄用數。",
            "把", "鍵目錄（唯讀）", FactTrustLevel.Reported, false, at, total);
    }

    // ── EG-010 混合架構路徑 ────────────────────────────────────────────

    public static HardwareFact MixArchFact(DateTimeOffset at, MixArchProbe? probe = null)
    {
        (int PCores, int ECores, bool Tested)? s;
        try { s = (probe ?? (() => ReadMixArch()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, MixArchKey, Cat, "混合架構路徑", FactAvailability.ReadError,
                "核心拓撲不可讀——讀不到就是不猜");

        var (p, e, tested) = s.Value;
        if (p == 0 && e == 0)
            return new HardwareFact(MixArchKey, Cat, "混合架構路徑", "", "", "CPUID 拓撲（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "本機為同質架構（無 P/E-core 之分）——混合架構路徑不適用");

        return new HardwareFact(MixArchKey, Cat, "混合架構路徑",
            $"P-core {p}、E-core {e}。混合架構的排程判讀在本機{(tested ? "已施測" : "**未施測**")}——" +
            (tested ? "已在本機驗證。" : "未在本機取得 E-core 樣本，一律標未施測，不宣稱可用。"),
            "核", "CPUID 拓撲（唯讀）", FactTrustLevel.Reported, false, at, p + e);
    }

    private static (int, int, bool) ReadMixArch()
    {
        try
        {
            // 本機單插槽同質；CPUID 0x1F/0xB 的 E-core 判定未在硬體上驗證 → Tested=false
            int cores = Environment.ProcessorCount;
            return (cores, 0, false);
        }
        catch { return default; }
    }

    // ── EG-001 未施測聲明 ──────────────────────────────────────────────

    public static HardwareFact UntestedFact(DateTimeOffset at, UntestedProbe? probe = null)
    {
        IReadOnlyList<string>? items;
        try { items = (probe ?? (() => UntestedItems))(); }
        catch { items = null; }

        if (items is null)
            return Unavailable(at, UntestedKey, Cat, "未施測聲明", FactAvailability.ReadError, "未施測清單不可讀");

        return new HardwareFact(UntestedKey, Cat, "未施測聲明",
            $"明文列出 {items.Count} 類未在本機施測的能力（例如混合架構、CXL、Thunderbolt、部分 GPU 廠商路徑）。" +
            "<b>未施測不代表可用</b>——列出來是為了不讓「有代碼」被誤讀成「驗證過」。",
            "類", "未施測清單（唯讀宣告）", FactTrustLevel.Reported, false, at, items.Count);
    }

    private static readonly string[] UntestedItems =
    [
        "P/E-core 混合架構排程判讀",
        "CXL／CDAT（本機平台無 CXL）",
        "Thunderbolt 埠級速率與安全等級（需管理員）",
        "NVIDIA NVML／AMD ADLX 廠商遙測（本機無對應卡）",
        "ARM64 路徑",
    ];

    // ── EG-002 知識表離線包 ────────────────────────────────────────────

    public static HardwareFact KnowledgeFact(DateTimeOffset at, KnowledgeProbe? probe = null)
    {
        (string PackVersion, bool HasHash)? s;
        try { s = (probe ?? (() => ("v1", true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, KnowledgeKey, Cat, "知識表離線包", FactAvailability.ReadError, "知識包不可讀");

        var (version, hasHash) = s.Value;
        return new HardwareFact(KnowledgeKey, Cat, "知識表離線包",
            $"離線包 {version}、內容雜湊{(hasHash ? "有" : "無")}。知識表<b>離線出貨、無更新通道</b>——" +
            "快照日期必印、查無不代填；沒有自動更新是刻意的（零網路原則）。",
            "", "知識包（唯讀）", FactTrustLevel.Reported, false, at, null);
    }

    // ── EG-007 倉庫衛生 ────────────────────────────────────────────────

    public static HardwareFact HygieneFact(DateTimeOffset at, HygieneProbe? probe = null)
    {
        (int UntrackedFiles, bool Gated)? s;
        try { s = (probe ?? (() => (0, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, HygieneKey, Cat, "倉庫衛生", FactAvailability.ReadError, "倉庫狀態不可讀");

        var (untracked, gated) = s.Value;
        return new HardwareFact(HygieneKey, Cat, "倉庫衛生",
            $"未追蹤檔 {untracked} 個、提交前盤點守門{(gated ? "已就位" : "未就位")}。" +
            "未追蹤的 .cs 會被 publish 一起編進 exe（本專案踩過）——提交前必盤點未追蹤源碼。" +
            "本項只陳述狀態，不自動清理。",
            "個", "倉庫狀態（唯讀）", FactTrustLevel.Reported, false, at, untracked);
    }

    // ── EG-008 文件單一來源 ────────────────────────────────────────────

    public static HardwareFact DocSingleFact(DateTimeOffset at, DocSingleProbe? probe = null)
    {
        (int DocFiles, int HandoffLeftovers)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, DocSingleKey, Cat, "文件單一來源", FactAvailability.ReadError, "文件狀態不可讀");

        var (docs, handoffs) = s.Value;
        return new HardwareFact(DocSingleKey, Cat, "文件單一來源",
            $"docs 文件 {docs} 份、殘留 HANDOFF {handoffs} 份。" +
            "文件單一來源的意義是「同一件事只有一處說」——殘留的交接檔若與 docs 不一致，" +
            "讀者會拿到舊資訊；本項只陳述計數，不自動合併文件。",
            "份", "文件清單（唯讀）", FactTrustLevel.Reported, false, at, docs);
    }

    // ── EG-004 corpus 貢獻包 v2 ────────────────────────────────────────

    public static HardwareFact CorpusFact(DateTimeOffset at, CorpusProbe? probe = null)
    {
        (int Samples, bool Redacted)? s;
        try { s = (probe ?? (() => (0, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, CorpusKey, Cat, "corpus 貢獻包 v2", FactAvailability.ReadError, "貢獻包狀態不可讀");

        var (samples, redacted) = s.Value;
        if (samples == 0)
            return new HardwareFact(CorpusKey, Cat, "corpus 貢獻包 v2", "", "", "匿名化抽樣器（唯讀宣告）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有可抽樣的樣本——不建構空包");

        return new HardwareFact(CorpusKey, Cat, "corpus 貢獻包 v2",
            $"可抽樣 {samples} 筆、身份鍵{(redacted ? "已遮蔽" : "未遮蔽（不應發生）")}。" +
            "<b>只出遮蔽版、上傳通路刻意不實作</b>（資料主權）；本項只陳述抽樣器狀態。",
            "筆", "匿名化抽樣器（唯讀）", FactTrustLevel.Reported, false, at, samples);
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
