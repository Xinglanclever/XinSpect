using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 子彈圖的<b>純數學</b>（U10-VZ-0022，追蹤鍵 <c>viz.bullet</c>）：
/// 值→座標映射與規格帶的跨度。全部靜態純函式、不碰 WPF，
/// 用合成資料直接驗證（同 <see cref="LatencyDistributionMath"/> 的先例）。
/// </summary>
public static class BulletChartMath
{
    /// <summary>值 → X 座標：<see cref="min"/> 貼左緣、<see cref="max"/> 貼右緣，區間外夾限（畫不出繪圖區就不畫出繪圖區）。</summary>
    public static double MapX(double v, double min, double max, double left, double width)
    {
        if (width <= 0) return left;
        if (max <= min) return left;
        return left + Math.Clamp((v - min) / (max - min), 0, 1) * width;
    }

    /// <summary>一段規格帶的左右跨度（已夾進量測軸的範圍——帶比軸寬時只畫軸內的部分）。</summary>
    public static (double X0, double X1) BandSpan(SensorDashboardService.SpecBand band,
        double min, double max, double left, double width)
        => (MapX(band.Lo, min, max, left, width), MapX(band.Hi, min, max, left, width));

    /// <summary>子彈圖的量測軸：以規格帶與量測值為準取包絡，再留 5% 邊距——軸太窄會把帶畫成滿格，帶就失去「區間」的意義。</summary>
    public static (double Min, double Max) Scale(IReadOnlyList<SensorDashboardService.SpecBand> bands,
        double? value, double? target)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var b in bands) { lo = Math.Min(lo, b.Lo); hi = Math.Max(hi, b.Hi); }
        if (value is { } v) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        if (target is { } t) { lo = Math.Min(lo, t); hi = Math.Max(hi, t); }
        if (lo > hi) return (0, 1);                       // 沒有帶也沒有值：給一個中性的 0–1 軸
        if (hi - lo < 1e-9) return (lo - Math.Abs(lo) - 1, hi + Math.Abs(hi) + 1);   // 全段等值：撐開避免除零
        double pad = (hi - lo) * 0.05;
        return (lo - pad, hi + pad);
    }
}

/// <summary>
/// 子彈圖（U10-VZ-0022，追蹤鍵 <c>viz.bullet</c>）：一條值條疊在規格區間帶上、目標刻度一根線——
/// 「現在多少、在規格的哪一段」一眼看完，不用讀者自己拿量測值去比規格表。
/// </summary>
/// <remarks>
/// <para>
/// <b>規格區間由呼叫端傳入，本控制項不內建任何規格值。</b>控制項只負責把帶、條、刻度放到
/// 同一條軸上；哪一段算合格是 ATX／CPU 廠商的規格，寫死在控制項裡就會在規格改版時變成謊言。
/// </para>
/// <para>
/// <b>誠實原則：</b>沒有讀值（<see cref="Value"/>＝null）時畫「沒有讀值」，
/// 不畫一條貼左緣的空條冒充量到 0。
/// </para>
/// </remarks>
public sealed class BulletChart : FrameworkElement
{
    /// <summary>DP 包裝：規格帶清單（呼叫端從外層傳入，順序＝繪製順序，建議外寬內窄、後畫的疊在上面）。</summary>
    public sealed class BulletBands
    {
        public BulletBands(IReadOnlyList<SensorDashboardService.SpecBand> bands) => Items = bands;
        public IReadOnlyList<SensorDashboardService.SpecBand> Items { get; }
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(BulletChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>這一列的名稱（如電源軌名）。</summary>
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public static readonly DependencyProperty BandsProperty = DependencyProperty.Register(
        nameof(Bands), typeof(BulletBands), typeof(BulletChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>規格區間帶（呼叫端傳入）；null＝只畫值條與軸。</summary>
    public BulletBands? Bands
    {
        get => (BulletBands?)GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double?), typeof(BulletChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>量測值；null＝畫「沒有讀值」。</summary>
    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(double?), typeof(BulletChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>目標刻度（標稱值）；null＝不畫。</summary>
    public double? Target { get => (double?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(BulletChart),
        new FrameworkPropertyMetadata("V", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>單位（只影響數字標示，不影響幾何）。</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    public BulletChart()
    {
        this.RepaintOnThemeChange();
        ClipToBounds = true;
        MinHeight = 34;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 80 || h < 20) return;

        var bands = Bands?.Items ?? [];
        const double padLeft = 96, padRight = 8;
        double barTop = h * 0.22, barH = Math.Max(6, h * 0.56);
        double plotW = Math.Max(1, w - padLeft - padRight);
        var (min, max) = BulletChartMath.Scale(bands, Value, Target);
        double barY = barTop + barH / 2;

        // 標題（列名）：左側一欄；過長由 ClipToBounds 裁切，不擠壓繪圖區
        var title = Format(Title, 11, VizPalette.Ink);
        dc.DrawText(title, new Point(8, barY - title.Height / 2));

        // 規格帶：由外而內逐層加深（呼叫端給的順序），強調色透明度分層、不另造色階
        for (int i = 0; i < bands.Count; i++)
        {
            var (x0, x1) = BulletChartMath.BandSpan(bands[i], min, max, padLeft, plotW);
            if (x1 <= x0) continue;
            var fill = VizPalette.Accent.Clone();
            fill.Opacity = 0.10 + 0.10 * i;
            fill.Freeze();
            dc.DrawRoundedRectangle(fill, null, new Rect(x0, barTop, x1 - x0, barH), 2, 2);
        }

        // 值條：量測值為準；沒有讀值就不畫條——空白本身是陳述
        if (Value is { } v)
        {
            double x = BulletChartMath.MapX(v, min, max, padLeft, plotW);
            var bar = new Rect(padLeft, barTop + barH * 0.18, Math.Max(2, x - padLeft), barH * 0.64);
            dc.DrawRoundedRectangle(VizPalette.Accent, null, bar, 2, 2);
        }

        // 目標刻度：一根豎線（標稱值）
        if (Target is { } t)
        {
            double tx = BulletChartMath.MapX(t, min, max, padLeft, plotW);
            var pen = new Pen(VizPalette.Ink, 1.6);
            pen.Freeze();
            dc.DrawLine(pen, new Point(tx, barTop - 3), new Point(tx, barTop + barH + 3));
        }

        // 軸端標示與讀值
        var lo = Format(Text(min), 9, VizPalette.Muted);
        dc.DrawText(lo, new Point(padLeft, h - 13));
        var hi = Format(Text(max), 9, VizPalette.Muted);
        dc.DrawText(hi, new Point(padLeft + plotW - hi.Width, h - 13));

        string reading = Value is { } val ? Text(val) : "沒有讀值";
        var vt = Format(reading, 10.5, Value is null ? VizPalette.Muted : VizPalette.Ink);
        dc.DrawText(vt, new Point(Math.Min(padLeft + plotW, w - padRight) - vt.Width, Math.Max(0, barTop - 14)));
    }

    private string Text(double v)
        => Math.Abs(v) >= 100 ? $"{v:0}" : Math.Abs(v) >= 10 ? $"{v:0.#}" : $"{v:0.###}";

    private FormattedText Format(string text, double size, Brush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft JhengHei UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
