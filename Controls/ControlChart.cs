using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>控制圖的中心線統計量（呼叫端選）。</summary>
public enum SpcCenter
{
    /// <summary>中位數——穩健，少數尖峰不會把中心線拖走。</summary>
    Median,
    /// <summary>均值（x̄）——傳統 Shewhart 的選擇。</summary>
    Mean,
}

/// <summary>
/// 控制圖（SPC）的<b>純數學</b>（U10-VZ-0105，追蹤鍵 <c>viz.controlchart</c>）：
/// 中心線、控制限與越界判定。全部靜態純函式、不碰 WPF，
/// 用合成資料直接驗證（同 <see cref="LatencyDistributionMath"/> 的先例）。
/// </summary>
/// <remarks>
/// <para>
/// 統計概念與 <see cref="TrendSentinel"/> 同一個家族：中心線是<b>描述</b>（序列自己的中位數或均值），
/// 控制限是「這個序列自己的 ±kσ」——<b>不是規格限</b>，更不是好壞判決。
/// 一個溫度序列自己的 3σ 帶，只說「離自己平常的樣子有多遠」；是否該清理風扇、是否在規格內，
/// 是另外的問題（規格請看子彈圖，趨勢請看統計哨兵）。
/// </para>
/// <para>
/// σ 用<b>樣本標準差</b>（n−1）；序列少於 2 點或全段等值時 σ＝0，控制限退化成中心線一條——
/// 這時圖上照實標「樣本不足／無離散度」，不假裝有一條有意義的控制帶。
/// </para>
/// </remarks>
public static class SpcMath
{
    /// <summary>預設控制限寬度（±3σ，Shewhart 慣例）。</summary>
    public const double DefaultK = 3.0;

    /// <summary>一張控制圖的統計骨架：中心線、上下控制限與方法的如實標示。</summary>
    /// <param name="Center">中心線（中位數或均值，依呼叫端選擇）。</param>
    /// <param name="Lower">下控制限；σ＝0 時等於中心線。</param>
    /// <param name="Upper">上控制限；σ＝0 時等於中心線。</param>
    /// <param name="Sigma">樣本標準差（n−1）。</param>
    /// <param name="MethodLabel">統計方法的說明（畫在圖上——讀者不必猜這帶怎麼來的）。</param>
    public sealed record Limits(double Center, double Lower, double Upper, double Sigma, string MethodLabel);

    /// <summary>中位數（輸入不必預排序；奇數取中間、偶數取中間兩數平均）。</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) throw new ArgumentException("沒有樣本，中位數無從談起。", nameof(values));
        var sorted = values.OrderBy(v => v).ToArray();
        int n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    /// <summary>均值。</summary>
    public static double Mean(IReadOnlyList<double> values)
    {
        if (values.Count == 0) throw new ArgumentException("沒有樣本，均值無從談起。", nameof(values));
        double sum = 0;
        foreach (double v in values) sum += v;
        return sum / values.Count;
    }

    /// <summary>樣本標準差（n−1 分母）；n&lt;2 或全段等值回 0。</summary>
    public static double Sigma(IReadOnlyList<double> values)
    {
        int n = values.Count;
        if (n < 2) return 0;
        double mean = Mean(values);
        double ss = 0;
        foreach (double v in values) ss += (v - mean) * (v - mean);
        return Math.Sqrt(ss / (n - 1));
    }

    /// <summary>
    /// 算控制限。中心線由 <paramref name="mode"/> 選（中位數或均值），σ 一律樣本標準差；
    /// 計算量 O(n log n)（排序），歷史倉降採樣後的數百點在可預期成本內。
    /// </summary>
    public static Limits Compute(IReadOnlyList<double> series, SpcCenter mode, double k = DefaultK)
    {
        if (series.Count == 0) throw new ArgumentException("沒有樣本，控制限無從談起。", nameof(series));
        double width = Math.Max(0, k);
        double sigma = Sigma(series);
        double center = mode == SpcCenter.Mean ? Mean(series) : Median(series);
        string centerName = mode == SpcCenter.Mean ? "均值" : "中位數";
        string label = sigma > 0
            ? $"中心線＝{centerName} ・ 控制限＝±{width:0.#}σ（樣本標準差）"
            : $"中心線＝{centerName} ・ 樣本不足或無離散度，控制限退化成中心線";
        return new Limits(center, center - width * sigma, center + width * sigma, sigma, label);
    }

    /// <summary>越界點的索引（嚴格出界才算——恰好在限上是「在界內」，Shewhart 慣例）。</summary>
    public static int[] Outliers(IReadOnlyList<double> series, Limits lim)
    {
        var hits = new List<int>();
        for (int i = 0; i < series.Count; i++)
            if (series[i] < lim.Lower || series[i] > lim.Upper) hits.Add(i);
        return [.. hits];
    }
}

/// <summary>
/// 控制圖（U10-VZ-0105，追蹤鍵 <c>viz.controlchart</c>）：序列折線＋中心線＋±kσ 控制限，
/// 越界點畫記號。<b>圖上直接寫出統計方法</b>（中心線是什麼、限怎麼來的），
/// 並標明這是「離自己平常樣子有多遠」的監看——不是規格判定，不替硬體下結論。
/// </summary>
public sealed class ControlChart : FrameworkElement
{
    /// <summary>要走勢的樣本（DP 包裝，同 <see cref="Sparkline.SparkSeries"/> 先例）。</summary>
    public sealed record SpcSeries(double[] Points);

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(SpcSeries), typeof(ControlChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>要走勢的樣本；null 或空＝畫「沒有樣本」。</summary>
    public SpcSeries? Points
    {
        get => (SpcSeries?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public static readonly DependencyProperty CenterModeProperty = DependencyProperty.Register(
        nameof(CenterMode), typeof(SpcCenter), typeof(ControlChart),
        new FrameworkPropertyMetadata(SpcCenter.Median, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>中心線統計量（中位數或均值）。</summary>
    public SpcCenter CenterMode { get => (SpcCenter)GetValue(CenterModeProperty); set => SetValue(CenterModeProperty, value); }

    public static readonly DependencyProperty KProperty = DependencyProperty.Register(
        nameof(K), typeof(double), typeof(ControlChart),
        new FrameworkPropertyMetadata(SpcMath.DefaultK, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>控制限寬度（±kσ）。</summary>
    public double K { get => (double)GetValue(KProperty); set => SetValue(KProperty, value); }

    public static readonly DependencyProperty SeriesTitleProperty = DependencyProperty.Register(
        nameof(SeriesTitle), typeof(string), typeof(ControlChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>序列名稱（如「處理器溫度」），畫在方法標示旁。</summary>
    public string SeriesTitle { get => (string)GetValue(SeriesTitleProperty); set => SetValue(SeriesTitleProperty, value); }

    public ControlChart()
    {
        this.RepaintOnThemeChange();
        ClipToBounds = true;
        MinHeight = 120;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 50) return;

        var series = Points?.Points ?? [];
        if (series.Length == 0)
        {
            dc.DrawText(Format("沒有樣本", 12, VizPalette.Muted), new Point(w / 2 - 28, h / 2 - 8));
            return;
        }

        var lim = SpcMath.Compute(series, CenterMode, K);

        // 縱軸範圍：樣本與控制限的包絡（限退化成中心線時自然跟著收縮）
        double lo = Math.Min(lim.Lower, series.Min()), hi = Math.Max(lim.Upper, series.Max());
        if (hi - lo < 1e-9) { lo -= 1; hi += 1; }

        const double padLeft = 46, padRight = 8, padTop = 16, padBottom = 14;
        double plotW = Math.Max(1, w - padLeft - padRight), plotH = Math.Max(1, h - padTop - padBottom);
        double X(int i) => padLeft + (series.Length == 1 ? plotW / 2 : i / (double)(series.Length - 1) * plotW);
        double Y(double v) => padTop + plotH - Math.Clamp((v - lo) / (hi - lo), 0, 1) * plotH;

        var grid = new Pen(VizPalette.Grid, 1);
        grid.Freeze();

        // 控制限（虛線）與中心線（虛線、更淡）——都在樣本自己的尺度上
        var limitPen = new Pen(VizPalette.Of("SeriousBrush", "#ec835a"), 1) { DashStyle = DashStyles.Dash };
        limitPen.Freeze();
        var centerPen = new Pen(VizPalette.Muted, 1) { DashStyle = DashStyles.Dot };
        centerPen.Freeze();
        if (lim.Sigma > 0)
        {
            dc.DrawLine(limitPen, new Point(padLeft, Y(lim.Upper)), new Point(padLeft + plotW, Y(lim.Upper)));
            dc.DrawLine(limitPen, new Point(padLeft, Y(lim.Lower)), new Point(padLeft + plotW, Y(lim.Lower)));
        }
        dc.DrawLine(centerPen, new Point(padLeft, Y(lim.Center)), new Point(padLeft + plotW, Y(lim.Center)));

        // 序列折線
        var line = new Pen(VizPalette.Accent, 1.4);
        line.Freeze();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(X(0), Y(series[0])), false, false);
            for (int i = 1; i < series.Length; i++) g.LineTo(new Point(X(i), Y(series[i])), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, line, geo);

        // 越界點：畫記號（警戒色圓點）——「離自己的 ±kσ 遠」，不是「壞」
        var outliers = SpcMath.Outliers(series, lim);
        if (outliers.Length > 0)
        {
            var mark = new Pen(VizPalette.Of("CriticalBrush", "#d03b3b"), 1.8);
            mark.Freeze();
            foreach (int i in outliers)
                dc.DrawEllipse(null, mark, new Point(X(i), Y(series[i])), 3.5, 3.5);
        }

        // 軸端值
        AxisText(dc, Text(lo), padLeft, h - 4, left: true);
        var hiT = Format(Text(hi), 9, VizPalette.Muted);
        dc.DrawText(hiT, new Point(padLeft, padTop - hiT.Height + 2));

        // 標題與方法標示：序列名＋統計方法＋越界數。方法要寫明白，
        // 並聲明這是統計監看不是規格判定——不把 ±kσ 說成「合格／不合格」。
        var head = Format(
            (string.IsNullOrWhiteSpace(SeriesTitle) ? "" : SeriesTitle + " ・ ")
            + lim.MethodLabel
            + (outliers.Length > 0 ? $" ・ 越界 {outliers.Length} 點" : " ・ 無越界"),
            9.5, VizPalette.Muted);
        dc.DrawText(head, new Point(padLeft, 2));
        var verdict = Format("統計監看：離序列自己的平常樣子有多遠——不是規格判定", 9, VizPalette.Muted);
        dc.DrawText(verdict, new Point(Math.Max(padLeft, w - padRight - verdict.Width), padTop + plotH - verdict.Height));
    }

    private void AxisText(DrawingContext dc, string text, double x, double y, bool left)
    {
        var ft = Format(text, 9, VizPalette.Muted);
        dc.DrawText(ft, new Point(left ? x : x - ft.Width, y - ft.Height));
    }

    private string Text(double v)
        => Math.Abs(v) >= 100 ? $"{v:0}" : Math.Abs(v) >= 10 ? $"{v:0.#}" : $"{v:0.###}";

    private FormattedText Format(string text, double size, Brush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft JhengHei UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
