using System.Windows;
using System.Windows.Controls;
namespace XinSpect;

public partial class SensorsView : UserControl
{
    public SensorsView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshExternalSensors();
    }

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;
    private Window? _sensorAlertsWindow, _sensorPanelWindow;

    private void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.SensorLog.IsLogging) vm.SensorLog.StopLogging();
        else if (vm.Live is { } live) vm.SensorLog.StartLogging(vm.Settings, live);
    }

    private void SelectAllLog_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { Live: { } live } vm) vm.SetSensorLogSelection(live.AllSensors.Select(r => r.Id));
    }

    private void ClearLogSelection_Click(object sender, RoutedEventArgs e) => Vm?.SetSensorLogSelection([]);
    private void ResetStatistics_Click(object sender, RoutedEventArgs e) => Vm?.Live?.ResetSensorStatistics();

    private void OpenAlerts_Click(object sender, RoutedEventArgs e)
    {
        if (_sensorAlertsWindow is { IsVisible: true }) { _sensorAlertsWindow.Activate(); return; }
        if (Vm is not { Live: { } live } vm) return;
        _sensorAlertsWindow = new SensorAlertsWindow(live, vm.Alerts) { Owner = Window.GetWindow(this) };
        _sensorAlertsWindow.Closed += (_, _) => _sensorAlertsWindow = null;
        _sensorAlertsWindow.Show();
    }

    private void OpenPanel_Click(object sender, RoutedEventArgs e)
    {
        if (_sensorPanelWindow is { IsVisible: true }) { _sensorPanelWindow.Activate(); return; }
        if (Vm?.Live is not { } live) return;
        _sensorPanelWindow = new SensorPanelWindow(live) { Owner = Window.GetWindow(this) };
        _sensorPanelWindow.Closed += (_, _) => _sensorPanelWindow = null;
        _sensorPanelWindow.Show();
    }

    private void SourceCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is null) return;
        Vm.ExternalSensors.Mode = SourceCombo.SelectedIndex switch
        {
            1 => SourceMode.HwInfo,
            2 => SourceMode.Aida64,
            3 => SourceMode.CoreTemp,
            _ => SourceMode.All,
        };
        RefreshExternalSensors();
    }

    private void RefreshExternal_Click(object sender, RoutedEventArgs e) => RefreshExternalSensors();

    private void RefreshExternalSensors()
    {
        try { Vm?.ExternalSensors.Refresh(Vm.Live?.CaptureCoreTemperatureSamples()); } catch { /* 外部來源為附加功能 */ }
    }
}
