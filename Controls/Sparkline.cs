using System;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 迷你走勢圖（sparkline）：把一小段數值序列畫成一條折線，塞在表格的一欄裡。
///
/// <para>
/// <b>沒有資料就什麼都不畫。</b><see cref="Values"/> 為 null 或少於兩點時整個控制項是空白——
/// 空白本身就是陳述（「這把鍵沒有可畫的歷史」），不拿一條假直線冒充量測。
/// </para>
///
/// <para>
/// 刻意不做座標軸、刻度與數字：那是 <see cref="HistoryGraph"/> 的職責。
/// 這裡只保留形狀——極值之間的正規化讓「漲跌」看得見，而具體數值就在同一列的值欄裡。
/// 全段等值時畫一條中央水平線（形狀如實是「平的」，不是沒有資料）。
/// </para>
///
/// <para>
/// DP 型別用 <see cref="SparkSeries"/> 類別包裝而非 <c>double[]</c>：
/// 樣板區段（DataTemplate）裡的陣列型屬性標記編譯不吃（MC4102），
/// 照倉庫慣例（<see cref="MetricHistory"/> 之於 <see cref="HistoryGraph"/>）包一層。
/// </para>
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    /// <summary>要走勢的數值序列（時間序）。</summary>
    public sealed record SparkSeries(double[] Points);

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(SparkSeries), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>要走勢的數值序列；null 或少於兩點＝留空。</summary>
    public SparkSeries? Values
    {
        get => (SparkSeries?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Sparkline()
    {
        this.RepaintOnThemeChange();
        ClipToBounds = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var v = Values?.Points;
        if (v is null || v.Length < 2) return;

        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 2) return;

        double min = double.MaxValue, max = double.MinValue;
        foreach (double x in v)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) return;   // 髒值整段不畫——半條線比沒有線更會騙人
            if (x < min) min = x;
            if (x > max) max = x;
        }

        double pad = 1.5;   // 上下留一點，線不貼著裁切邊
        double Y(double x) => min == max
            ? h / 2
            : pad + (1 - (x - min) / (max - min)) * (h - pad * 2);

        var pen = new Pen(VizPalette.Accent, 1.5);
        pen.Freeze();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < v.Length; i++)
            {
                var p = new Point(w * i / (v.Length - 1), Y(v[i]));
                if (i == 0) g.BeginFigure(p, false, false);
                else g.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
