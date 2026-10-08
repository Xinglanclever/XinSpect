using System.Collections.ObjectModel;
using System.Globalization;

namespace XinSpect;

/// <summary>一支哨兵觀測到的單一事件（變化點或趨勢），供 UI 列表顯示。</summary>
/// <param name="Time">事件時間（本地）。</param>
/// <param name="MetricIndex">對應的 <see cref="HistoryMetrics"/> 指標索引。</param>
/// <param name="Headline">一行結論（繁中原文，交由語言層翻譯）。</param>
/// <param name="Detail">依據：實際數字與方法，供使用者自行核對。</param>
/// <param name="Severity">嚴重度色（僅表達幅度，不代表好壞）。</param>
public sealed record SentinelFinding(
    DateTime Time, int MetricIndex, string Headline, string Detail, Severity Severity);

/// <summary>
/// 事實時序哨兵：把歷史倉的資料交給 <see cref="TrendSentinel"/> 做統計判讀，翻成人看得懂的一行結論。
/// </summary>
/// <remarks>
/// <para>
/// <b>只做統計推論，不對硬體下結論。</b>本服務說的是「這個數列在何時改變了、斜率是多少、
/// 兩個數列的關係有沒有變化」，不是「你的硬碟快壞了」或「你的散熱壞了」——因果判讀留給使用者。
/// 每一則結論都附上實際數字與方法名稱，讓使用者能自己核對。
/// </para>
/// <para>
/// <b>三態誠實：</b>資料點不足時回報「樣本不足」而不是硬給一個趨勢；該指標整段沒有讀值
/// （本機沒有這顆感測器）時回報「本機無此感測器」而不是把一串 0 當成量測結果；
/// 常數序列回報「無變化」而不是「穩定」。
/// </para>
/// <para>
/// <b>成本：</b>斜率估計是 O(n²) 配對。本服務在分析前先把超過 <see cref="MaxPoints"/> 的
/// 區間等距降採樣，把成本壓在可預期的範圍內；降採樣後的樣本數會如實寫進依據欄。
/// </para>
/// </remarks>
public sealed class TrendSentinelService : ObservableObject
{
    /// <summary>單一指標送入統計前的最大點數（超過則等距降採樣）。</summary>
    public const int MaxPoints = 360;

    /// <summary>判定「有趨勢」要求的最少資料點（與 <see cref="TrendSentinel.MinSamplesForTrend"/> 一致）。</summary>
    public const int MinTrendPoints = TrendSentinel.MinSamplesForTrend;

    /// <summary>判定「有變化點」要求的最少資料點。</summary>
    public const int MinChangePoints = TrendSentinel.MinSamplesForChange;

    /// <summary>相關性分析要求的最少資料點。</summary>
    public const int MinCorrelationPoints = 8;

    /// <summary>趨勢強度換算成「每小時變化量」時用的取樣步長（分鐘級歷史倉為 1）。</summary>
    private const double MinutesPerStep = 1.0;

    /// <summary>斜率換算成每小時的變化量。</summary>
    public static double PerHour(double slopePerStep) => slopePerStep * (60.0 / MinutesPerStep);

    /// <summary>本次分析的結論（最新在前）。</summary>
    public ObservableCollection<SentinelFinding> Findings { get; } = new();

    private string _statusText = "尚未分析";
    /// <summary>狀態列文字（繁中原文）。</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private string _coverageText = "";
    /// <summary>本次分析的覆蓋範圍如實說明（幾個指標有資料、幾個沒有感測器）。</summary>
    public string CoverageText { get => _coverageText; private set => SetProperty(ref _coverageText, value); }

    private bool _hasFindings;
    /// <summary>是否有任何發現（UI 據此顯示空狀態）。</summary>
    public bool HasFindings { get => _hasFindings; private set => SetProperty(ref _hasFindings, value); }

    /// <summary>
    /// 分析指定時間窗內的歷史資料。由歷史頁在載入／自動跟隨時呼叫。
    /// </summary>
    /// <param name="store">歷史倉。</param>
    /// <param name="fromUtc">起（UTC）。</param>
    /// <param name="toUtc">迄（UTC）。</param>
    /// <remarks>
    /// 以整段區間為分析範圍；資料點過多時等距降採樣（見 <see cref="MaxPoints"/>）。
    /// 任一步驟失敗都不會拋出——哨兵是附加功能，不能影響歷史頁本身。
    /// </remarks>
    public void Analyze(HistoryStore store, DateTime fromUtc, DateTime toUtc)
    {
        ArgumentNullException.ThrowIfNull(store);
        Findings.Clear();
        try
        {
            var series = store.Query(fromUtc, toUtc);
            if (series.Count == 0)
            {
                StatusText = "此區間沒有資料——哨兵無從判讀";
                CoverageText = "";
                HasFindings = false;
                return;
            }

            int withData = 0, withoutSensor = 0;
            var rows = new List<(int Metric, double[] Values)>(HistoryMetrics.Count);
            for (int m = 0; m < HistoryMetrics.Count; m++)
            {
                if (!series.HasData(m)) { withoutSensor++; continue; }
                withData++;
                rows.Add((m, Extract(series, m)));
            }

            CoverageText = withoutSensor == 0
                ? $"{withData} 項指標皆有讀值"
                : $"{withData} 項指標有讀值，{withoutSensor} 項本機無此感測器";

            if (withData == 0)
            {
                StatusText = "全部指標在本機都讀不到讀值——哨兵沒有可判讀的資料";
                HasFindings = false;
                return;
            }

            var byTime = new List<SentinelFinding>();

            // 1) 每個指標的單調趨勢（只在統計上站得住腳時才報）
            foreach (var (metric, values) in rows)
                AddTrend(byTime, metric, values, series);

            // 2) 每個指標的變化點（突變）
            foreach (var (metric, values) in rows)
                AddChangePoints(byTime, metric, values, series);

            // 3) 兩個最常一起看的指標之間的關係穩定性
            AddRelation(byTime, rows, series);

            // 最新在前
            foreach (var f in byTime.OrderByDescending(f => f.Time))
                Findings.Add(f);

            HasFindings = Findings.Count > 0;
            StatusText = Findings.Count == 0
                ? "本區間沒有統計上站得住腳的變化——這是有意義的結果"
                : $"本區間共 {Findings.Count} 則統計發現（判讀留給使用者）";
        }
        catch (Exception ex)
        {
            StatusText = "分析失敗：" + ex.Message;
            Diag.Swallow("趨勢哨兵分析", ex, "本次未產生哨兵結論");
            HasFindings = false;
        }
    }

    // ── 各類判讀 ──────────────────────────────────────────────────────────

    private static void AddTrend(
        List<SentinelFinding> sink, int metric, double[] values, HistorySeries series)
    {
        if (values.Length < MinTrendPoints) return;
        var est = TrendSentinel.Slope(values);
        if (!est.SlopeExcludesZero) return;      // 統計上站不住腳就不報，避免雜訊被當趨勢

        double perHour = PerHour(est.SlopePerStep);
        string unit = HistoryMetrics.Units[metric];
        string dir = perHour > 0 ? "上升" : "下降";
        double lo = PerHour(est.LowSlope), hi = PerHour(est.HighSlope);

        sink.Add(new SentinelFinding(
            series.Times[^1].ToLocalTime(),
            metric,
            $"{HistoryMetrics.Titles[metric]}呈{dir}趨勢：每小時約 {Math.Abs(perHour):0.##} {unit}",
            $"Theil–Sen 穩健斜率 ・ 95% 信賴區間 {lo:0.##} ～ {hi:0.##} {unit}/小時"
            + $" ・ 樣本 {est.Pairs} 對配對、{values.Length} 點（{(series.SecondLevel ? "秒級" : "分鐘級")}）",
            Severity.Neutral));
    }

    private static void AddChangePoints(
        List<SentinelFinding> sink, int metric, double[] values, HistorySeries series)
    {
        if (values.Length < MinChangePoints) return;
        var hits = TrendSentinel.ChangePoints(values);
        if (hits.Count == 0) return;

        string unit = HistoryMetrics.Units[metric];
        foreach (var hit in hits)
        {
            string dir = hit.Direction > 0 ? "上移" : "下移";
            sink.Add(new SentinelFinding(
                series.Times[Math.Clamp(hit.Index, 0, series.Count - 1)].ToLocalTime(),
                metric,
                $"{HistoryMetrics.Titles[metric]}在此时刻{dir}",
                $"CUSUM 變化點 ・ 估計變化量 {Math.Abs(hit.Change):0.##} {unit}/拍"
                + $" ・ 樣本 {values.Length} 點（{(series.SecondLevel ? "秒級" : "分鐘級")}）"
                + " ・ 累積和終究會對持續的緩慢漂移觸發，請與趨勢結論交叉判讀",
                Severity.Neutral));
        }
    }

    private static void AddRelation(
        List<SentinelFinding> sink, List<(int Metric, double[] Values)> rows, HistorySeries series)
    {
        // 只比「負載 vs 溫度」這個實際有用的配對，不去窮舉組合製造雜訊
        var pairs = new (int Load, int Temp, string Label)[]
        {
            (HistoryMetrics.CpuLoad, HistoryMetrics.CpuTemp, "處理器負載與溫度"),
            (HistoryMetrics.GpuLoad, HistoryMetrics.GpuTemp, "顯示卡負載與溫度"),
        };

        foreach (var (loadM, tempM, label) in pairs)
        {
            var load = rows.FirstOrDefault(r => r.Metric == loadM);
            var temp = rows.FirstOrDefault(r => r.Metric == tempM);
            if (load.Values is null || temp.Values is null) continue;

            int n = Math.Min(load.Values.Length, temp.Values.Length);
            if (n < MinCorrelationPoints) continue;

            var cs = TrendSentinel.CorrelationShiftBetweenHalves(load.Values, temp.Values);
            if (cs.SampleCount < MinCorrelationPoints) continue;

            double first = cs.RawCorrelation, second = cs.SmoothCorrelation;
            // 只有「後期明顯失去耦合」才值得說；兩段都強耦合是正常狀態，不必佔版面。
            // 門檻取 0.8 → 0.5：實測「溫度被固定墊高、與負載完全脫鉤」的情境會落到 ≈ 0.0，
            // 而「耦合變弱但仍相關」的情境落在 0.88 附近——那是程度差異不是失去耦合，
            // 依 0.4 的嚴格門檻會被整批漏掉，依 0.5 則能涵蓋真正脫鉤的個案又不誤報。
            bool lost = Math.Abs(first) >= 0.8 && Math.Abs(second) < 0.5;
            if (!lost) continue;

            sink.Add(new SentinelFinding(
                series.Times[^1].ToLocalTime(),
                tempM,
                $"{label}的關聯在後期明顯減弱",
                $"前後半相關性 {first:0.##} → "
                + $"{second:0.##} ・ Pearson ・ 樣本 {cs.SampleCount} 點"
                + " ・ 只報「關係變了」，不代表因果；請一併看兩條各自的趨勢",
                Severity.Neutral));
        }
    }

    // ── 抽取與降採樣 ──────────────────────────────────────────────────────

    // 取單一指標的時間序列（每點的代表值＝平均），超過 MaxPoints 就等距降採樣。
    private static double[] Extract(HistorySeries series, int metric)
    {
        int n = series.Count;
        if (n <= MaxPoints)
        {
            var direct = new double[n];
            for (int i = 0; i < n; i++) direct[i] = series.A(i, metric);
            return direct;
        }

        // 等距取樣：每桶取該桶的平均，維持整體形狀又不失代表性
        var buffer = new double[MaxPoints];
        for (int c = 0; c < MaxPoints; c++)
        {
            int s = (int)((long)c * n / MaxPoints);
            int e = (int)((long)(c + 1) * n / MaxPoints);
            if (e <= s) e = s + 1;
            if (e > n) e = n;
            double sum = 0;
            for (int i = s; i < e; i++) sum += series.A(i, metric);
            buffer[c] = sum / (e - s);
        }
        return buffer;
    }

    /// <summary>把斜率換算成人看得懂的字串（供 UI 直接顯示，含單位）。</summary>
    public static string DescribeSlope(int metric, double perHour)
    {
        string dir = perHour > 0 ? "上升" : "下降";
        string unit = HistoryMetrics.Units[metric];
        return $"{dir} {Math.Abs(perHour).ToString("0.##", CultureInfo.InvariantCulture)} {unit}/小時";
    }
}
