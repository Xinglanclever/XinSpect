using System.IO;

namespace XinSpect;

/// <summary>
/// 基線學習第二批（Vol2 第 BL-002、BL-005、BL-008、BL-011、BL-012 項）：
/// 溫度趨勢外推（<c>bl.temp.forecast</c>）、季節模式（<c>bl.season.summary</c>）、
/// 開機效能基線（<c>bl.boot.summary</c>）、預測不確定性（<c>bl.ci.summary</c>）、
/// 資料品質聲明（<c>bl.quality</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這個域的性格是「說清楚自己有多不確定」。</b>五項裡有四項在做同一件事：
/// 把「這個數字有多可信」變成事實本身。趨勢帶信賴區間、外推標 horizon 與樣本量、
/// 季節只在樣本跨得夠久時才敢說、資料品質逐項列出缺口。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① 外推是<b>估計不是預言</b>——一律附信賴區間與 horizon，且不給單一「未來值」當結論；
/// ② 季節模式需要<b>跨越足夠長的時間</b>才成立，樣本不足一律 NotApplicable；
/// ③ 開機基線是「每次開機對自基線」，不是廠商標稱值；
/// ④ 資料品質聲明要<b>具名列出缺口</b>，不是給一個分數；
/// ⑤ 信賴區間不含零才叫趨勢，含零一律如實說「這串起伏還看不出方向」。
/// </para>
/// </remarks>
public static class BaselineForecastFactsService
{
    private const string Cat = "基線學習";
    private const string Source = "歷史倉（分鐘級彙整）＋穩健統計";

    /// <summary>稱得上「季節模式」的最短涵蓋天數。</summary>
    public const int MinSeasonDays = 14;
    /// <summary>外推 horizon 的最長步數——再遠就不給了，免得讀者以為外推有意義。</summary>
    public const int MaxHorizonSteps = 60;

    // ── 鍵 ────────────────────────────────────────────────────────────────

    /// <summary>BL-002 溫度趨勢外推。</summary>
    public const string TempForecastKey = "bl.temp.forecast";
    /// <summary>BL-005 季節模式。</summary>
    public const string SeasonKey = "bl.season.summary";
    /// <summary>BL-008 開機效能基線。</summary>
    public const string BootKey = "bl.boot.summary";
    /// <summary>BL-011 預測不確定性。</summary>
    public const string ConfidenceKey = "bl.ci.summary";
    /// <summary>BL-012 資料品質聲明。</summary>
    public const string QualityKey = "bl.quality";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>（溫度序列 °C, 每個取樣點之間的分鐘數）。</summary>
    public sealed record TempSeries(IReadOnlyList<double> Values, double MinutesPerStep);

    /// <summary>（白天均溫序列, 夜間均溫序列, 涵蓋天數）——日夜分離是季節模式的粗粒度代理。</summary>
    public sealed record SeasonSamples(IReadOnlyList<double> DayMeans, IReadOnlyList<double> NightMeans, int DaysCovered);

    /// <summary>（每次開機的到桌面毫秒序列, 涵蓋的開機次數）。</summary>
    public sealed record BootSamples(IReadOnlyList<double> Ms);

    /// <summary>（有讀值的指標數, 總指標數, 各指標樣本數, 缺口說明）。</summary>
    public sealed record QualityState(int MetricsWithData, int MetricsTotal, IReadOnlyList<int> Samples, string Gaps);

    public delegate TempSeries? TempProbe();
    public delegate SeasonSamples? SeasonProbe();
    public delegate BootSamples? BootProbe();
    public delegate QualityState? QualityProbe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        TempProbe? temp = null,
        SeasonProbe? season = null,
        BootProbe? boot = null,
        QualityProbe? quality = null)
    {
        var facts = new List<HardwareFact>(5)
        {
            TempForecastFact(at, temp),
            SeasonFact(at, season),
            BootFact(at, boot),
            QualityFact(at, quality),
        };
        facts.Add(ConfidenceSummaryFact(at, facts));
        return facts;
    }

    // ── BL-002 溫度趨勢外推 ───────────────────────────────────────────────

    public static HardwareFact TempForecastFact(DateTimeOffset at, TempProbe? probe = null)
    {
        TempSeries? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || s.Values.Count < TrendSentinel.MinSamplesForTrend)
            return Unavailable(at, TempForecastKey, "溫度趨勢外推", FactAvailability.NotApplicable,
                $"溫度歷史少於 {TrendSentinel.MinSamplesForTrend} 點——樣本不足就不外推（寧可說不知道）");

        var est = TrendSentinel.Slope(s.Values);
        double last = s.Values[^1];
        int horizon = Math.Min(MaxHorizonSteps, Math.Max(1, s.Values.Count / 6));
        double forecast = last + est.SlopePerStep * horizon;
        double lowForecast = last + est.LowSlope * horizon;
        double highForecast = last + est.HighSlope * horizon;

        string verdict = est.SlopeExcludesZero
            ? $"斜率信賴區間不含零（{est.LowSlope:0.####}–{est.HighSlope:0.####}／步），趨勢站得住腳"
            : $"斜率信賴區間含零（{est.LowSlope:0.####}–{est.HighSlope:0.####}／步）——這串起伏還看不出方向，別當趨勢";

        return new HardwareFact(TempForecastKey, Cat, "溫度趨勢外推",
            $"最新 {last:0.#} °C・Theil–Sen 斜率 {est.SlopePerStep:0.####} °C/步（{s.MinutesPerStep:0.#} 分／步）・" +
            $"外推 {horizon} 步：{forecast:0.#} °C（區間 {Math.Min(lowForecast, highForecast):0.#}–{Math.Max(lowForecast, highForecast):0.#}）。{verdict}。" +
            "<b>這是估計不是預言</b>：外推假設「接下來跟剛過去的一樣」，散熱改變、負載改變、環境溫度改變都會推翻它——" +
            "區間比中點重要，樣本量比區間重要。",
            "°C", Source, FactTrustLevel.Derived, false, at, forecast);
    }

    // ── BL-005 季節模式 ───────────────────────────────────────────────────

    public static HardwareFact SeasonFact(DateTimeOffset at, SeasonProbe? probe = null)
    {
        SeasonSamples? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || s.DaysCovered < MinSeasonDays)
            return Unavailable(at, SeasonKey, "季節模式", FactAvailability.NotApplicable,
                s is null
                    ? "沒有日夜分離的歷史樣本——季節要資料，不憑空生一個模式出來"
                    : $"只涵蓋 {s.DaysCovered} 天，未達 {MinSeasonDays} 天門檻——樣本短於兩週，分不出季節與雜訊");

        double day = s.DayMeans.DefaultIfEmpty(0).Average();
        double night = s.NightMeans.DefaultIfEmpty(0).Average();
        double spread = day - night;
        string reading = Math.Abs(spread) < 1.0
            ? "日夜差不到 1 °C——這台機器的散熱與環境在取樣期內沒有明顯日夜差"
            : spread > 0
                ? $"日間比夜間高 {spread:0.#} °C——與環境溫度日夜起伏一致（室溫影響散熱效率）"
                : $"夜間比日間高 {-spread:0.#} °C——與一般日夜起伏相反，值得看一眼取樣期間有沒有別的事在跑";

        return new HardwareFact(SeasonKey, Cat, "季節模式",
            $"涵蓋 {s.DaysCovered} 天・日間均 {day:0.#} °C・夜間均 {night:0.#} °C・日夜差 {spread:+0.#;-0.#;0} °C。{reading}。" +
            "<b>這是粗粒度代理</b>：用日夜均值代替真正的季節分解，只跨得過兩個星期——" +
            "真正的季節模式要跨季的資料，現在說得出來的就只有「日夜有沒有差」。",
            "°C", Source, FactTrustLevel.Derived, false, at, day - night);
    }

    // ── BL-008 開機效能基線 ───────────────────────────────────────────────

    public static HardwareFact BootFact(DateTimeOffset at, BootProbe? probe = null)
    {
        BootSamples? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || s.Ms.Count == 0)
            return Unavailable(at, BootKey, "開機效能基線", FactAvailability.ReadError,
                "沒有開機耗時樣本——讀不到就是不猜（開機基線要多次開機才成立）");
        if (s.Ms.Count < 3)
            return Unavailable(at, BootKey, "開機效能基線", FactAvailability.NotApplicable,
                $"只有 {s.Ms.Count} 次開機紀錄——少於 3 次不足以當基線，不拿一次開機代表常態");

        var sorted = s.Ms.OrderBy(x => x).ToList();
        double median = sorted[sorted.Count / 2];
        double latest = s.Ms[^1];
        double delta = latest - median;
        string trend = Math.Abs(delta) < median * 0.15
            ? "最新一次與中位相當（±15% 內）"
            : delta > 0
                ? $"最新一次比中位慢 {delta:0} ms（{delta / median * 100:+0.#}%）"
                : $"最新一次比中位快 {-delta:0} ms（{delta / median * 100:0.#}%）";

        return new HardwareFact(BootKey, Cat, "開機效能基線",
            $"開機 {s.Ms.Count} 次・中位 {median:0} ms・最新 {latest:0} ms・最快 {sorted[0]:0} ms・最慢 {sorted[^1]:0} ms。{trend}。" +
            "<b>這是本機自基線，不是廠商標稱值</b>：開機時間受更新安裝、啟動項增減、驅動載入、磁碟健康影響，" +
            "跟自己比才有意義；單次快慢都可能只是那一次剛好裝了更新。",
            "ms", "開機事件（Diagnostics-Performance，唯讀）", FactTrustLevel.Measured, false, at, median);
    }

    // ── BL-012 資料品質聲明 ───────────────────────────────────────────────

    public static HardwareFact QualityFact(DateTimeOffset at, QualityProbe? probe = null)
    {
        QualityState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, QualityKey, "資料品質聲明", FactAvailability.ReadError,
                "歷史倉狀態讀不到——品質聲明本身讀不到，就不能拿它背書任何預測");

        var v = s;
        int thin = v.Samples.Count(x => x < BaselineLearningService.MinSamples);
        int rich = v.Samples.Count(x => x >= BaselineLearningService.MinSamples);
        string body = v.MetricsWithData == 0
            ? "歷史倉還沒有任何指標有讀值——所有預測都無從談起"
            : $"有讀值的指標 {v.MetricsWithData}/{v.MetricsTotal} 項・達 {BaselineLearningService.MinSamples} 點門檻 {rich} 項・" +
              $"樣本不足 {thin} 項。缺口：{v.Gaps}";

        return new HardwareFact(QualityKey, Cat, "資料品質聲明",
            body + "。<b>具名列缺口是刻意的</b>：給一個「品質 87 分」的分數看起來專業，" +
            "但沒人知道那 13 分扣在哪、該不該相信下一條預測——" +
            "列出「哪幾項樣本不足」才能讓人自己判斷哪條預測現在能用。",
            "項", Source, FactTrustLevel.Derived, false, at, v.MetricsWithData);
    }

    // ── BL-011 預測不確定性彙總 ───────────────────────────────────────────

    private static HardwareFact ConfidenceSummaryFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        var derived = facts.Where(f => f.Trust == FactTrustLevel.Derived).ToList();
        int n = facts.Count;
        int unavailable = facts.Count(f => f.Availability != FactAvailability.Present);
        int withInterval = facts.Count(f => f.Key == TempForecastKey && f.Availability == FactAvailability.Present);

        return new HardwareFact(ConfidenceKey, Cat, "預測不確定性",
            $"本批 {n} 條預測事實・衍生值 {derived.Count} 條・帶明確區間 {withInterval} 條・不可得 {unavailable} 條。" +
            "<b>不確定性要跟數字一起交付</b>：一條沒有區間的預測看起來比一條有區間的精確，那是錯覺——" +
            "本專案的折衷是：能給區間的就給（Theil–Sen 的 95% 信賴區間），給不了的如實說給不了，" +
            "而不是把單點估計包裝成結論。",
            "條", Source, FactTrustLevel.Derived, false, at, withInterval);
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
