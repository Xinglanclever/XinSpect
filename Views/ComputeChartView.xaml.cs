using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace XinSpect;

/// <summary>算力圖頁面：水平長條顯示各維度的相對算力。</summary>
public partial class ComputeChartView : UserControl
{
    private readonly ComputeChartService _svc = new();
    private bool _loaded;

    public ComputeChartView()
    {
        InitializeComponent();
        // 訂一次就好。先前寫在 DoRefresh 裡，每按一次「重新整理」就多掛一個處理常式，
        // 而且舊的 lambda 永遠不會被釋放。
        ChartItems.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (ChartItems.ItemContainerGenerator.Status
                != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated) return;
            Dispatcher.BeginInvoke(new Action(ApplyBarColors),
                System.Windows.Threading.DispatcherPriority.Loaded);
        };
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; DoRefresh(); } };
    }

    private void DoRefresh()
    {
        _svc.Refresh(Shell.Vm);
        StatusText.Text = _svc.StatusLine;

        if (_svc.Metrics.Count == 0)
        {
            EmptyCard.Visibility = Visibility.Visible;
            ChartCard.Visibility = Visibility.Collapsed;
            LegendCard.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyCard.Visibility = Visibility.Collapsed;
        ChartCard.Visibility = Visibility.Visible;
        LegendCard.Visibility = Visibility.Visible;
        ChartItems.ItemsSource = _svc.Metrics;

        Dispatcher.BeginInvoke(new Action(ApplyBarColors),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ApplyBarColors()
    {
        for (int i = 0; i < ChartItems.Items.Count; i++)
        {
            if (ChartItems.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
            cp.ApplyTemplate();
            if (FindChildByName(cp, "Bar") is Border bar && ChartItems.Items[i] is ComputeMetric metric)
                bar.Background = CategoryBrush(metric.Category);
        }
    }

    private static DependencyObject? FindChildByName(DependencyObject parent, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            var found = FindChildByName(child, name);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>依類別回傳不同色——走主題資源，換主題時顏色跟著換。</summary>
    private static SolidColorBrush CategoryBrush(string category) => category switch
    {
        "CPU"    => VizPalette.Of("AccentBrush", "#3987e5"),     // 主色（藍）
        "記憶體"  => VizPalette.Of("GoodBrush", "#0ca30c"),       // 綠
        "顯示卡"  => VizPalette.Of("CriticalBrush", "#d03b3b"),   // 紅
        "儲存"    => VizPalette.Of("WarningBrush", "#fab219"),    // 琥珀
        "NPU"    => VizPalette.Of("SeriousBrush", "#ec835a"),    // 橘
        _        => VizPalette.Muted,
    };

    private void Refresh_Click(object sender, RoutedEventArgs e) => DoRefresh();
}

/// <summary>
/// 將 BarFraction (0–1) 乘以父容器 ActualWidth，得出長條像素寬度。
/// XAML 中以 MultiBinding 傳入 (BarFraction, Grid.ActualWidth)。
/// </summary>
public sealed class BarWidthConverter : IMultiValueConverter
{
    public static readonly BarWidthConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2
            && values[0] is double fraction
            && values[1] is double totalWidth
            && totalWidth > 0)
        {
            return Math.Max(0, fraction * totalWidth);
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
