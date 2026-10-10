using System.Diagnostics.Eventing.Reader;

namespace XinSpect;

/// <summary>一條事件的標註輸入（時間、來源、編號、訊息）。</summary>
public sealed record AnnotatedEvent(DateTimeOffset At, string Channel, int Id, string Message);

/// <summary>標註在時間軸上的一件事（BL-007）：種類 + 一句話＋時間。</summary>
public sealed record EventAnnotation(DateTimeOffset At, string Kind, string Label);

/// <summary>
/// 事件標註層（BL-007）：把 WHEA／TDR／非預期關機／藍屏這幾類事件<b>對映成時間軸上的標註</b>，
/// 好讓它們跟溫度、頻率、負載曲線畫在同一個時間軸上（「那一次當機當時幾度」）。
/// <para>
/// 純函式、不讀任何來源：讀取由呼叫端注入。標註<b>只說「那個時間點發生了這種事件」</b>，
/// 不建立因果——哪一條曲線造成當機不在本層的答案裡（那需要實驗，不是關聯）。
/// </para>
/// </summary>
public static class EventAnnotationLayer
{
    public const string UnexpectedShutdown = "非預期關機";
    public const string BugCheck = "藍屏";
    public const string Whea = "WHEA 硬體錯誤";
    public const string DisplayTimeout = "顯示驅動逾時（TDR）";

    /// <summary>把事件逐筆標註；無法歸類的事件不產標註（不回傳「其他」這種什麼都沒說的標籤）。</summary>
    public static IReadOnlyList<EventAnnotation> FromEvents(IReadOnlyList<AnnotatedEvent> events)
    {
        var list = new List<EventAnnotation>(events.Count);
        foreach (var e in events)
        {
            string? kind = Classify(e);
            if (kind is null) continue;
            string label = e.Message.Length > 0 ? $"{kind}：{OneLine(e.Message)}" : kind;
            list.Add(new EventAnnotation(e.At, kind, label));
        }
        return list.OrderBy(a => a.At).ToList();
    }

    /// <summary>WHEA 事件來源或編號 → 種類；不屬於這四類回 null。</summary>
    public static string? Classify(AnnotatedEvent e)
    {
        if (e.Channel.Contains("WHEA", StringComparison.OrdinalIgnoreCase)) return Whea;
        return e.Id switch
        {
            41 or 6008 => UnexpectedShutdown,
            1001 => BugCheck,
            4101 => DisplayTimeout,
            _ => null,
        };
    }

    private static string OneLine(string message)
    {
        string first = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } lines
            ? lines[0]
            : "";
        return first.Length > 120 ? first[..120] + "…" : first;
    }
}

/// <summary>單一指標的自基線模型（BL-001）。樣本數與離散度一起走——只給平均值的「基線」不能判斷異常。</summary>
public sealed record MetricBaseline(int Metric, string Title, string Unit, int Samples,
    double Mean, double StdDev, double P50, double P95);

/// <summary>
/// 自基線學習與異常評分（Vol 2 批次 C／BL-001、BL-004、BL-007、BL-012）。
/// <para>
/// <b>做什麼：</b>把歷史倉的每個指標算成本機自己的分位數模型（中位數、P95、平均、標準差、樣本數），
/// 再用最新一點對模型算 z 分數，取最大偏差當異常分數。
/// </para>
/// <para>
/// <b>界線（寫進值裡）：</b>①<b>分數不是診斷</b>——偏差大只代表「跟這台機器自己的過去不一樣」，
/// 可能是新的負載、新的驅動、也可能是感測器飄了；②<b>樣本不足就不稱基線</b>（門檻寫在
/// <see cref="MinSamples"/>，值裡標明樣本量與缺口）；③歷史倉的 0 是「沒讀到」的既有表示法，
/// 因此整段皆 0 的指標由 <see cref="HistorySeries.HasData"/> 排除，不進模型。
/// </para>
/// </summary>
public static class BaselineLearningService
{
    public const string Category = "基線學習";

    public const string BaselineKey = "bl.baseline";
    public const string ScoreKey = "bl.score";
    public const string EventsKey = "bl.events";

    /// <summary>稱得上「基線」的最少樣本數；低於它就如實標樣本不足，不給半條基線。</summary>
    public const int MinSamples = 30;

    private const string Source = "歷史倉（分鐘級彙整）＋事件記錄";
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    // ── 模型 ──────────────────────────────────────────────────────────────

    /// <summary>由歷史序列建出每個指標的自基線（整段皆 0＝沒讀到的指標不進模型）。</summary>
    public static IReadOnlyList<MetricBaseline> BuildModel(HistorySeries series)
    {
        var model = new List<MetricBaseline>();
        if (series.Count == 0) return model;

        for (int m = 0; m < HistoryMetrics.Count; m++)
        {
            if (!series.HasData(m)) continue;
            var values = new double[series.Count];
            for (int i = 0; i < series.Count; i++) values[i] = series.A(i, m);

            double mean = values.Average();
            double variance = values.Length == 0 ? 0 : values.Sum(v => (v - mean) * (v - mean)) / values.Length;
            model.Add(new MetricBaseline(m, HistoryMetrics.Titles[m], HistoryMetrics.Units[m], values.Length,
                mean, Math.Sqrt(variance),
                HistorySeries.Percentile(values, 50), HistorySeries.Percentile(values, 95)));
        }
        return model;
    }

    /// <summary>最新一點對基線的 z 分數（標準差為 0 時回 0——沒有離散度的指標無從判斷偏差）。</summary>
    public static double ZScore(double latest, MetricBaseline baseline) =>
        baseline.StdDev <= 0 ? 0 : (latest - baseline.Mean) / baseline.StdDev;

    // ── 事實 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, HistorySeries series,
        Func<IReadOnlyList<AnnotatedEvent>?>? eventsProbe = null, TimeSpan? window = null)
    {
        TimeSpan span = window ?? DefaultWindow;
        var model = BuildModel(series);
        var facts = new List<HardwareFact>(model.Count + 3);

        facts.Add(BaselineFact(at, series, model, span));
        facts.Add(ScoreFact(at, series, model));
        facts.Add(EventsFact(at, eventsProbe, span));
        return facts;
    }

    private static HardwareFact BaselineFact(DateTimeOffset at, HistorySeries series,
        IReadOnlyList<MetricBaseline> model, TimeSpan span)
    {
        if (series.Count == 0)
            return Unavailable(at, BaselineKey, "自基線",
                FactAvailability.NotSupported,
                "歷史倉還沒有資料（尚未累積）——基線要資料，不憑空生一條出來");

        var thin = model.Where(b => b.Samples < MinSamples).ToList();
        string quality = thin.Count == 0
            ? $"全部 {model.Count} 項都達到 {MinSamples} 點門檻，可當基線"
            : $"其中 {thin.Count} 項樣本不足 {MinSamples} 點（{string.Join("、", thin.Take(3).Select(b => $"{b.Title} {b.Samples} 點"))}" +
              (thin.Count > 3 ? " 等" : "") + "）——如實標示，不給半條基線";
        // 逐指標模型串在同一個值裡（不另立鍵）：這一版刻意不新增動態家族——
        // 家族家數有上限守門，而指標集本身是編譯期固定的一組，逐項明細寫在值裡同樣可讀、可比較。
        string perMetric = model.Count == 0
            ? "沒有指標有讀值"
            : string.Join("；", model.Select(b =>
                $"{b.Title} 中位 {b.P50:0.##}{b.Unit}／P95 {b.P95:0.##}{b.Unit}／±{b.StdDev:0.##}／{b.Samples} 點"));

        return new HardwareFact(BaselineKey, Category, "自基線",
            $"視窗 {span.TotalDays:0} 天・樣本 {series.Count} 點・有讀值的指標 {model.Count}/{HistoryMetrics.Count} 項・{quality}。" +
            $"逐項：{perMetric}。" +
            "整段皆 0 的指標（本機無此感測器）不進模型——那是『沒讀到』的既有表示法，不是量到 0。",
            "點", Source, FactTrustLevel.Derived, false, at, series.Count);
    }

    private static HardwareFact ScoreFact(DateTimeOffset at, HistorySeries series,
        IReadOnlyList<MetricBaseline> model)
    {
        var usable = model.Where(b => b.Samples >= MinSamples && b.StdDev > 0).ToList();
        if (usable.Count == 0)
            return Unavailable(at, ScoreKey, "異常評分",
                FactAvailability.NotSupported,
                model.Count == 0
                    ? "沒有任何指標達到基線門檻——沒有基線就沒有偏差可言"
                    : $"有讀值的指標都未達 {MinSamples} 點或沒有離散度——如實說無法評分，不以 0 分冒充正常");

        int last = series.Count - 1;
        var top = usable
            .Select(b => (Baseline: b, Z: ZScore(series.A(last, b.Metric), b)))
            .OrderByDescending(x => Math.Abs(x.Z))
            .First();

        return new HardwareFact(ScoreKey, Category, "異常評分",
            $"最大偏差 {Math.Abs(top.Z):0.0}σ：{top.Baseline.Title}（最新 {series.A(last, top.Baseline.Metric):0.##}" +
            $"{top.Baseline.Unit} 對基線 {top.Baseline.Mean:0.##}±{top.Baseline.StdDev:0.##}）・" +
            $"以 {usable.Count} 項有基線的指標計・最新樣本時間 {series.Times[last]:yyyy-MM-dd HH:mm}。" +
            "分數不是診斷：偏差大只代表「跟這台機器自己的過去不一樣」。",
            "σ", Source, FactTrustLevel.Derived, false, at, Math.Abs(top.Z));
    }

    private static HardwareFact EventsFact(DateTimeOffset at,
        Func<IReadOnlyList<AnnotatedEvent>?>? probe, TimeSpan span)
    {
        IReadOnlyList<AnnotatedEvent>? events;
        try { events = (probe ?? RealEvents(span))(); }
        catch { events = null; }

        if (events is null)
            return Unavailable(at, EventsKey, "事件標註",
                FactAvailability.ReadError,
                "事件記錄查詢失敗（頻道不存在或權限不足）——讀不到就是不猜，不畫成 0 次");

        var annotations = EventAnnotationLayer.FromEvents(events);
        var byKind = annotations.GroupBy(a => a.Kind)
            .ToDictionary(g => g.Key, g => g.Count());
        string counts = byKind.Count == 0
            ? "四類事件都沒有"
            : string.Join("、", byKind.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value} 次"));
        string latest = annotations.Count == 0
            ? "—"
            : annotations[^1].At.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        return new HardwareFact(EventsKey, Category, "事件標註",
            $"視窗 {span.TotalDays:0} 天：{counts}・最近一次 {latest}・可標註 {annotations.Count} 筆。" +
            "標註只說「那個時間點發生了這種事件」，不建立因果（非預期關機也可能是停電）。",
            "筆", Source, FactTrustLevel.Reported, false, at, annotations.Count);
    }

    /// <summary>真實通路：System（41／6008／1001／4101）＋ WHEA-Logger 全數，取視窗內事件。</summary>
    public static Func<IReadOnlyList<AnnotatedEvent>> RealEvents(TimeSpan span)
    {
        long ms = Math.Max(0, (long)span.TotalMilliseconds);
        return () =>
        {
            var list = new List<AnnotatedEvent>();
            ReadChannel(list, "System",
                $"*[System[((EventID=41 or EventID=6008 or EventID=1001 or EventID=4101)) and " +
                $"TimeCreated[timediff(@SystemTime) <= {ms}]]]");
            ReadChannel(list, "Microsoft-Windows-WHEA-Logger/Operational",
                $"*[System[TimeCreated[timediff(@SystemTime) <= {ms}]]]");
            return list;
        };
    }

    private static void ReadChannel(List<AnnotatedEvent> into, string channel, string xpath)
    {
        try
        {
            var query = new EventLogQuery(channel, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            int guard = 0;
            while (guard++ < 500)
            {
                EventRecord? record;
                try { record = reader.ReadEvent(); }
                catch (EventLogNotFoundException) { return; }
                if (record is null) return;
                using (record)
                {
                    string message;
                    try { message = record.FormatDescription() ?? ""; }
                    catch { message = ""; }
                    into.Add(new AnnotatedEvent(
                        new DateTimeOffset(record.TimeCreated ?? DateTime.MinValue),
                        channel, record.Id, message));
                }
            }
        }
        catch { /* 單一頻道讀不到不影響另一條；整批失敗由呼叫端標三態 */ }
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
