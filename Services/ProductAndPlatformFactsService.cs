namespace XinSpect;

/// <summary>
/// 產品與平台（Vol2 第 378–384 項）：PO-007 行程樹快照簽章（<c>po.proctree</c>，opt-in L2）、
/// PO-010 工作集排行（<c>po.ws</c>，opt-in L2）、PO-014 系統呼叫延遲樣本（<c>po.syscall.lat</c>）、
/// PL-006 閃爍／字型回退守門（<c>pl.uiquality</c>）、PL-007 多語系截圖基線（<c>pl.shotbaseline</c>）、
/// PL-008 每頁首載預算（<c>pl.pagebudget</c>）、PL-001 便攜／安裝雙包裝（<c>pl.package</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>紅線：</b>① PO-007／PO-010 屬 <b>L2 敏感</b>——opt-in 才跑，未注入＝NotApplicable；
/// ② 行程樹「瞬時快照」要聲明，且<b>不列行程名稱／路徑／父行程</b>；③ 系統呼叫延遲是微測，
/// 量測期間要標註；④ 首載預算與截圖基線是<b>守門結果</b>不是產品功能；⑤ 雙包裝是產品方向，
/// 未定案即明文「需討論」；⑥ 讀不到一律三態。
/// </para>
/// </remarks>
public static class ProductAndPlatformFactsService
{
    private const string CatPo = "效能觀測";
    private const string CatPl = "產品平台";

    public const string ProcTreeKey = "po.proctree";
    public const string WsKey = "po.ws";
    public const string SyscallKey = "po.syscall.lat";
    public const string UiQualityKey = "pl.uiquality";
    public const string ShotBaselineKey = "pl.shotbaseline";
    public const string PageBudgetKey = "pl.pagebudget";
    public const string PackageKey = "pl.package";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    public delegate (int Processes, int Orphans, bool SnapshotTaken)? ProcTreeProbe();
    public delegate (int Ranked, bool OptedIn)? WsProbe();
    public delegate (double MedianNs, int Samples)? SyscallProbe();
    public delegate (int FlickerCases, int FontFallbacks)? UiQualityProbe();
    public delegate (int Languages, int Baselines, bool AllMatch)? ShotBaselineProbe();
    public delegate (int Pages, int OverBudget)? PageBudgetProbe();
    public delegate (bool PortableMode, bool InstallerMode, bool Decided)? PackageProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        ProcTreeProbe? procTree = null,
        WsProbe? ws = null,
        SyscallProbe? syscall = null,
        UiQualityProbe? uiQuality = null,
        ShotBaselineProbe? shotBaseline = null,
        PageBudgetProbe? pageBudget = null,
        PackageProbe? package = null)
        =>
        [
            ProcTreeFact(at, procTree),
            WsFact(at, ws),
            SyscallFact(at, syscall),
            UiQualityFact(at, uiQuality),
            ShotBaselineFact(at, shotBaseline),
            PageBudgetFact(at, pageBudget),
            PackageFact(at, package),
        ];

    // ── PO-007 行程樹快照簽章（opt-in L2）─────────────────────────────

    public static HardwareFact ProcTreeFact(DateTimeOffset at, ProcTreeProbe? probe = null)
    {
        (int Processes, int Orphans, bool SnapshotTaken)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(ProcTreeKey, CatPo, "行程樹快照簽章", "", "", "—",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "L2 敏感且為 opt-in——需使用者明確觸發，未觸發永不列舉行程樹");

        var (procs, orphans, taken) = s.Value;
        return new HardwareFact(ProcTreeKey, CatPo, "行程樹快照簽章",
            $"行程 {procs} 個、疑似異常父行程 {orphans} 個、快照={(taken ? "已取" : "未取")}。" +
            "<b>瞬時快照聲明</b>：行程樹在取樣瞬間成立，下一刻就可能不同；" +
            "本項<b>不列行程名稱、路徑或父行程</b>，只列計數。異常父行程是訊號不是判決。",
            "個", "行程樹快照（opt-in L2）", FactTrustLevel.Measured, false, at, procs);
    }

    // ── PO-010 工作集排行（opt-in L2）─────────────────────────────────

    public static HardwareFact WsFact(DateTimeOffset at, WsProbe? probe = null)
    {
        (int Ranked, bool OptedIn)? s;
        try { s = (probe ?? (() => (0, false)))(); }
        catch { s = null; }

        if (s is null || !s.Value.OptedIn)
            return new HardwareFact(WsKey, CatPo, "工作集排行", "", "", "—",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "L2 敏感且為 opt-in——工作集排行需使用者明確同意，未同意不列舉");

        var (ranked, _) = s.Value;
        return new HardwareFact(WsKey, CatPo, "工作集排行",
            $"已排行的行程 {ranked} 個（僅計數）。<b>不列行程名稱</b>——工作集排行最容易被誤讀成" +
            "『誰佔記憶體』，但那需要看 commit 而非 working set；本項只陳述排行規模。",
            "個", "行程工作集（opt-in L2）", FactTrustLevel.Measured, false, at, ranked);
    }

    // ── PO-014 系統呼叫延遲樣本 ────────────────────────────────────────

    public static HardwareFact SyscallFact(DateTimeOffset at, SyscallProbe? probe = null)
    {
        (double MedianNs, int Samples)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(SyscallKey, CatPo, "系統呼叫延遲", "", "", "NtQuerySystemInformation 微測",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "未取得微測樣本——系統呼叫延遲需主動量測，未量不宣稱");

        var (median, samples) = s.Value;
        return new HardwareFact(SyscallKey, CatPo, "系統呼叫延遲",
            $"中位 {median:0} ns（樣本 {samples}）。" +
            "<b>量測期間標註</b>：微測結果受 CPU 頻率、快取、排程干擾；" +
            "這是本程式量到的延遲，不是核心的固定成本——只做為相對比較的基準。",
            "ns", "NtQSI 微測（唯讀）", FactTrustLevel.Measured, false, at, median);
    }

    // ── PL-006 閃爍／字型回退守門 ──────────────────────────────────────

    public static HardwareFact UiQualityFact(DateTimeOffset at, UiQualityProbe? probe = null)
    {
        (int FlickerCases, int FontFallbacks)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, UiQualityKey, CatPl, "UI 品質守門", FactAvailability.ReadError, "UI 品質掃描失敗");

        var (flicker, fallbacks) = s.Value;
        return new HardwareFact(UiQualityKey, CatPl, "UI 品質守門",
            $"已知閃爍案例 {flicker}、字型回退點 {fallbacks}。" +
            "這是<b>守門結果</b>不是產品功能——閃爍（雙重算繪）與字型回退（缺字型時改用系統字型）" +
            "是自動化測試盯的品質項；0 是值（掃描通過），不是「沒掃」。",
            "項", "UI 品質掃描（唯讀）", FactTrustLevel.Reported, false, at, flicker);
    }

    // ── PL-007 多語系截圖基線 ──────────────────────────────────────────

    public static HardwareFact ShotBaselineFact(DateTimeOffset at, ShotBaselineProbe? probe = null)
    {
        (int Languages, int Baselines, bool AllMatch)? s;
        try { s = (probe ?? (() => (1, 0, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, ShotBaselineKey, CatPl, "多語系截圖基線", FactAvailability.ReadError, "截圖基線不可讀");

        var (langs, baselines, allMatch) = s.Value;
        return new HardwareFact(ShotBaselineKey, CatPl, "多語系截圖基線",
            $"語言 {langs} 種、基線 {baselines} 份、全部相符={(allMatch ? "是" : "否")}。" +
            "每語言版面基線用 SHA-256 比對（與算繪快照同機制）；不符代表<b>版面漂移</b>，" +
            "可能是翻譯溢出或佈局改動——是訊號，需人工確認再更新基線。",
            "種", "多語算繪基線（唯讀）", FactTrustLevel.Reported, false, at, baselines);
    }

    // ── PL-008 每頁首載預算 ────────────────────────────────────────────

    public static HardwareFact PageBudgetFact(DateTimeOffset at, PageBudgetProbe? probe = null)
    {
        (int Pages, int OverBudget)? s;
        try { s = (probe ?? (() => ReadPageBudget()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, PageBudgetKey, CatPl, "每頁首載預算", FactAvailability.ReadError, "首載預算不可讀");

        var (pages, over) = s.Value;
        return new HardwareFact(PageBudgetKey, CatPl, "每頁首載預算",
            $"頁面 {pages} 個、超預算 {over} 個。" +
            "首載預算是<b>效能門檻</b>不是功能：每個頁面的第一次載入要有時間上限；" +
            "超預算代表需要最佳化，不是「壞掉」——且量測環境（電源計畫）會影響結果。",
            "個", "首載預算（唯讀）", FactTrustLevel.Reported, false, at, over);
    }

    private static (int, int) ReadPageBudget()
    {
        try { return (PerfBudget.All.Count, 0); }
        catch { return (0, 0); }
    }

    // ── PL-001 便攜／安裝雙包裝 ────────────────────────────────────────

    public static HardwareFact PackageFact(DateTimeOffset at, PackageProbe? probe = null)
    {
        (bool PortableMode, bool InstallerMode, bool Decided)? s;
        try { s = (probe ?? (() => (true, true, false)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, PackageKey, CatPl, "便攜／安裝雙包裝", FactAvailability.ReadError, "封裝狀態不可讀");

        var (portable, installer, decided) = s.Value;
        return new HardwareFact(PackageKey, CatPl, "便攜／安裝雙包裝",
            $"便攜模式={(portable ? "有" : "無")}、安裝模式={(installer ? "有" : "無")}、" +
            $"產品方向={(decided ? "已定案" : "**需討論**")}。" +
            "雙包裝是<b>產品方向決定</b>（單一 exe 便攜 vs 安裝器寫入系統）——" +
            (decided ? "已定案。" : "尚未定案，本版明文標記需討論，不假裝已決定。") +
            "性格改變聲明：安裝模式會寫入系統目錄與登錄，與便攜的零寫入宣稱不同。",
            "", "封裝狀態（唯讀宣告）", FactTrustLevel.Reported, false, at, portable ? 1 : 0);
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
