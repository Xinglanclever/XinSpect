using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 系統風扇控制分頁：列出主機板／Super I/O 上可寫入的風扇，提供手動轉速套用與一鍵還原自動。
/// 讀寫皆走 SensorService 的同一顆 LibreHardwareMonitor 執行個體（與其餘感測共用「檢測環境」）。
/// 顯示卡風扇請至「顯示卡超頻」分頁以 NVML 控制。
/// </summary>
public partial class FanControlView : UserControl
{
    private MainViewModel? _vm;

    public FanControlView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
        Loaded += (_, _) => Sync();
    }

    private SensorService? Live => (DataContext as MainViewModel)?.Live;

    // DataContext 於首次成為 Host.Content 時才注入；掛上 Live 變更通知，待感測器背景載入完成再刷新清單。
    private void Hook()
    {
        if (_vm is not null) _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += Vm_PropertyChanged;
        Sync();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Live)) Dispatcher.Invoke(Sync);
    }

    // 依感測器是否就緒／是否有可控風扇，切換清單與空狀態提示。
    private void Sync()
    {
        var live = Live;
        FanList.ItemsSource = live?.FanControls;
        bool has = live?.HasFanControls == true;
        FanList.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = live is null
            ? "感測器引擎尚未就緒，稍候將自動載入可控風扇……"
            : "未偵測到可由軟體控制的系統風扇。\n\n多數情況需在 BIOS 將風扇模式設為「PWM／可控」；或主機板的 Super I/O 晶片未被 LibreHardwareMonitor 支援。筆電與部分品牌機的風扇由 EC 韌體全權管理，通常無法由軟體介入——此為正常情形，並非程式錯誤。";
    }

    // 「套用」：把滑桿目標值寫入該風扇。未勾選「允許低於 20%」時，夾在 20% 之上以防過熱。
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FanControlRow fan })
        {
            double floor = AllowLow.IsChecked == true ? fan.MinValue : Math.Max(fan.MinValue, 20);
            double applied = Math.Max(fan.SetPoint, floor);
            fan.SetPoint = applied;         // 讓滑桿反映實際套用值（被下限夾上時同步回彈）
            fan.ApplyManual(applied);
        }
    }

    // 單一風扇交回 BIOS／自動控制。
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FanControlRow fan })
            fan.RestoreAuto();
    }

    // 全部風扇還原為 BIOS／自動控制。
    private void RestoreAll_Click(object sender, RoutedEventArgs e) => Live?.RestoreAllFansToAuto();
}
