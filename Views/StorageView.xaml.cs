using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

public partial class StorageView : UserControl
{
    public StorageView() => InitializeComponent();

    // Host.Content 延遲載入時 DataContext 由父容器繼承；仍以主視窗為後備。
    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;

    private void SmartRead_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        int i = SmartDrives.SelectedIndex;
        if (i < 0 || i >= Vm.DiskSmart.Drives.Count) return;
        Vm.DiskSmart.Read(Vm.DiskSmart.Drives[i].Index);
    }

    // ===== SLC 快取耗盡曲線 =====
    private void SlcStart_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        SyncSlcVolume();
        Vm.SlcCache.Start();
    }

    private void SlcStop_Click(object sender, RoutedEventArgs e) => Vm?.SlcCache.Cancel();

    private void SlcVolume_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSlcVolume();

    private void SyncSlcVolume()
    {
        if (Vm is null || SlcVolume.SelectedItem is not VolumeInfo v) return;
        Vm.SlcCache.DriveLetter = v.Name;   // 例：「C:」
    }
}
