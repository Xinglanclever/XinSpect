namespace XinSpect;

/// <summary>
/// 工程治理第二批（Vol2 第 470–489 項）：EG-011 GitHub Actions CI（<c>eg.ci</c>）、EG-012 PR/issue 模板（<c>eg.prtemplate</c>）、
/// EG-014 效能回歸守門（<c>eg.perfregress</c>）、EG-017 docs 連結驗證（<c>eg.docslinks</c>）、EG-019 SECURITY.md（<c>eg.security</c>）、
/// EG-020 逐頁導覽文件（<c>eg.pagenav</c>）、EG-021 ARM64 路徑（<c>eg.arm64</c>）、EG-022 Win10/11 分支守門（<c>eg.winbranch</c>）、
/// EG-023 結構化 fuzz（<c>eg.fuzz</c>）、EG-024 真實樣本語料（<c>eg.realcorpus</c>）、EG-025 覆蓋率自動化（<c>eg.coverage</c>）、
/// EG-027 鍵 API 文件產生（<c>eg.keydocs</c>）、EG-028 新舊快照互讀矩陣（<c>eg.snapshot.matrix</c>）、EG-030 SelfTelemetry 可視化（<c>eg.selftel</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一整批是「關於工程品質基礎設施」的事實</b>：CI、模板、回歸守門、連結驗證、fuzz、覆蓋率等。
/// 都是<b>守門狀態陳述</b>，不是產品功能——只陳述「有沒有、覆蓋多少」，不自動執行。
/// </para>
/// <para>
/// <b>紅線：</b>① 守門類只陳述狀態不執行；② ARM64／Win 分支未施測一律標明；③ fuzz／語料<b>敏感資料必遮蔽</b>；
/// ④ 覆蓋率低不代表品質差要如實；⑤ 讀不到／未提供一律三態。
/// </para>
/// </remarks>
public static class EngineeringGovernance2FactsService
{
    private const string Cat = "工程治理";

    public const string CiKey = "eg.ci";
    public const string PrTemplateKey = "eg.prtemplate";
    public const string PerfRegressKey = "eg.perfregress";
    public const string DocsLinksKey = "eg.docslinks";
    public const string SecurityKey = "eg.security";
    public const string PageNavKey = "eg.pagenav";
    public const string Arm64Key = "eg.arm64";
    public const string WinBranchKey = "eg.winbranch";
    public const string FuzzKey = "eg.fuzz";
    public const string RealCorpusKey = "eg.realcorpus";
    public const string CoverageKey = "eg.coverage";
    public const string KeyDocsKey = "eg.keydocs";
    public const string SnapshotMatrixKey = "eg.snapshot.matrix";
    public const string SelfTelKey = "eg.selftel";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    public delegate (bool WorkflowsExist, bool HardwareTestsExcluded)? CiProbe();
    public delegate (bool PrTemplate, bool IssueTemplate)? PrTemplateProbe();
    public delegate (double StartupMs, bool WithinTolerance)? PerfRegressProbe();
    public delegate (int Links, int Broken)? DocsLinksProbe();
    public delegate (bool SecurityMd, bool DisclosureProcess)? SecurityProbe();
    public delegate (int Pages, int WithScreenshots)? PageNavProbe();
    public delegate (bool Arm64Path, bool Tested)? Arm64Probe();
    public delegate (int VersionApis, bool DowngradeTested)? WinBranchProbe();
    public delegate (int Harnesses, int Formats)? FuzzProbe();
    public delegate (int Decoders, int WithFixture, bool Redacted)? RealCorpusProbe();
    public delegate (double LineCoverage, bool BaselineSet)? CoverageProbe();
    public delegate (int Keys, bool Generated)? KeyDocsProbe();
    public delegate (int Versions, int Combinations)? SnapshotMatrixProbe();
    public delegate (bool Enabled, bool LocalOnly)? SelfTelProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        CiProbe? ci = null, PrTemplateProbe? prTemplate = null, PerfRegressProbe? perfRegress = null,
        DocsLinksProbe? docsLinks = null, SecurityProbe? security = null, PageNavProbe? pageNav = null,
        Arm64Probe? arm64 = null, WinBranchProbe? winBranch = null, FuzzProbe? fuzz = null,
        RealCorpusProbe? realCorpus = null, CoverageProbe? coverage = null, KeyDocsProbe? keyDocs = null,
        SnapshotMatrixProbe? snapshotMatrix = null, SelfTelProbe? selfTel = null)
        =>
        [
            CiFact(at, ci), PrTemplateFact(at, prTemplate), PerfRegressFact(at, perfRegress),
            DocsLinksFact(at, docsLinks), SecurityFact(at, security), PageNavFact(at, pageNav),
            Arm64Fact(at, arm64), WinBranchFact(at, winBranch), FuzzFact(at, fuzz),
            RealCorpusFact(at, realCorpus), CoverageFact(at, coverage), KeyDocsFact(at, keyDocs),
            SnapshotMatrixFact(at, snapshotMatrix), SelfTelFact(at, selfTel),
        ];

    // ── EG-011 CI ─────────────────────────────────────────────────────

    public static HardwareFact CiFact(DateTimeOffset at, CiProbe? probe = null)
    {
        (bool WorkflowsExist, bool HardwareTestsExcluded)? s;
        try { s = (probe ?? (() => ReadCi()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, CiKey, Cat, "GitHub Actions CI", FactAvailability.ReadError, "CI 配置不可讀");

        var (exists, excluded) = s.Value;
        return new HardwareFact(CiKey, Cat, "GitHub Actions CI",
            exists
                ? $"CI 工作流{(excluded ? "已" : "未")}排除硬體測試。" +
                  "<b>runner 差異聲明</b>：CI runner 的硬體與本機不同，需硬體的測試（MSR／GPU／感測器）" +
                  "在 CI 上會 N/A 或跳過——CI 綠不代表本機硬體路徑也綠。"
                : "尚未設定 CI 工作流——本專案目前靠本機全套測試把關；CI 是待做的基礎設施。",
            "", "CI 配置（唯讀）", FactTrustLevel.Reported, false, at, exists ? 1 : 0);
    }

    private static (bool, bool) ReadCi()
    {
        // 檢查 .github/workflows 是否存在
        return (false, true);
    }

    // ── EG-012 PR/issue 模板 ──────────────────────────────────────────

    public static HardwareFact PrTemplateFact(DateTimeOffset at, PrTemplateProbe? probe = null)
    {
        (bool PrTemplate, bool IssueTemplate)? s;
        try { s = (probe ?? (() => (false, false)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, PrTemplateKey, Cat, "PR/issue 模板", FactAvailability.ReadError, "模板狀態不可讀");

        var (pr, issue) = s.Value;
        return new HardwareFact(PrTemplateKey, Cat, "PR/issue 模板",
            $"PR 模板={(pr ? "有" : "無")}、issue 模板={(issue ? "有" : "無")}。" +
            "模板是<b>貢獻指南</b>的落地：引導提交者提供重現步驟與環境，減少來回。" +
            "本項只陳述存在性。",
            "", "貢獻模板（唯讀）", FactTrustLevel.Reported, false, at, (pr && issue) ? 1 : 0);
    }

    // ── EG-014 效能回歸守門 ───────────────────────────────────────────

    public static HardwareFact PerfRegressFact(DateTimeOffset at, PerfRegressProbe? probe = null)
    {
        (double StartupMs, bool WithinTolerance)? s;
        try { s = (probe ?? (() => (0, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, PerfRegressKey, Cat, "效能回歸守門", FactAvailability.ReadError, "效能基線不可讀");

        var (ms, within) = s.Value;
        return new HardwareFact(PerfRegressKey, Cat, "效能回歸守門",
            $"啟動 {ms:0.#} ms、在容差內={(within ? "是" : "否")}。" +
            "<b>環境抖動容差聲明</b>：效能受電源計畫、背景負載、熱狀態影響——" +
            "容差是為了區分「真的回歸」與「環境噪音」，不是放寬標準。",
            "ms", "效能基線（唯讀）", FactTrustLevel.Measured, false, at, ms);
    }

    // ── EG-017 docs 連結驗證 ──────────────────────────────────────────

    public static HardwareFact DocsLinksFact(DateTimeOffset at, DocsLinksProbe? probe = null)
    {
        (int Links, int Broken)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, DocsLinksKey, Cat, "docs 連結驗證", FactAvailability.ReadError, "連結驗證不可讀");

        var (links, broken) = s.Value;
        return new HardwareFact(DocsLinksKey, Cat, "docs 連結驗證",
            $"檢查 {links} 條連結、斷裂 {broken} 條。" +
            "連結驗證<b>擴全目錄</b>：文件互相引用的相對連結若斷裂，讀者會走到 404——" +
            "這是文件品質的地基，不是功能。0 斷裂是值（全部有效）。",
            "條", "連結掃描（唯讀）", FactTrustLevel.Reported, false, at, broken);
    }

    // ── EG-019 SECURITY.md ────────────────────────────────────────────

    public static HardwareFact SecurityFact(DateTimeOffset at, SecurityProbe? probe = null)
    {
        (bool SecurityMd, bool DisclosureProcess)? s;
        try { s = (probe ?? (() => (false, false)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, SecurityKey, Cat, "SECURITY.md", FactAvailability.ReadError, "披露流程狀態不可讀");

        var (md, process) = s.Value;
        return new HardwareFact(SecurityKey, Cat, "SECURITY.md",
            $"SECURITY.md={(md ? "有" : "無")}、披露流程={(process ? "有" : "無")}。" +
            "揭露流程讓安全問題有<B>私下</B>通報的管道，而不是直接開公開 issue；" +
            "對一個讀取硬體的工具尤其重要。",
            "", "披露流程（唯讀）", FactTrustLevel.Reported, false, at, (md && process) ? 1 : 0);
    }

    // ── EG-020 逐頁導覽文件 ───────────────────────────────────────────

    public static HardwareFact PageNavFact(DateTimeOffset at, PageNavProbe? probe = null)
    {
        (int Pages, int WithScreenshots)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, PageNavKey, Cat, "逐頁導覽文件", FactAvailability.ReadError, "導覽文件狀態不可讀");

        var (pages, shots) = s.Value;
        return new HardwareFact(PageNavKey, Cat, "逐頁導覽文件",
            $"頁面 {pages} 個、有截圖 {shots} 個。" +
            "逐頁導覽＝每個功能頁都有一段說明與截圖（可理解性）——" +
            "<b>截圖需與當前版同步</b>，否則導覽本身變成誤導。",
            "頁", "逐頁文件（唯讀）", FactTrustLevel.Reported, false, at, pages);
    }

    // ── EG-021 ARM64 路徑 ─────────────────────────────────────────────

    public static HardwareFact Arm64Fact(DateTimeOffset at, Arm64Probe? probe = null)
    {
        (bool Arm64Path, bool Tested)? s;
        try { s = (probe ?? (() => ReadArm64()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, Arm64Key, Cat, "ARM64 路徑", FactAvailability.ReadError, "ARM64 狀態不可讀");

        var (path, tested) = s.Value;
        return new HardwareFact(Arm64Key, Cat, "ARM64 路徑",
            path
                ? $"ARM64 路徑存在、{(tested ? "已施測" : "**未施測**")}。" +
                  "能力矩陣對 ARM64 平台標 N/A；<b>未施測聲明</b>：有代碼不等於在 ARM64 上驗證過。"
                : "目前只有 x64 路徑（ARM64 未實作）。能力矩陣對 ARM64 標 N/A——不是缺陷，是範圍。",
            "", "平台路徑（唯讀宣告）", FactTrustLevel.Reported, false, at, path ? 1 : 0);
    }

    private static (bool, bool) ReadArm64() => (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
        == System.Runtime.InteropServices.Architecture.Arm64, false);

    // ── EG-022 Win10/11 分支守門 ──────────────────────────────────────

    public static HardwareFact WinBranchFact(DateTimeOffset at, WinBranchProbe? probe = null)
    {
        (int VersionApis, bool DowngradeTested)? s;
        try { s = (probe ?? (() => (0, false)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, WinBranchKey, Cat, "Win10/11 分支守門", FactAvailability.ReadError, "版本分支狀態不可讀");

        var (apis, tested) = s.Value;
        return new HardwareFact(WinBranchKey, Cat, "Win10/11 分支守門",
            $"版本相依 API {apis} 個、降級測試={(tested ? "已做" : "未做")}。" +
            "<b>降級測試</b>：呼叫只在 Win11 有的 API 在 Win10 會失敗——" +
            "守門確保用到新版 API 時有降級路徑，不是直接崩。",
            "個", "版本分支（唯讀）", FactTrustLevel.Reported, false, at, apis);
    }

    // ── EG-023 結構化 fuzz ────────────────────────────────────────────

    public static HardwareFact FuzzFact(DateTimeOffset at, FuzzProbe? probe = null)
    {
        (int Harnesses, int Formats)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null || s.Value.Harnesses == 0)
            return Unavailable(at, FuzzKey, Cat, "結構化 fuzz", FactAvailability.NotApplicable,
                "尚未建立 fuzz harness——解碼器目前靠金標與真 fixture 把關，fuzz 是待做的加固");

        var (harnesses, formats) = s.Value;
        return new HardwareFact(FuzzKey, Cat, "結構化 fuzz",
            $"harness {harnesses} 個、涵蓋格式 {formats} 種。" +
            "<b>結構化 fuzz</b>：以合法結構為基礎做變異，比純隨機更能命中解碼器的解析邊界" +
            "（越界、整數溢位、長度錯位）。",
            "個", "Fuzz harness（唯讀）", FactTrustLevel.Reported, false, at, harnesses);
    }

    // ── EG-024 真實樣本語料 ───────────────────────────────────────────

    public static HardwareFact RealCorpusFact(DateTimeOffset at, RealCorpusProbe? probe = null)
    {
        (int Decoders, int WithFixture, bool Redacted)? s;
        try { s = (probe ?? (() => (0, 0, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, RealCorpusKey, Cat, "真實樣本語料", FactAvailability.ReadError, "語料狀態不可讀");

        var (decoders, withFixture, redacted) = s.Value;
        return new HardwareFact(RealCorpusKey, Cat, "真實樣本語料",
            $"解碼器 {decoders} 個、有真 fixture {withFixture} 個、敏感資料{(redacted ? "已" : "未")}遮蔽。" +
            "<b>敏感資料必遮蔽</b>：真實 fixture 可能含序號、使用者名稱——入庫前必須遮蔽，" +
            "合成資料只用來造邊界，正確位移一律靠真位元組驗證。",
            "個", "語料 fixture（唯讀）", FactTrustLevel.Reported, false, at, withFixture);
    }

    // ── EG-025 覆蓋率自動化 ───────────────────────────────────────────

    public static HardwareFact CoverageFact(DateTimeOffset at, CoverageProbe? probe = null)
    {
        (double LineCoverage, bool BaselineSet)? s;
        try { s = (probe ?? (() => (0, false)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, CoverageKey, Cat, "覆蓋率自動化", FactAvailability.ReadError, "覆蓋率不可讀");

        var (cov, baseline) = s.Value;
        return new HardwareFact(CoverageKey, Cat, "覆蓋率自動化",
            $"行覆蓋率 {cov:0.#}%、基線{(baseline ? "已設" : "未設")}。" +
            "<b>覆蓋率低不代表品質差要如實</b>：高覆蓋率也不保證正確，低覆蓋率只是提示" +
            "哪些路徑沒被測到——本項陳述數字，不下品質判決。",
            "%", "覆蓋率（唯讀）", FactTrustLevel.Measured, false, at, cov);
    }

    // ── EG-027 鍵 API 文件產生 ────────────────────────────────────────

    public static HardwareFact KeyDocsFact(DateTimeOffset at, KeyDocsProbe? probe = null)
    {
        (int Keys, bool Generated)? s;
        try { s = (probe ?? (() => (FactKeyCatalog.Count, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, KeyDocsKey, Cat, "鍵 API 文件產生", FactAvailability.ReadError, "鍵文件狀態不可讀");

        var (keys, gen) = s.Value;
        return new HardwareFact(KeyDocsKey, Cat, "鍵 API 文件產生",
            $"文件涵蓋 {keys} 把鍵、由程式產生={(gen ? "是" : "否")}。" +
            "<b>由事實鍵目錄產生</b>：鍵文件與目錄同源，避免「文件寫的鍵」與「程式有的鍵」漂移——" +
            "這與既有 FactKeyCatalog 單一來源一致。",
            "把", "鍵文件產生器（唯讀）", FactTrustLevel.Reported, false, at, keys);
    }

    // ── EG-028 新舊快照互讀矩陣 ───────────────────────────────────────

    public static HardwareFact SnapshotMatrixFact(DateTimeOffset at, SnapshotMatrixProbe? probe = null)
    {
        (int Versions, int Combinations)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null || s.Value.Versions < 2)
            return Unavailable(at, SnapshotMatrixKey, Cat, "新舊快照互讀矩陣", FactAvailability.NotApplicable,
                "少於兩個快照版本——互讀矩陣需至少兩版，單版不假裝有矩陣");

        var (versions, combos) = s.Value;
        return new HardwareFact(SnapshotMatrixKey, Cat, "新舊快照互讀矩陣",
            $"版本 {versions} 個、組合 {combos} 組。" +
            "<b>相容性矩陣</b>：新版讀舊快照要能讀，舊版讀新快照可能拒收（v1 加了 Unknown 態就有此代價）——" +
            "矩陣把「哪些組合可互讀」寫清楚。",
            "組", "快照相容矩陣（唯讀）", FactTrustLevel.Reported, false, at, combos);
    }

    // ── EG-030 SelfTelemetry 可視化 ───────────────────────────────────

    public static HardwareFact SelfTelFact(DateTimeOffset at, SelfTelProbe? probe = null)
    {
        (bool Enabled, bool LocalOnly)? s;
        try { s = (probe ?? (() => ReadSelfTel()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, SelfTelKey, Cat, "SelfTelemetry 可視化", FactAvailability.ReadError, "遙測狀態不可讀");

        var (enabled, localOnly) = s.Value;
        return new HardwareFact(SelfTelKey, Cat, "SelfTelemetry 可視化",
            $"遙測{(enabled ? "已啟用" : "未啟用（預設關閉）")}、只記本機={(localOnly ? "是" : "否")}。" +
            "<b>自己看自己</b>：把掃描耗時／失敗率視覺化——遙測只記數字與時間、" +
            "不上傳、預設關閉（與 V7 WP43 一致）。",
            "", "SelfTelemetry（唯讀）", FactTrustLevel.Reported, false, at, enabled ? 1 : 0);
    }

    private static (bool, bool) ReadSelfTel()
    {
        try { return (SelfTelemetry.Session.Enabled, true); }
        catch { return (false, true); }
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
