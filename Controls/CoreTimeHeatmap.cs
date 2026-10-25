using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 核×時間熱圖的<b>純數學</b>（U10-VZ-0004，追蹤鍵 <c>viz.heatmap.coretime</c>）：
/// 把取樣序列壓進固定欄數、算格子矩形。全部靜態純函式、不碰 WPF——
/// 座標與壓縮算式要能被單元測試用合成資料直接驗證（同 <see cref="LatencyDistributionMath"/> 的先例：
/// 版面演算法與畫法分家，測試盯演算法，眼睛盯畫法）。
/// </summary>
public static class CoreTimeHeatmapMath
{
    /// <summary>熱圖的一列：核心標籤與（可能含缺格的）取樣序列；<c>null</c>＝那一拍沒有讀值。</summary>
    public sealed record CoreTimeHeatmapRow(string Label, double?[] Values);

    /// <summary>
    /// 把一列取樣壓進 <paramref name="columns"/> 欄：每欄取<b>非缺格樣本的平均</b>；
    /// 欄內全是缺格（那一整段都沒有讀值）時該欄仍是 <c>null</c>——缺格壓縮後還是缺格，不冒充 0。
    /// 序列比欄數短時原樣補齊缺格到欄數（時間軸長度如實呈現，不放大圖形）。
    /// </summary>
    public static double?[] Compress(double?[] values, int columns)
    {
        int cols = Math.Max(1, columns);
        int n = values.Length;
        if (n <= cols)
        {
            var pad = new double?[cols];
            for (int i = 0; i < n; i++) pad[i] = values[i];
            return pad;
        }

        var outCol = new double?[cols];
        for (int c = 0; c < cols; c++)
        {
            int s = (int)((long)c * n / cols);
            int e = (int)((long)(c + 1) * n / cols);
            if (e <= s) e = s + 1;
            if (e > n) e = n;
            double sum = 0; int used = 0;
            for (int i = s; i < e; i++)
                if (values[i] is double v) { sum += v; used++; }
            outCol[c] = used > 0 ? sum / used : null;
        }
        return outCol;
    }

    /// <summary>格子矩形：第 <paramref name="col"/> 欄、第 <paramref name="row"/> 列在繪圖區 <paramref name="area"/> 內的位置，
    /// 格與格之間留 <paramref name="gap"/> 的縫（缺格時縫裡就是底色，「留白」看得出來）。</summary>
    public static Rect CellRect(int col, int row, int cols, int rows, Rect area, double gap)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        double w = (area.Width - gap * (cols - 1)) / cols;
        double h = (area.Height - gap * (rows - 1)) / rows;
        return new Rect(area.X + col * (w + gap), area.Y + row * (h + gap),
            Math.Max(0, w), Math.Max(0, h));
    }

    /// <summary>欄的時間比例（0＝最舊、1＝現在），供時間標籤定位。</summary>
    public static double ColumnFraction(int col, int cols)
        => cols <= 1 ? 0 : Math.Clamp(col / (double)(cols - 1), 0, 1);
}

/// <summary>
/// 核×時間熱圖（U10-VZ-0004，追蹤鍵 <c>viz.heatmap.coretime</c>）：橫軸時間（左舊右新）、
/// 縱軸核心、色階＝溫度。色階<b>只有一份</b>——全部經 <see cref="HeatScale"/> 取色，
/// 與核心熱區圖、逐核液柱、圖例同一來源，同一溫度在畫面上任何地方都是同一個顏色
/// （v2.43 出過第二份色階的教訓，這裡不再犯）。
/// </summary>
/// <remarks>
/// <b>缺格留白不冒充：</b><c>null</c> 的格子什麼都不畫——那一拍那顆核心沒有讀值是事實，
/// 蓋一個「看起來像資料」的顏色等於編造量測。整張圖沒有任何讀值時畫「沒有樣本」。
/// </remarks>
public sealed class CoreTimeHeatmapChart : FrameworkElement
{
    /// <summary>DP 包裝（樣板區段裡的清單型屬性標記編譯不吃 MC4102，照 <see cref="Sparkline.SparkSeries"/> 先例包一層）。</summary>
    public sealed class HeatRows
    {
        public HeatRows(IReadOnlyList<CoreTimeHeatmapMath.CoreTimeHeatmapRow> rows) => Items = rows;
        public IReadOnlyList<CoreTimeHeatmapMath.CoreTimeHeatmapRow> Items { get; }
    }

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(HeatRows), typeof(CoreTimeHeatmapChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>熱圖的列；null 或全空＝畫「沒有樣本」。</summary>
    public HeatRows? Rows
    {
        get => (HeatRows?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(CoreTimeHeatmapChart),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>時間欄數；列的取樣比這多時壓縮（<see cref="CoreTimeHeatmapMath.Compress"/>）。</summary>
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    public CoreTimeHeatmapChart()
    {
        this.RepaintOnThemeChange();
        ClipToBounds = true;
        MinHeight = 90;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 40) return;

        var rows = Rows?.Items ?? [];
        if (rows.Count == 0)
        {
            DrawEmpty(dc, w, h);
            return;
        }

        int cols = Math.Max(1, Columns);
        var grid = new (string Label, double?[] Values)[rows.Count];
        bool anyValue = false;
        for (int r = 0; r < rows.Count; r++)
        {
            var compressed = CoreTimeHeatmapMath.Compress(rows[r].Values, cols);
            grid[r] = (rows[r].Label, compressed);
            if (!anyValue && compressed.Any(v => v is not null)) anyValue = true;
        }
        if (!anyValue)
        {
            DrawEmpty(dc, w, h);
            return;
        }

        const double padLeft = 58, padRight = 8, padTop = 18, padBottom = 30;
        var area = new Rect(padLeft, padTop, Math.Max(1, w - padLeft - padRight),
            Math.Max(1, h - padTop - padBottom));
        double gap = 1;

        // 格子：色階一律問 HeatScale（單一來源）；null 格不畫——留白就是「沒有讀值」
        for (int r = 0; r < grid.Length; r++)
        {
            var values = grid[r].Values;
            for (int c = 0; c < values.Length; c++)
            {
                if (values[c] is not double v) continue;
                var rect = CoreTimeHeatmapMath.CellRect(c, r, cols, grid.Length, area, gap);
                if (rect.Width <= 0.5 || rect.Height <= 0.5) continue;
                dc.DrawRectangle(HeatScale.BrushFor(v), null, rect);
            }
        }

        // 列標籤（核心名）：右對齊貼著繪圖區左緣
        for (int r = 0; r < grid.Length; r++)
        {
            double cellH = (area.Height - gap * (grid.Length - 1)) / grid.Length;
            double y = area.Y + r * (cellH + gap) + cellH / 2;
            var ft = Format(grid[r].Label, 9.5, VizPalette.Muted);
            dc.DrawText(ft, new Point(padLeft - ft.Width - 5, y - ft.Height / 2));
        }

        // 時間方向標示：左舊右新（畫在底軸下方）
        var old = Format("較早", 9.5, VizPalette.Muted);
        dc.DrawText(old, new Point(padLeft, h - 14));
        var now = Format("現在", 9.5, VizPalette.Muted);
        dc.DrawText(now, new Point(w - padRight - now.Width, h - 14));

        // 色階圖例：由 HeatScale.Legend 導出，溫度文字與色塊同源，不另抄一份溫度表（畫在頂部右側）
        DrawLegend(dc, w);
    }

    private void DrawLegend(DrawingContext dc, double w)
    {
        var legend = HeatScale.Legend;
        double sw = 16, sh = 7;
        double totalW = legend.Count * sw + 18;
        double x = Math.Max(58, w - 8 - totalW);
        double y = 3;
        foreach (var tick in legend)
        {
            var brush = HeatScale.BrushFor(tick.Temperature);
            if (brush is not null) dc.DrawRectangle(brush, null, new Rect(x, y, sw, sh));
            x += sw;
        }
        dc.DrawText(Format("°C", 9, VizPalette.Muted), new Point(x + 3, y - 2));
    }

    private void DrawEmpty(DrawingContext dc, double w, double h)
        => dc.DrawText(Format("沒有樣本", 12, VizPalette.Muted), new Point(w / 2 - 28, h / 2 - 8));

    private FormattedText Format(string text, double size, Brush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft JhengHei UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
