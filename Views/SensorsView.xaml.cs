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

    private MainViewModel? Vm => DataContext as MainViewModel;

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
        try { Vm?.ExternalSensors.Refresh(); } catch { /* 外部來源為附加功能 */ }
    }
}
