using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace XinSpect;

/// <summary>true → Collapsed，false → Visible（用於空狀態訊息）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>數量 &gt; 0 → Visible，否則 Collapsed（用於清單有內容時才顯示）。</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Severity → 前景色（對應 dataviz status 配色）。數值標籤永遠可見，故顏色非唯一線索。</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public static readonly SolidColorBrush Good = Freeze("#0ca30c");
    public static readonly SolidColorBrush Warning = Freeze("#fab219");
    public static readonly SolidColorBrush Serious = Freeze("#ec835a");
    public static readonly SolidColorBrush Critical = Freeze("#d03b3b");
    public static readonly SolidColorBrush Neutral = Freeze("#c3c2b7");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Severity s ? Brush(s) : Neutral;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    public static SolidColorBrush Brush(Severity s) => s switch
    {
        Severity.Good => Good,
        Severity.Warning => Warning,
        Severity.Serious => Serious,
        Severity.Critical => Critical,
        _ => Neutral,
    };

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>終端執行狀態 → 指示燈顏色（執行中綠、停止灰）。</summary>
public sealed class BoolToRunBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Run = Frozen("#0ca30c");
    private static readonly SolidColorBrush Stop = Frozen("#7a7a72");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Run : Stop;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>終端執行狀態 → 文字（執行中／已停止）。</summary>
public sealed class BoolToRunTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "執行中" : "已停止";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>是否 PowerShell → 輸入提示字元（PS&gt; 或 &gt;）。</summary>
public sealed class BoolToPromptConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "PS>" : ">";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
