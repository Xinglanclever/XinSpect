using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

public partial class BlueSquadronView : UserControl, IPageLifecycle
{
    public BlueSquadronView() => InitializeComponent();

    public void OnActivated()
    {
        Vm?.BlueSquadron.Posture.Refresh(Vm);
    }

    public void OnDeactivated() { }

    private void Refresh_Click(object sender, RoutedEventArgs e)
        => Vm?.BlueSquadron.Posture.Refresh(Vm);

    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;
}
