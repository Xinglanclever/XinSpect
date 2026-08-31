using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 瓶頸診斷頁：把各頁已量到的讀值合起來分析一次。
/// </summary>
/// <remarks>
/// <para>
/// 進頁時會補齊幾項<b>唯讀且便宜</b>的量測（黏滯節流位元、記憶體認可帳面、MCA 銀行、電源政策、
/// 逐核歸因），因為少了它們這一頁能講的話會少一半。這些都只是讀取，不製造負載、不寫入任何東西。
/// 反過來說，會讓機器發熱的量測（效能天花板的撞牆、Top-down 的取樣）<b>一律不自動發動</b>——
/// 使用者點進一頁不等於同意讓機器開始燒。缺席的量測會出現在「還沒納入判斷的部分」，附上該去哪一頁按。
/// </para>
/// <para>
/// 停留期間每兩秒重算：分析本身是純函式，讀的是感測引擎已經在跑的那份值，不額外取樣硬體。
/// </para>
/// </remarks>
public partial class BottleneckView : UserControl, IPageLifecycle
{
    private DispatcherTimer? _timer;

    public BottleneckView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    public void OnActivated()
    {
        if (Vm is not { } vm) return;

        // 補齊唯讀量測；已經有資料的就不重讀（本方法會被外殼重播，必須可重複呼叫）
        if (vm.ThermalSticky.Rows.Count == 0 && vm.ThermalSticky.CanRefresh) vm.ThermalSticky.Refresh();
        if (vm.MemoryTruth.Reading is null) vm.MemoryTruth.Refresh();
        if (vm.Mca.Rows.Count == 0 && vm.Mca.CanRefresh) vm.Mca.Refresh();
        if (vm.PowerPolicy.Settings.Count == 0 && vm.PowerPolicy.CanRefresh) vm.PowerPolicy.Refresh();
        if (vm.CoreTime.Rows.Count == 0 && vm.CoreTime.CanRefresh) vm.CoreTime.Refresh();

        vm.RefreshBottleneck();

        _timer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void OnDeactivated() => _timer?.Stop();

    private void OnTick(object? sender, EventArgs e) => Vm?.RefreshBottleneck();

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        // 手動按下時連唯讀量測一起重讀：黏滯位元與 MCA 是黏滯的，重讀才看得到新發生的事
        if (vm.ThermalSticky.CanRefresh) vm.ThermalSticky.Refresh();
        vm.MemoryTruth.Refresh();
        if (vm.Mca.CanRefresh) vm.Mca.Refresh();
        if (vm.PowerPolicy.CanRefresh) vm.PowerPolicy.Refresh();
        if (vm.CoreTime.CanRefresh) vm.CoreTime.Refresh();
        vm.RefreshBottleneck();
    }
}
