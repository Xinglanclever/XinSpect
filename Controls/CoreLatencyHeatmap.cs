using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 核心到核心延遲熱圖：N×N 方陣，格色由最小（綠）經中位（琥珀）到最大（紅）分段內插——
/// 以中位數為中點可避免少數離群值把整張圖壓成同一色。對角線（自己對自己）為深色且不參與配色。
/// 滑鼠停留顯示該格的「LP↔LP 與 ns 讀值」（寫入 <see cref="HoverText"/>，卡片上以文字呈現）。
/// </summary>
public sealed class CoreLatencyHeatmap : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(double[,]), typeof(CoreLatencyHeatmap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>延遲矩陣（ns，往返）；對角線為 NaN。</summary>
    public double[,]? Data { get => (double[,])GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public static readonly DependencyProperty LpsProperty = DependencyProperty.Register(
        nameof(Lps), typeof(int[]), typeof(CoreLatencyHeatmap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>行列對應的邏輯處理器編號（供標籤與滑鼠讀值）。</summary>
    public int[]? Lps { get => (int[])GetValue(LpsProperty); set => SetValue(LpsProperty, value); }

    public static readonly DependencyProperty HoverTextProperty = DependencyProperty.Register(
        nameof(HoverText), typeof(string), typeof(CoreLatencyHeatmap), new FrameworkPropertyMetadata(""));
    public string HoverText { get => (string)GetValue(HoverTextProperty); set => SetValue(HoverTextProperty, value); }

    private int _hoverA = -1, _hoverB = -1;   // 目前停留的格（畫高亮框用）
    private double _originX, _originY, _cell;

    public CoreLatencyHeatmap()
    {
        this.RepaintOnThemeChange();
        ClipToBounds = true;
        MinHeight = 240;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var m = Data;
        int n = m?.GetLength(0) ?? 0;
        var backdrop = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x23));
        backdrop.Freeze();
        dc.DrawRectangle(backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (m is null || n < 2) return;

        double lo = double.MaxValue, hi = double.MinValue;
        var vals = new List<double>();
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (i != j && double.IsFinite(m[i, j])) { vals.Add(m[i, j]); if (m[i, j] < lo) lo = m[i, j]; if (m[i, j] > hi) hi = m[i, j]; }
        if (vals.Count == 0) return;
        vals.Sort();
        double med = vals.Count % 2 == 1 ? vals[vals.Count / 2] : (vals[vals.Count / 2 - 1] + vals[vals.Count / 2]) / 2.0;

        const int LabelW = 34;   // 左／上標籤欄
        double availW = Math.Max(0, ActualWidth - LabelW - 6);
        double availH = Math.Max(0, ActualHeight - LabelW - 24);   // 底部留圖例
        double cell = Math.Max(3, Math.Floor(Math.Min(availW, availH) / n));
        _cell = cell;
        _originX = LabelW + 4;
        _originY = LabelW + 4;

        var typeface = new Typeface("Microsoft JhengHei UI");
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 標籤：格太小時隔列／隔欄標
        int step = (int)Math.Ceiling(n / (cell >= 15 ? n : (cell >= 8 ? n / 2.0 : n / 4.0)));
        var labelFg = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA)); labelFg.Freeze();
        // Data 與 Lps 是兩個獨立的相依屬性，中間可能被夾進一次繪製；長度不符時寧可不標，
        // 也不要標錯的編號（更不要越界）。下一次兩者都到位的繪製會補上。
        var lps = HeatmapMath.LabelsFor(Lps, n);
        for (int i = 0; lps is not null && i < n; i += Math.Max(1, step))
        {
            string s = lps[i].ToString();
            dc.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, 9, labelFg, dip),
                new Point(_originX + i * cell + cell / 2 - 4, 2));
            dc.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, 9, labelFg, dip),
                new Point(2, _originY + i * cell + cell / 2 - 6));
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double v = m[i, j];
                Brush fill;
                if (i == j || !double.IsFinite(v))
                    fill = new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x31));
                else
                    fill = new SolidColorBrush(LatencyColor(Normalize(v, lo, med, hi)));
                fill.Freeze();
                var rect = new Rect(_originX + j * cell, _originY + i * cell, cell - 1, cell - 1);
                dc.DrawRoundedRectangle(fill, null, rect, 2, 2);
            }
        }

        // 滑鼠停留高亮
        if (_hoverA >= 0 && _hoverA < n && _hoverB >= 0 && _hoverB < n)
        {
            var hiPen = new Pen(Brushes.White, 1.5);
            dc.DrawRectangle(null, hiPen, new Rect(
                _originX + _hoverB * cell - 0.5, _originY + _hoverA * cell - 0.5, cell, cell));
        }

        // 圖例：最小／中位／最大（真實數字，不是漸層裝飾）
        var legendFg = new SolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xD6)); legendFg.Freeze();
        dc.DrawText(new FormattedText(
                $"最小 {lo:0} ns ・ 中位 {med:0} ns ・ 最大 {hi:0} ns（往返；顏色以中位數為中點分段）",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, legendFg, dip),
            new Point(_originX, _originY + cell * n + 6));
    }

    /// <summary>以中位數為中點的正規化：低於中位壓在綠→琥珀段，高於中位走琥珀→紅段。</summary>
    private static double Normalize(double v, double lo, double med, double hi)
    {
        if (v <= med) return med > lo ? 0.5 * (v - lo) / (med - lo) : 0.0;
        return hi > med ? 0.5 + 0.5 * (v - med) / (hi - med) : 1.0;
    }

    private static Color LatencyColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        static Color Mix(Color a, Color b, double k) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
        return t < 0.5
            ? Mix(Color.FromRgb(0x4C, 0xAF, 0x50), Color.FromRgb(0xE0, 0xB3, 0x41), t * 2)
            : Mix(Color.FromRgb(0xE0, 0xB3, 0x41), Color.FromRgb(0xE0, 0x5B, 0x4B), (t - 0.5) * 2);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var m = Data;
        int n = m?.GetLength(0) ?? 0;
        if (n < 2 || _cell < 1) { HoverText = ""; return; }
        var p = e.GetPosition(this);
        int a = (int)((p.Y - _originY) / _cell);
        int b = (int)((p.X - _originX) / _cell);
        if (a < 0 || a >= n || b < 0 || b >= n || a == b || !double.IsFinite(m![a, b]))
        {
            _hoverA = _hoverB = -1;
            HoverText = "";
        }
        else
        {
            _hoverA = a; _hoverB = b;
            var lps = HeatmapMath.LabelsFor(Lps, n);
            HoverText = lps is null
                ? $"列 {a} ↔ 列 {b}：{m[a, b]:0.0} ns（原子交換往返；邏輯處理器編號尚未就緒）"
                : $"LP{lps[a]} ↔ LP{lps[b]}：{m[a, b]:0.0} ns（原子交換往返）";
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverA = _hoverB = -1;
        HoverText = "";
        InvalidateVisual();
    }
}

/// <summary>熱圖的純函式（單元測試涵蓋）。</summary>
public static class HeatmapMath
{
    /// <summary>
    /// 行列標籤：長度與矩陣階數相符才採用，否則回 null（呼叫端應改為不標籤）。
    /// 標錯的編號比不標更糟——那會讓使用者以為量的是別顆核心。
    /// </summary>
    public static int[]? LabelsFor(int[]? lps, int n)
        => lps is not null && n > 0 && lps.Length == n ? lps : null;
}
