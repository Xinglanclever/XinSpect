using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 系統風扇控制分頁：列出主機板／Super I/O 上可寫入的風扇，提供手動轉速套用、
/// 可拖動的溫度→轉速曲線，以及一鍵還原自動。
/// 讀寫皆走 SensorService 的同一顆 LibreHardwareMonitor 執行個體（與其餘感測共用「檢測環境」）；
/// 曲線的每秒控制迴圈由 MetricsPump 驅動，故切換分頁後仍持續生效。
/// 顯示卡風扇請至「顯示卡超頻」分頁以 NVML 控制。
/// </summary>
public partial class FanControlView : UserControl
{
    private MainViewModel? _vm;
    private FanCurveService? _curves;

    public FanControlView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
        Loaded += (_, _) => Sync();
    }

    private SensorService? Live => (DataContext as MainViewModel)?.Live;

    // DataContext 於首次成為 Host.Content 時才注入；掛上 Live 與曲線服務的變更通知，
    // 待感測器背景載入完成再刷新清單與狀態列。
    private void Hook()
    {
        if (_vm is not null) _vm.PropertyChanged -= Vm_PropertyChanged;
        if (_curves is not null) _curves.PropertyChanged -= Curves_PropertyChanged;

        _vm = DataContext as MainViewModel;
        _curves = _vm?.FanCurves;

        if (_vm is not null) _vm.PropertyChanged += Vm_PropertyChanged;
        if (_curves is not null) _curves.PropertyChanged += Curves_PropertyChanged;
        Sync();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Live)) Dispatcher.Invoke(Sync);
    }

    private void Curves_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FanCurveService.StatusText)
            or nameof(FanCurveService.HasCurves) or nameof(FanCurveService.AllowStop))
            Dispatcher.Invoke(Sync);
    }

    // 依感測器是否就緒／是否有可控風扇，切換清單與空狀態提示；清單以曲線為單位（每條曲線持有其風扇）。
    private void Sync()
    {
        var live = Live;
        FanList.ItemsSource = _curves?.Curves;
        bool has = live?.HasFanControls == true && _curves?.HasCurves == true;

        FanList.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        CurveBar.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;

        if (_curves is not null)
        {
            CurveStatus.Text = _curves.StatusText;
            if (AllowStop.IsChecked != _curves.AllowStop) AllowStop.IsChecked = _curves.AllowStop;
        }

        EmptyText.Text = live is null
            ? "感測器引擎尚未就緒，稍候將自動載入可控風扇……"
            : "未偵測到可由軟體控制的系統風扇。\n\n多數情況需在 BIOS 將風扇模式設為「PWM／可控」；或主機板的 Super I/O 晶片未被 LibreHardwareMonitor 支援。筆電與部分品牌機的風扇由 EC 韌體全權管理，通常無法由軟體介入——此為正常情形，並非程式錯誤。";
    }

    // 「套用」：把滑桿目標值寫入該風扇。未勾選「允許低於 20%」時，夾在 20% 之上以防過熱。
    // 手動接管即代表放棄曲線，故先停用該風扇的曲線（不交還自動，否則手動值會被立刻蓋掉）。
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FanControlRow fan })
        {
            _curves?.ReleaseFor(fan);
            double floor = AllowLow.IsChecked == true ? fan.MinValue : Math.Max(fan.MinValue, 20);
            double applied = Math.Max(fan.SetPoint, floor);
            fan.SetPoint = applied;         // 讓滑桿反映實際套用值（被下限夾上時同步回彈）
            fan.ApplyManual(applied);
        }
    }

    // 單一風扇交回 BIOS／自動控制（同時停用其曲線，否則下一拍又被接管）。
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FanControlRow fan })
        {
            _curves?.ReleaseFor(fan);
            fan.RestoreAuto();
        }
    }

    // 全部風扇還原為 BIOS／自動控制（含停用所有曲線）。
    private void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        try { _curves?.DisableAll(); } catch { /* 逐顆還原失敗不影響下一步 */ }
        Live?.RestoreAllFansToAuto();
    }

    // 全部曲線套用同一樣板（Tag 為 0 靜音 / 1 均衡 / 2 效能）。
    private void PresetAll_Click(object sender, RoutedEventArgs e)
    {
        if (_curves is not null && sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int preset))
            _curves.ApplyPresetToAll(preset);
    }

    // 單一風扇套用樣板（按鈕的 DataContext 即該條曲線）。
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag, DataContext: FanCurve curve } && int.TryParse(tag, out int preset))
            curve.LoadPreset(preset);
    }

    // 全部停用曲線並交還主機板自動控制。
    private void DisableCurves_Click(object sender, RoutedEventArgs e) => _curves?.DisableAll();

    // 是否允許曲線把輸出壓到 20% 以下（含停轉）。
    private void AllowStop_Changed(object sender, RoutedEventArgs e)
    {
        if (_curves is not null) _curves.AllowStop = AllowStop.IsChecked == true;
    }
}
