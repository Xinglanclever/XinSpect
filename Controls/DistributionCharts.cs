using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 分布圖的純數學（U10-VZ-0009 直方圖／U10-VZ-0103 ECDF，追蹤鍵 <c>viz.hist</c>／<c>viz.ecdf</c>）：
/// 分桶、百分位、經驗累積分布與座標映射。<b>全部是靜態純函式、不碰 WPF</b>——
/// 座標計算要能被單元測試用合成資料直接驗證，不必開視窗（同 <see cref="CoreHeatLayout"/> 的先例：
/// 版面演算法與畫法分家，測試盯演算法，眼睛盯畫法）。
/// </summary>
/// <remarks>
/// 百分位沿用 <see cref="DiskLatencyStats"/> 的最近排名法：回的是排序後第 ⌈p×N⌉ 筆
/// <b>真的量到過的那一筆</b>，不內插、不套分布模型。髒值（NaN／±∞）整筆剔除——
/// 剔了多少要讓呼叫端知道（<see cref="Histogram"/> 回傳的 <c>Used</c>），不是靜默吞掉。
/// </remarks>
public static class LatencyDistributionMath
{
    /// <summary>本功能的追蹤鍵（U10-VZ-0009 直方圖）。以 const 字面寫在原始碼裡，鍵掃描才找得到
    /// （XML 註解裡的鍵掃描不到——第 271 輪教訓）。</summary>
    public const string HistogramKey = "viz.hist";

    /// <summary>本功能的追蹤鍵（U10-VZ-0103 ECDF）。同上，const 字面進原始碼。</summary>
    public const string EcdfKey = "viz.ecdf";

    /// <summary>直方圖的一格：<c>[From, To]</c> 區間的樣本數；最後一格的 <c>To</c> 是樣本最大值（含端點）。</summary>
    public readonly record struct Bucket(double From, double To, int Count);

    /// <summary>一次分桶的結果：格子與實際進桶的樣本數（髒值剔除後的數）；<c>Log</c> 是實際採用的布局
    /// （要求對數但樣本含非正值時如實退化成等寬，呼叫端按這個旗標定位標線）。</summary>
    public sealed record HistogramResult(IReadOnlyList<Bucket> Buckets, int Used, bool Log);

    /// <summary>最近排名百分位：第 ⌈p×N⌉ 筆（0 起始夾限）。呼叫端先排好序。</summary>
    public static double Percentile(double[] sorted, double p)
        => sorted.Length == 0
            ? throw new ArgumentException("沒有樣本，百分位無從談起。", nameof(sorted))
            : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    /// <summary>P50／P95／P99 三個百分位（延遲分布的標準三標線）。</summary>
    public static (double P50, double P95, double P99) Percentiles(double[] sorted)
        => (Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99));

    /// <summary>
    /// 分桶。<paramref name="logBuckets"/> 為真時用<b>對數分桶</b>（等比邊界，
    /// 延遲資料「快的在一邊擠成一團、慢的拖長尾」的形狀才看得見）；
    /// 為假時等寬分桶。頭尾的空格砍掉、中間的空格留著——快慢兩群之間的空白本身就是要看的東西。
    /// </summary>
    /// <remarks>
    /// 邊界含下不含上、最後一格含上端（與 <see cref="LatencyBucket"/> 同一條規矩）。
    /// 對數布局<b>要求樣本全為正</b>：有非正值時整體如實退化成等寬（<see cref="HistogramResult.Log"/>＝false），
    /// 不把 ≤0 硬塞進沒有位置的對數軸。
    /// </remarks>
    public static HistogramResult Histogram(IReadOnlyList<double> values, int binCount, bool logBuckets)
    {
        int bins = Math.Max(1, binCount);
        var clean = Clean(values);
        if (clean.Length == 0) return new HistogramResult([], 0, logBuckets);

        double min = clean[0], max = clean[^1];
        bool useLog = logBuckets && min > 0;
        var counts = new int[bins];
        if (min == max)
        {
            counts[0] = clean.Length;                       // 全段等值：一格如實說「都在這裡」
        }
        else if (useLog)
        {
            double hi = Math.Max(max, min * 2);             // 至少撐起一個數量級，長條不會全擠在第一格
            double ratio = Math.Pow(hi / min, 1.0 / bins);
            foreach (double v in clean)
            {
                int b = (int)Math.Floor(Math.Log(v / min, ratio));
                if (b >= bins) b = bins - 1;                // 夾到最後一格（含上端）
                if (b < 0) b = 0;
                counts[b]++;
            }
        }
        else
        {
            LinearCounts(clean, bins, min, max, counts);
        }

        int first = Array.FindIndex(counts, c => c > 0);
        int last = Array.FindLastIndex(counts, c => c > 0);
        var buckets = new List<Bucket>(last - first + 1);
        for (int i = first; i <= last; i++)
            buckets.Add(new Bucket(Edge(i, bins, min, max, useLog),
                i == bins - 1 ? max : Edge(i + 1, bins, min, max, useLog), counts[i]));
        return new HistogramResult(buckets, clean.Length, useLog);
    }

    /// <summary>經驗累積分布：排序後第 i 筆（0 起始）的 ECDF 點為 (x, (i+1)/N)。
    /// 同值樣本合併成階梯的一個踏面——輸出的 X 嚴格遞增，Y 單調不減、末端恰為 1。</summary>
    public static (double X, double P)[] Ecdf(IReadOnlyList<double> values)
    {
        var clean = Clean(values);
        int n = clean.Length;
        if (n == 0) return [];
        var pts = new List<(double X, double P)>(n);
        for (int i = 0; i < n; i++)
        {
            if (i + 1 < n && clean[i + 1] == clean[i]) continue;   // 同值：走到最後一筆才記（階梯踏面）
            pts.Add((clean[i], (i + 1) / (double)n));
        }
        return [.. pts];
    }

    // ── 座標映射（控制項與測試共用同一套算式）────────────────────────────────

    /// <summary>值 → Y 座標：min 貼底、max 貼頂；區間為零（全段等值）時畫在中線。</summary>
    public static double MapY(double v, double min, double max, double top, double height)
        => max > min ? top + height - Math.Clamp((v - min) / (max - min), 0, 1) * height
                     : top + height / 2;

    /// <summary>比例（0–1）→ X 座標，<paramref name="left"/> 為繪圖區左緣。</summary>
    public static double MapX(double frac, double left, double width)
        => left + Math.Clamp(frac, 0, 1) * width;

    /// <summary>
    /// 值在格子布局上的水平比例（0–1）：<b>格子等寬排在畫面上</b>，所以位置由「落在第幾格＋格內比例」決定，
    /// 對數布局時格內也按對數內插——百分位標線才能落在它真正屬於的位置。
    /// </summary>
    public static double FractionOf(double value, IReadOnlyList<Bucket> buckets, bool log)
    {
        int n = buckets.Count;
        for (int i = 0; i < n; i++)
        {
            var b = buckets[i];
            if (value < b.From) return i / (double)n;
            if (double.IsPositiveInfinity(b.To) || value < b.To)
            {
                if (value <= b.From) return i / (double)n;
                double inner = log && b.From > 0 && value > 0
                    ? Math.Log(value / b.From) / Math.Log(b.To / b.From)
                    : (value - b.From) / (b.To - b.From);
                return (i + Math.Clamp(inner, 0, 1)) / n;
            }
        }
        return 1.0;
    }

    /// <summary>延遲值的版面標示：微秒與毫秒分開寫，不要讓人自己換算（與 <see cref="LatencyBucket"/> 同款）。</summary>
    public static string LatencyText(double us)
        => Math.Abs(us) < 1000 ? $"{us:0.#} µs" : $"{us / 1000:0.##} ms";

    // ── 內部 ────────────────────────────────────────────────────────────────

    /// <summary>剔掉髒值（NaN／±∞）後排序；剔了多少＝原長度與回傳長度的差，如實可見。</summary>
    internal static double[] Clean(IReadOnlyList<double> values)
    {
        var list = new List<double>(values.Count);
        foreach (double v in values)
            if (!double.IsNaN(v) && !double.IsInfinity(v)) list.Add(v);
        var arr = list.ToArray();
        Array.Sort(arr);
        return arr;
    }

    private static void LinearCounts(double[] sorted, int bins, double min, double max, int[] counts)
    {
        foreach (double v in sorted)
        {
            int b = (int)Math.Floor((v - min) / (max - min) * bins);
            if (b >= bins) b = bins - 1;       // 恰為 max 的樣本落在最後一格（含上端）
            if (b < 0) b = 0;
            counts[b]++;
        }
    }

    /// <summary>第 <paramref name="i"/> 個邊界的值：等寬＝線性內插；對數＝等比內插（<paramref name="log"/> 為真時
    /// 假定 min&gt;0——<see cref="Histogram"/> 已保證兩者不會混用）。</summary>
    private static double Edge(int i, int bins, double min, double max, bool log)
    {
        if (!log) return min + (max - min) * i / (double)bins;
        if (i == 0) return min;
        double hi = Math.Max(max, min * 2);
        return min * Math.Pow(hi / min, i / (double)bins);
    }
}

/// <summary>
/// 延遲直方圖（U10-VZ-0009，追蹤鍵 <c>viz.hist</c>）：樣本分桶畫成長條，
/// 疊上 P50／P95／P99 三條虛線標線——平均值看不出的停頓，尾端百分位看得出來。
/// </summary>
/// <remarks>
/// 畫法沿 <see cref="FrameTimeChart"/> 的自繪先例（OnRender＋凍結筆刷＋FormattedText），
/// 數學全部在 <see cref="LatencyDistributionMath"/>，本類只負責把算好的格子與百分位放到座標上。
/// <b>沒有樣本就畫「沒有樣本」，不畫一根貼底的空長條冒充量測。</b>
/// DP 型別包一層 <see cref="HistogramChart.DistributionSeries"/>（照 <see cref="Sparkline.SparkSeries"/>
/// 的先例：樣板區段裡的陣列型屬性標記編譯不吃 MC4102）。
/// </remarks>
public sealed class HistogramChart : FrameworkElement
{
    /// <summary>要畫直方圖的樣本（時間序或任意序；順序不影響形狀）。</summary>
    public sealed record DistributionSeries(double[] Points);

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(DistributionSeries), typeof(HistogramChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>要畫直方圖的樣本；null 或空＝畫「沒有樣本」。</summary>
    public DistributionSeries? Values
    {
        get => (DistributionSeries?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public static readonly DependencyProperty BinCountProperty = DependencyProperty.Register(
        nameof(BinCount), typeof(int), typeof(HistogramChart),
        new FrameworkPropertyMetadata(16, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>分桶數（對數／等寬都用這個格數）。</summary>
    public int BinCount { get => (int)GetValue(BinCountProperty); set => SetValue(BinCountProperty, value); }

    public static readonly DependencyProperty LogBucketsProperty = DependencyProperty.Register(
        nameof(LogBuckets), typeof(bool), typeof(HistogramChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>true＝對數分桶（延遲分布的預設）；false＝等寬分桶。</summary>
    public bool LogBuckets { get => (bool)GetValue(LogBucketsProperty); set => SetValue(LogBucketsProperty, value); }

    public static readonly DependencyProperty ShowPercentilesProperty = DependencyProperty.Register(
        nameof(ShowPercentiles), typeof(bool), typeof(HistogramChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>是否疊 P50／P95／P99 標線。</summary>
    public bool ShowPercentiles { get => (bool)GetValue(ShowPercentilesProperty); set => SetValue(ShowPercentilesProperty, value); }

    public HistogramChart()
    {
        this.RepaintOnThemeChange();
        ClipToBounds = true;
        MinHeight = 120;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;

        var result = LatencyDistributionMath.Histogram(Values?.Points ?? [], BinCount, LogBuckets);
        if (result.Used == 0 || result.Buckets.Count == 0)
        {
            DrawEmpty(dc, w, h);
            return;
        }

        const double padLeft = 44, padRight = 8, padTop = 14, padBottom = 18;
        double plotW = Math.Max(1, w - padLeft - padRight), plotH = Math.Max(1, h - padTop - padBottom);
        var samples = LatencyDistributionMath.Clean(Values!.Points!);
        var (p50, p95, p99) = LatencyDistributionMath.Percentiles(samples);

        int maxCount = 0;
        foreach (var b in result.Buckets) maxCount = Math.Max(maxCount, b.Count);

        // 長條：以最多的一格為滿格（不然全部長條都短到看不出形狀）；畫面位置由格號決定（格子等寬）
        var fill = VizPalette.Accent;
        double binW = plotW / result.Buckets.Count;
        for (int i = 0; i < result.Buckets.Count; i++)
        {
            double bh = maxCount == 0 ? 0 : result.Buckets[i].Count / (double)maxCount * plotH;
            if (bh <= 0) continue;   // 空格不畫——但它的位置留著（快慢兩群之間的空白就是要看的東西）
            double x0 = padLeft + i * binW;
            dc.DrawRoundedRectangle(fill, null,
                new Rect(x0 + 1, padTop + plotH - bh, Math.Max(1, binW - 2), bh), 1, 1);
        }

        // 百分位標線：位置由 <see cref="LatencyDistributionMath.FractionOf"/> 按「落在第幾格」決定，
        // 對數布局才不會把標線畫到錯誤的位置上
        if (ShowPercentiles)
        {
            DrawPercentile(dc, p50, 0, result, padLeft, plotW, padTop, plotH);
            DrawPercentile(dc, p95, 1, result, padLeft, plotW, padTop, plotH);
            DrawPercentile(dc, p99, 2, result, padLeft, plotW, padTop, plotH);
        }

        // 座標基線與刻度
        var axis = new Pen(VizPalette.Grid, 1);
        axis.Freeze();
        dc.DrawLine(axis, new Point(padLeft, padTop + plotH), new Point(padLeft + plotW, padTop + plotH));
        if (maxCount > 1)
        {
            AxisText(dc, $"{maxCount / 2}", padLeft, padTop + plotH / 2, right: true);
            AxisText(dc, $"{maxCount}", padLeft, padTop, right: true);
        }
        AxisText(dc, LatencyDistributionMath.LatencyText(result.Buckets[0].From), padLeft, h - 14, right: false);
        string last = "≥ " + LatencyDistributionMath.LatencyText(result.Buckets[^1].From);
        dc.DrawText(Format(last, 9.5, VizPalette.Muted), new Point(padLeft + plotW - 64, h - 14));

        // 摘要小字（右上）：筆數與分桶方式——標線之外，這張圖怎麼算出來的也要說明白
        var note = Format($"{result.Used:N0} 筆 ・ {(LogBuckets ? "對數" : "等寬")}分桶", 9.5, VizPalette.Muted);
        dc.DrawText(note, new Point(Math.Max(padLeft, w - padRight - note.Width), padTop));
    }

    private void DrawPercentile(DrawingContext dc, double value, int slot,
        LatencyDistributionMath.HistogramResult result, double padLeft, double plotW, double padTop, double plotH)
    {
        double frac = LatencyDistributionMath.FractionOf(value, result.Buckets, result.Log);
        double x = LatencyDistributionMath.MapX(frac, padLeft, plotW);
        var pen = new Pen(VizPalette.Muted, 1) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        dc.DrawLine(pen, new Point(x, padTop), new Point(x, padTop + plotH));
        dc.DrawText(Format($"P{(slot == 0 ? "50" : slot == 1 ? "95" : "99")}"
            + $" {LatencyDistributionMath.LatencyText(value)}", 9.5, VizPalette.Muted),
            new Point(Math.Min(x + 3, padLeft + plotW - 78), padTop + slot * 12));
    }

    private void DrawEmpty(DrawingContext dc, double w, double h)
        => dc.DrawText(Format("沒有樣本", 12, VizPalette.Muted), new Point(w / 2 - 28, h / 2 - 8));

    private void AxisText(DrawingContext dc, string text, double x, double y, bool right)
    {
        var ft = Format(text, 9.5, VizPalette.Muted);
        dc.DrawText(ft, new Point(right ? x - ft.Width - 4 : x, y - 5));
    }

    private FormattedText Format(string text, double size, Brush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft JhengHei UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
}

/// <summary>
/// ECDF 圖（U10-VZ-0103，追蹤鍵 <c>viz.ecdf</c>）：經驗累積分布的階梯線——
/// 橫軸是樣本值、縱軸是「不大於該值的樣本比例」。P50／95／99 就是曲線過 0.5／0.95／0.99 的位置，
/// 與直方圖（<see cref="HistogramChart"/>）同一份樣本、同一套數學（<see cref="LatencyDistributionMath"/>）、
/// 同一個檔案家族。
/// </summary>
/// <remarks>
/// 階梯線只走「真的量到的樣本」：縱軸用最近排名（第 i 筆＝i/N），不內插出量不到的百分位。
/// 沒有樣本就畫「沒有樣本」。
/// </remarks>
public sealed class EcdfChart : FrameworkElement
{
    /// <summary>要畫 ECDF 的樣本。</summary>
    public sealed record DistributionSeries(double[] Points);

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(DistributionSeries), typeof(EcdfChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>要畫 ECDF 的樣本；null 或空＝畫「沒有樣本」。</summary>
    public DistributionSeries? Values
    {
        get => (DistributionSeries?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public EcdfChart()
    {
        this.RepaintOnThemeChange();
        ClipToBounds = true;
        MinHeight = 120;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;

        var pts = LatencyDistributionMath.Ecdf(Values?.Points ?? []);
        if (pts.Length == 0)
        {
            dc.DrawText(Format("沒有樣本", 12, VizPalette.Muted), new Point(w / 2 - 28, h / 2 - 8));
            return;
        }

        const double padLeft = 44, padRight = 8, padTop = 12, padBottom = 18;
        double plotW = Math.Max(1, w - padLeft - padRight), plotH = Math.Max(1, h - padTop - padBottom);

        double xMin = pts[0].X, xMax = pts[^1].X;
        double X(double v) => LatencyDistributionMath.MapX(xMax > xMin ? (v - xMin) / (xMax - xMin) : 0.5, padLeft, plotW);
        double Y(double p) => LatencyDistributionMath.MapY(p, 0, 1, padTop, plotH);

        var grid = new Pen(VizPalette.Grid, 1);
        grid.Freeze();

        // Y 軸刻度：0／50／100% 三道格線——ECDF 的縱軸就是比例，三道夠讀
        foreach (var (p, label) in new[] { (0.0, "0%"), (0.5, "50%"), (1.0, "100%") })
        {
            double y = Y(p);
            dc.DrawLine(grid, new Point(padLeft, y), new Point(padLeft + plotW, y));
            dc.DrawText(Format(label, 9.5, VizPalette.Muted), new Point(4, y - 6));
        }

        // 階梯線：每個點水平走到下一點的 X，再垂直升一格（經驗分布的標準畫法）
        var line = new Pen(VizPalette.Accent, 1.6);
        line.Freeze();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(padLeft, Y(0)), false, false);
            g.LineTo(new Point(X(pts[0].X), Y(0)), true, false);
            for (int i = 0; i < pts.Length; i++)
            {
                double px = X(pts[i].X);
                g.LineTo(new Point(px, Y(pts[i].P)), true, false);                       // 升到該值的累積比例
                if (i + 1 < pts.Length) g.LineTo(new Point(X(pts[i + 1].X), Y(pts[i].P)), true, false);   // 水平走到下一值
            }
            g.LineTo(new Point(padLeft + plotW, Y(1)), true, false);   // 尾端水平延伸到右緣（比例封頂 1）
        }
        geo.Freeze();
        dc.DrawGeometry(null, line, geo);

        var axis = new Pen(VizPalette.Grid, 1);
        axis.Freeze();
        dc.DrawLine(axis, new Point(padLeft, padTop + plotH), new Point(padLeft + plotW, padTop + plotH));
        dc.DrawText(Format(LatencyDistributionMath.LatencyText(xMin), 9.5, VizPalette.Muted), new Point(padLeft, h - 14));
        var last = Format(LatencyDistributionMath.LatencyText(xMax), 9.5, VizPalette.Muted);
        dc.DrawText(last, new Point(padLeft + plotW - last.Width, h - 14));
    }

    private FormattedText Format(string text, double size, Brush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft JhengHei UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
