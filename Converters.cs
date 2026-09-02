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
/// <remarks>
/// 四階狀態色是主題資源（<c>GoodBrush</c> 等），淺色主題會換成加深的一組以保持對比度，
/// 所以這裡回傳的是<b>活的</b>共用筆刷而非凍結副本——換主題時已經畫在畫面上的徽章會自己跟著變。
/// 因此呼叫端不可改它的 <c>Color</c>／<c>Opacity</c>。
/// </remarks>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public static SolidColorBrush Good => VizPalette.Of("GoodBrush", "#0ca30c");
    public static SolidColorBrush Warning => VizPalette.Of("WarningBrush", "#fab219");
    public static SolidColorBrush Serious => VizPalette.Of("SeriousBrush", "#ec835a");
    public static SolidColorBrush Critical => VizPalette.Of("CriticalBrush", "#d03b3b");

    /// <summary>非 Severity 或 <see cref="Severity.Neutral"/>：走次要文字色，不強調。</summary>
    public static SolidColorBrush Neutral => VizPalette.Of("SecondaryInkBrush", "#c3c2b7");

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
}

/// <summary>終端機執行狀態 → 指示燈顏色（執行中綠、停止灰）。與狀態徽章同一套主題色。</summary>
public sealed class BoolToRunBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? SeverityToBrushConverter.Good : VizPalette.Muted;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>終端機執行狀態 → 文字（執行中／已停止）。</summary>
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

/// <summary>是否置頂 → 圖釘字符（置頂時實心，取消時空心）。</summary>
public sealed class BoolToPinGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "📌" : "○";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>是否精簡 → 展開／收起箭頭（精簡時朝下＝可展開，展開時朝上＝可收起）。</summary>
public sealed class BoolToCompactGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "▾" : "▴";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>USB 拓樸深度 → 左邊的縮排（每經過一層外接集線器往右一格）。</summary>
/// <remarks>USB 規格最多 7 層，深度封在 6 格：資料異常（或負數）時寧可不縮排，也不把裝置名稱推出畫面。</remarks>
public sealed class DepthToIndentConverter : IValueConverter
{
    private const double Step = 14;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new Thickness(value is int d ? Math.Clamp(d, 0, 6) * Step : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>總覽磁貼 → 對應的版面樣板（以資源鍵 <c>Tile.{識別碼}</c> 查找）。</summary>
/// <remarks>
/// 磁貼順序是資料、外觀是宣告：把七塊卡片各包成一個 <c>DataTemplate</c> 放在
/// <c>OverviewView.xaml</c> 的資源裡，這裡只做「識別碼 → 樣板」的查表，
/// 新增磁貼時不必動到任何 C#。查不到樣板就回傳 <c>null</c>（該格空白），不丟例外。
/// </remarks>
public sealed class DashboardTileTemplateSelector : System.Windows.Controls.DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
        => item is DashboardTile t && container is FrameworkElement fe
            ? fe.TryFindResource("Tile." + t.Id) as DataTemplate
            : null;
}

/// <summary>通電小時 → 「N 小時（全天候運轉約 M 年）」。換算規則只寫在 MachineAgeDecoder 裡一份。</summary>
public sealed class HoursToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => MachineAgeDecoder.HoursText(value is long h ? h : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
