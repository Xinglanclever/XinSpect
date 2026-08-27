using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// IHS 風格核心熱區圖：每個實體核心一格，底色由該核心溫度（藍＝冷 → 紅＝熱）決定。
/// ItemsSource 綁定 CoreRow 集合；CoreRow.TempC 變動時（每秒）會觸發底色重新計算。
/// </summary>
public partial class CoreHeatmap : UserControl
{
    public CoreHeatmap() => InitializeComponent();

    public static readonly DependencyProperty CoresProperty = DependencyProperty.Register(
        nameof(Cores), typeof(IEnumerable), typeof(CoreHeatmap), new PropertyMetadata(null));

    public IEnumerable? Cores { get => (IEnumerable?)GetValue(CoresProperty); set => SetValue(CoresProperty, value); }
}

/// <summary>核心溫度(°C) → 熱區底色。null（無讀值）給中性灰。</summary>
public sealed class HeatConverter : IValueConverter
{
    // 溫度色階：冷藍 → 青綠 → 黃綠 → 琥珀 → 熱紅
    private static readonly (double T, Color C)[] Stops =
    {
        (30, Color.FromRgb(0x1E, 0x6E, 0xDC)),
        (48, Color.FromRgb(0x14, 0xAA, 0x8C)),
        (64, Color.FromRgb(0x6C, 0xBE, 0x28)),
        (80, Color.FromRgb(0xF0, 0xAA, 0x1E)),
        (95, Color.FromRgb(0xD2, 0x3C, 0x3C)),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double? t = value as double?;
        if (t is not double temp || double.IsNaN(temp))
            return new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40));   // 無讀值：中性灰

        if (temp <= Stops[0].T) return Solid(Stops[0].C);
        if (temp >= Stops[^1].T) return Solid(Stops[^1].C);
        for (int i = 0; i < Stops.Length - 1; i++)
        {
            var (t0, c0) = Stops[i];
            var (t1, c1) = Stops[i + 1];
            if (temp >= t0 && temp <= t1)
            {
                double f = (temp - t0) / (t1 - t0);
                return Solid(Lerp(c0, c1, f));
            }
        }
        return Solid(Stops[^1].C);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Color Lerp(Color a, Color b, double f) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * f),
        (byte)(a.G + (b.G - a.G) * f),
        (byte)(a.B + (b.B - a.B) * f));

    private static SolidColorBrush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
