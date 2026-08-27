using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 顯示卡超頻分頁。DataContext 繼承自 MainWindow（= MainViewModel），繫結走 <c>GpuOc.*</c>；
/// 此檔僅把按鈕點擊轉呼叫服務方法，真正的硬體寫入全發生在 <see cref="GpuOcService"/>
/// （NVML／NVAPI）。進入本分頁不另設風險確認（其寫入皆帶驅動端夾限與版本保護）。
/// </summary>
public partial class GpuOcView : UserControl
{
    public GpuOcView() => InitializeComponent();

    private GpuOcService? Gpu => (DataContext as MainViewModel)?.GpuOc;

    private void Power_Click(object sender, System.Windows.RoutedEventArgs e) => Gpu?.ApplyPowerLimit();
    private void Temp_Click(object sender, System.Windows.RoutedEventArgs e) => Gpu?.ApplyTempLimit();
    private void Core_Click(object sender, System.Windows.RoutedEventArgs e) => Gpu?.ApplyCoreOffset();
    private void Mem_Click(object sender, System.Windows.RoutedEventArgs e) => Gpu?.ApplyMemOffset();
    private void Fan_Click(object sender, System.Windows.RoutedEventArgs e) => Gpu?.ApplyFan();
    private void ApplyAll_Click(object sender, System.Windows.RoutedEventArgs e) => Gpu?.ApplyAll();
    private void Restore_Click(object sender, System.Windows.RoutedEventArgs e) => Gpu?.RestoreDefaults();
}
