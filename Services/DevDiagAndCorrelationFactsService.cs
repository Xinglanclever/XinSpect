using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 開發者診斷補完（Vol2 第 DL-006 逐行程 DLL 清單、DL-007 句柄洩漏哨兵項）與
/// 基線關聯二（BL-006 相關性探索、BL-009 負載模式分類）。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼把這四項放一起：</b>它們共用同一個模型——「長期取樣序列 ＋ 穩健統計」。
/// DL-006 是「誰載了什麼」，DL-007 是本行程 handle 隨時間的斜率，BL-006 是兩個序列的
/// 相關性，BL-009 是負載樣本的分群。四項都只依賴注入的樣本，不主動去碰別的行程。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① 逐行程 DLL 清單屬 <b>L2</b>，只在明示同意下取樣，且只列<b>計數與判定</b>，
///    逐條明細放在 <c>dl.dll.{行程}</c> 家族鍵裡並在匯出時遮蔽路徑；
/// ② 句柄斜率是<b>訊號不是判決</b>——單調上升可能只是取樣期間正在載入快取；
/// ③ 相關性是<b>觀察不是因果</b>（CorrelationShift 的教訓：兩條各自單調的序列相關係數趨近 ±1）；
/// ④ 聚類是<b>推測級聲明</b>——把樣本分群不等於知道使用者在做什麼。
/// </para>
/// </remarks>
public static class DevDiagAndCorrelationFactsService
{
    private const string CatDl = "開發者診斷";
    private const string CatBl = "基線學習";

    // ── 鍵 ────────────────────────────────────────────────────────────────

    /// <summary>DL-006 逐行程 DLL 清單（彙總）。</summary>
    public const string PerProcessDllKey = "dl.dll.summary";
    /// <summary>DL-006 逐條家族鍵前綴（<c>dl.dll.{行程名}</c>）。</summary>
    public const string PerProcessDllPrefix = "dl.dll.";
    /// <summary>DL-007 句柄洩漏哨兵。</summary>
    public const string HandleLeakKey = "dl.leak.summary";
    /// <summary>BL-006 相關性探索。</summary>
    public const string CorrelationKey = "bl.corr.summary";
    /// <summary>BL-009 負載模式分類。</summary>
    public const string PatternKey = "bl.pattern";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>一個行程的模組負載：行程名、模組數、未簽章數、無法驗證數。</summary>
    public sealed record ProcessModules(string Process, int Modules, int Unsigned, int Unverifiable);

    /// <summary>（本行程 handle 數序列, 每個取樣點之間的分鐘數）。</summary>
    public sealed record HandleSeries(IReadOnlyList<int> Counts, double MinutesPerStep);

    /// <summary>（序列 A, 序列 B, 兩序列的名稱, 每點分鐘數）——BL-006 的兩個指標。</summary>
    public sealed record PairSeries(IReadOnlyList<double> A, IReadOnlyList<double> B, string NameA, string NameB, double MinutesPerStep);

    /// <summary>（負載樣本, 分群數, 各群樣本數, 各群中心值）——BL-009 的聚類結果。</summary>
    public sealed record PatternState(IReadOnlyList<double> Samples, int Clusters, IReadOnlyList<int> Sizes, IReadOnlyList<double> Centers);

    public delegate IReadOnlyList<ProcessModules>? ProcessModulesProbe();
    public delegate HandleSeries? HandleProbe();
    public delegate PairSeries? PairProbe();
    public delegate PatternState? PatternProbe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        ProcessModulesProbe? processes = null,
        HandleProbe? handles = null,
        PairProbe? pair = null,
        PatternProbe? pattern = null)
        =>
        [
            PerProcessDllFact(at, processes),
            HandleLeakFact(at, handles),
            CorrelationFact(at, pair),
            PatternFact(at, pattern),
        ];

    // ── DL-006 逐行程 DLL 清單 ────────────────────────────────────────────

    public static HardwareFact PerProcessDllFact(DateTimeOffset at, ProcessModulesProbe? probe = null)
    {
        IReadOnlyList<ProcessModules>? rows;
        try { rows = (probe ?? (() => null))(); }
        catch { rows = null; }

        if (rows is null)
            return Unavailable(at, PerProcessDllKey, CatDl, "逐行程 DLL 清單", FactAvailability.NotApplicable,
                "未取得逐行程模組列舉的明示同意——L2 動作，未同意則永不執行（本項只宣告閘門狀態）");

        if (rows.Count == 0)
            return Unavailable(at, PerProcessDllKey, CatDl, "逐行程 DLL 清單", FactAvailability.NotApplicable,
                "沒有可列舉的行程——不適用，不是錯誤");

        int totalModules = rows.Sum(r => r.Modules);
        int totalUnsigned = rows.Sum(r => r.Unsigned);
        int flagged = rows.Count(r => r.Unsigned > 0 || r.Unverifiable > 0);
        string top = string.Join("；", rows.OrderByDescending(r => r.Unsigned).Take(3)
            .Select(r => $"{r.Process}（{r.Modules} 模組・未簽章 {r.Unsigned}）"));

        return new HardwareFact(PerProcessDllKey, CatDl, "逐行程 DLL 清單",
            $"行程 {rows.Count} 個・模組合計 {totalModules} 個・未簽章 {totalUnsigned} 個・有可疑項的行程 {flagged} 個。" +
            $"而未簽章最多的：{top}。" +
            "<b>L2 且是聚合視角</b>：這是「哪些行程載了沒簽章的東西」的清單，不是「哪些行程有問題」——" +
            "開發工具、外掛、即時編譯產生的模組本來就不會簽章。" +
            $"逐行程明細見 <c>{PerProcessDllPrefix}<行程名></c>；匯出時會遮蔽模組路徑。",
            "個", "Process.Modules ＋ Authenticode（L2，需明示同意）", FactTrustLevel.Reported, false, at, rows.Count);
    }

    /// <summary>逐行程的明細鍵（供呼叫端在已同意的情況下展開成家族鍵）。</summary>
    public static IEnumerable<HardwareFact> PerProcessDllEntries(DateTimeOffset at, IReadOnlyList<ProcessModules> rows)
    {
        foreach (var r in rows)
        {
            yield return new HardwareFact(PerProcessDllPrefix + r.Process, CatDl,
                $"行程模組・{r.Process}",
                $"模組 {r.Modules} 個・未簽章 {r.Unsigned} 個・無法驗證 {r.Unverifiable} 個。" +
                "只列計數與判定，不列模組路徑與名稱（路徑可能含使用者名稱）；" +
                "未簽章不是惡意判決，只陳述事實。",
                "個", "Process.Modules ＋ Authenticode（L2）", FactTrustLevel.Reported, false, at, r.Modules);
        }
    }

    // ── DL-007 句柄洩漏哨兵 ───────────────────────────────────────────────

    public static HardwareFact HandleLeakFact(DateTimeOffset at, HandleProbe? probe = null)
    {
        HandleSeries? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || s.Counts.Count < TrendSentinel.MinSamplesForTrend)
            return Unavailable(at, HandleLeakKey, CatDl, "句柄洩漏哨兵", FactAvailability.NotApplicable,
                s is null
                    ? "沒有句柄取樣序列——不適用（本項只在本行程長期執行時才有意義）"
                    : $"句柄樣本少於 {TrendSentinel.MinSamplesForTrend} 點——樣本不足看不出斜率，不宣稱洩漏");

        var values = s.Counts.Select(c => (double)c).ToList();
        var est = TrendSentinel.Slope(values);
        double perHour = TrendSentinelService.PerHour(est.SlopePerStep) * (5.0 / s.MinutesPerStep);
        string verdict = est.SlopeExcludesZero && est.SlopePerStep > 0
            ? $"信賴區間不含零且為正（{est.LowSlope:0.###}–{est.HighSlope:0.###}／步）——上升趨勢站得住腳"
            : est.SlopeExcludesZero
                ? "信賴區間不含零但為負——句柄數在下降（結束時釋放）"
                : "信賴區間含零——這串起伏還看不出單調趨勢";

        return new HardwareFact(HandleLeakKey, CatDl, "句柄洩漏哨兵",
            $"樣本 {s.Counts.Count} 點（{s.MinutesPerStep:0.#} 分／步）・最新 {s.Counts[^1]} 個・" +
            $"斜率 {est.SlopePerStep:0.####}／步（約 {perHour:0.#}／小時）。{verdict}。" +
            "<b>訊號不是判決</b>：單調上升可能只是取樣期間正在載入快取或建立連線池；" +
            "真正的洩漏要跨更長時間、且與工作量脫鉤才談得上——本項只把斜率與區間攤開。",
            "個", "GetProcessHandleCount（本行程，唯讀）", FactTrustLevel.Derived, false, at, s.Counts[^1]);
    }

    // ── BL-006 相關性探索 ─────────────────────────────────────────────────

    public static HardwareFact CorrelationFact(DateTimeOffset at, PairProbe? probe = null)
    {
        PairSeries? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        int n = s is null ? 0 : Math.Min(s.A.Count, s.B.Count);
        if (s is null || n < TrendSentinelService.MinCorrelationPoints)
            return Unavailable(at, CorrelationKey, CatBl, "相關性探索", FactAvailability.NotApplicable,
                s is null
                    ? "沒有配對序列——不適用（相關性要兩條同行時間軸的序列）"
                    : $"配對樣本只有 {n} 點，未達 {TrendSentinelService.MinCorrelationPoints} 點——樣本不足不算相關性");

        var a = s.A.Take(n).ToList();
        var b = s.B.Take(n).ToList();
        var shift = TrendSentinel.CorrelationShiftBetweenHalves(a, b);

        string reading = shift.SmoothStrength >= 0.8
            ? "濾波後仍維持高相關——兩者共同變化的成分由同一組起伏主導，值得繼續追"
            : shift.SmoothStrength <= 0.3
                ? "濾波後相關明顯變弱——兩條各走各的緩慢趨勢，不構成共同變化"
                : "濾波後相關中等——訊號混雜，不足以支持任何方向性結論";

        return new HardwareFact(CorrelationKey, CatBl, "相關性探索",
            $"「{s.NameA}」×「{s.NameB}」・{n} 點・原始相關 {shift.RawCorrelation:0.###}・" +
            $"平滑後 {shift.SmoothCorrelation:0.###}（強度 {shift.SmoothStrength:0.###}）。{reading}。" +
            "<b>觀察不是因果</b>：兩條各自單調的序列，其相關係數會趨近 ±1——" +
            "所以判斷依據是「平滑後還剩多少」，不是原始係數高低；而就算真相關，也不代表誰造成誰。",
            "", "歷史倉兩條序列（穩健相關性，唯讀）", FactTrustLevel.Derived, false, at, shift.RawCorrelation);
    }

    // ── BL-009 負載模式分類 ───────────────────────────────────────────────

    public static HardwareFact PatternFact(DateTimeOffset at, PatternProbe? probe = null)
    {
        PatternState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || s.Samples.Count < BaselineLearningService.MinSamples)
            return Unavailable(at, PatternKey, CatBl, "負載模式分類", FactAvailability.NotApplicable,
                s is null
                    ? "沒有負載樣本——不適用（分群要資料）"
                    : $"樣本 {s.Samples.Count} 點，未達 {BaselineLearningService.MinSamples} 點門檻——樣本不足就不分群");

        if (s.Clusters <= 1)
            return Unavailable(at, PatternKey, CatBl, "負載模式分類", FactAvailability.NotApplicable,
                "所有樣本落在同一群——這台機器在取樣期間只有一種負載型態，沒有可區分的模式");

        string detail = string.Join("、", s.Centers.Select((c, i) =>
            $"群{i + 1} 中心 {c:0.#}（{s.Sizes[i]} 點）"));
        double spread = s.Centers.Max() - s.Centers.Min();

        return new HardwareFact(PatternKey, CatBl, "負載模式分類",
            $"樣本 {s.Samples.Count} 點・分為 {s.Clusters} 群・群間距 {spread:0.#}。{detail}。" +
            "<b>推測級聲明</b>：分群是數學結果，不是「知道使用者在做什麼」——" +
            "同一個群可能是打遊戲也可能是跑渲染；本項只說「取樣期內有幾種明顯不同的負載水準」，" +
            "不對每一群貼標籤。",
            "群", "歷史倉負載樣本（穩健分群）", FactTrustLevel.Derived, false, at, s.Clusters);
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string cat, string name,
        FactAvailability availability, string reason)
        => new(key, cat, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
