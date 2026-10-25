using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 日曆熱圖的<b>純數學</b>（U10-VZ-0098，追蹤鍵 <c>viz.calendar.heatmap</c>）：
/// 月曆格的行列配置與強度換算。全部靜態純函式、不碰 WPF，
/// 用合成資料直接驗證（同 <see cref="LatencyDistributionMath"/> 的先例）。
/// </summary>
public static class CalendarHeatmapMath
{
    /// <summary>一個月有幾天（西元年月；輸入不合法夾進 1–12）。</summary>
    public static int DaysInMonth(int year, int month)
        => DateTime.DaysInMonth(year, Math.Clamp(month, 1, 12));

    /// <summary>
    /// 1 號落在第幾欄（週一第一欄）：週一＝0 … 週日＝6。
    /// 週一開欄讓「一週＝一列」與工作曆一致，週末自然靠在右邊兩格。
    /// </summary>
    public static int FirstColumn(int year, int month)
    {
        var dow = new DateTime(year, Math.Clamp(month, 1, 12), 1).DayOfWeek;   // Sunday=0 … Saturday=6
        return ((int)dow + 6) % 7;                                             // 換成週一＝0
    }

    /// <summary>這個月要佔幾列（幾週）。</summary>
    public static int WeekCount(int year, int month)
        => (FirstColumn(year, month) + DaysInMonth(year, month) + 6) / 7;

    /// <summary>日期 → (列, 欄)；欄＝星期（週一 0 起），列＝第幾週。</summary>
    public static (int Row, int Col) CellOf(DateOnly date)
    {
        var dow = date.DayOfWeek;
        return ((date.Day + FirstColumn(date.Year, date.Month) - 1) / 7, ((int)dow + 6) % 7);
    }

    /// <summary>強度（0–1）：以當月最大計數為滿格；沒有任何資料（max≤0）時全月 0——由呼叫端決定怎麼呈現「不知道」。</summary>
    public static double Intensity(int count, int max)
        => max <= 0 ? 0 : Math.Clamp(count / (double)max, 0, 1);

    /// <summary>把逐日計數建成日期 → 計數的索引（同日重複時取後者——呼叫端不該發生，發生了也如實以最後一筆為準）。</summary>
    public static IReadOnlyDictionary<DateOnly, int> Index(IReadOnlyList<SensorDashboardService.DailyCount> days)
    {
        var map = new Dictionary<DateOnly, int>();
        foreach (var d in days) map[d.Day] = d.Count;
        return map;
    }
}

/// <summary>
/// 日曆熱圖（U10-VZ-0098，追蹤鍵 <c>viz.calendar.heatmap</c>）：一個月的月曆格，
/// 格的顏色深淺＝當日異常（警告以上）事件數。當機、節流、WHEA 這類「有發生才有一筆」的歷史，
/// 攤在日曆上比折線好讀——哪幾天集中、是不是越來越密，一眼可見。
/// </summary>
/// <remarks>
/// <para>
/// <b>空日留白：</b>逐日計數由呼叫端傳入，只有「有事件的日」才有格子顏色——
/// 程式沒在跑的日子我們不知道有沒有異常，留白是誠實，補 0 是編造。
/// 月外的空格與當月還沒到的日子也一樣留白。
/// </para>
/// <para>
/// 強度色只用<b>單一色相的深淺</b>（強調色＋透明度）：異常計數不是溫度，
/// 不套 <see cref="HeatScale"/> 的溫度色階——把「幾件」說成「幾度」是錯的語意。
/// </para>
/// </remarks>
public sealed class CalendarHeatmapChart : FrameworkElement
{
    /// <summary>DP 包裝：逐日異常計數（呼叫端傳入）。</summary>
    public sealed class CalendarDays
    {
        public CalendarDays(IReadOnlyList<SensorDashboardService.DailyCount> days) => Items = days;
        public IReadOnlyList<SensorDashboardService.DailyCount> Items { get; }
    }

    private static readonly string[] WeekdayHeads = ["一", "二", "三", "四", "五", "六", "日"];

    public static readonly DependencyProperty MonthProperty = DependencyProperty.Register(
        nameof(Month), typeof(DateTime), typeof(CalendarHeatmapChart),
        new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>要畫的月份（任取該月一天）。</summary>
    public DateTime Month { get => (DateTime)GetValue(MonthProperty); set => SetValue(MonthProperty, value); }

    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(CalendarDays), typeof(CalendarHeatmapChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>逐日異常計數；null＝全月留白。</summary>
    public CalendarDays? Days
    {
        get => (CalendarDays?)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    public static readonly DependencyProperty KindLabelProperty = DependencyProperty.Register(
        nameof(KindLabel), typeof(string), typeof(CalendarHeatmapChart),
        new FrameworkPropertyMetadata("異常事件", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>計數的語意（如「異常事件」），供標題與圖例說明。</summary>
    public string KindLabel { get => (string)GetValue(KindLabelProperty); set => SetValue(KindLabelProperty, value); }

    public CalendarHeatmapChart()
    {
        this.RepaintOnThemeChange();
        ClipToBounds = true;
        MinHeight = 120;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 100 || h < 60) return;

        int year = Month.Year, month = Math.Clamp(Month.Month, 1, 12);
        var counts = CalendarHeatmapMath.Index(Days?.Items ?? []);
        int max = counts.Count == 0 ? 0 : counts.Values.Max();

        const double padLeft = 22, padRight = 8, padTop = 16, padBottom = 14;
        double headH = 14;
        var area = new Rect(padLeft, padTop + headH, Math.Max(1, w - padLeft - padRight),
            Math.Max(1, h - padTop - headH - padBottom));

        // 標題：年月與總數——「怎麼算出來的」一併說明（只計有事件的日）
        int total = counts.Values.Sum();
        var head = Format($"{year} 年 {month} 月 ・ {KindLabel}合計 {total}", 10.5, VizPalette.Ink);
        dc.DrawText(head, new Point(padLeft, 2));

        // 星期表頭（週一在左）
        double colW = area.Width / 7;
        for (int i = 0; i < 7; i++)
        {
            var ft = Format(WeekdayHeads[i], 9, VizPalette.Muted);
            dc.DrawText(ft, new Point(area.X + i * colW + colW / 2 - ft.Width / 2, area.Y - headH + 1));
        }

        int weeks = Math.Max(1, CalendarHeatmapMath.WeekCount(year, month));
        double cellH = area.Height / weeks;
        double cellW = colW;
        double inset = Math.Min(3, Math.Min(cellW, cellH) * 0.12);

        var hairline = new Pen(VizPalette.Hairline, 1);
        hairline.Freeze();

        for (int day = 1; day <= CalendarHeatmapMath.DaysInMonth(year, month); day++)
        {
            var date = new DateOnly(year, month, day);
            var (row, col) = CalendarHeatmapMath.CellOf(date);
            var cell = new Rect(area.X + col * cellW + inset, area.Y + row * cellH + inset,
                Math.Max(1, cellW - inset * 2), Math.Max(1, cellH - inset * 2));

            if (counts.TryGetValue(date, out int count))
            {
                // 有事件的日：單一色相的深淺＝強度；深淺以當月最大計數為滿格
                double intensity = CalendarHeatmapMath.Intensity(count, max);
                var fill = VizPalette.Accent.Clone();
                fill.Opacity = 0.25 + 0.75 * intensity;
                fill.Freeze();
                dc.DrawRoundedRectangle(fill, null, cell, 2, 2);
            }
            else
            {
                // 空日（沒有事件／沒有資料）：只留框線標示「這一天存在」，不著色
                dc.DrawRoundedRectangle(null, hairline, cell, 2, 2);
            }

            var num = Format($"{day}", 8.5, VizPalette.Muted);
            dc.DrawText(num, new Point(cell.X + 2, cell.Y + 1));
        }

        // 圖例：少 → 多（單色相深淺，不是溫度色階）
        double ly = h - 11;
        double lx = padLeft;
        var cap0 = Format($"{KindLabel}少", 9, VizPalette.Muted);
        dc.DrawText(cap0, new Point(lx, ly - 2));
        lx += cap0.Width + 4;
        for (int i = 0; i < 5; i++)
        {
            var fill = VizPalette.Accent.Clone();
            fill.Opacity = 0.25 + 0.75 * i / 4.0;
            fill.Freeze();
            dc.DrawRectangle(fill, null, new Rect(lx, ly, 14, 7));
            lx += 16;
        }
        dc.DrawText(Format("多", 9, VizPalette.Muted), new Point(lx + 2, ly - 2));
    }

    private FormattedText Format(string text, double size, Brush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft JhengHei UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
