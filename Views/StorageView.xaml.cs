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

    /// <summary>
    /// 兩個下拉選單的 SelectedIndex 是 XAML 字面值，但 ItemsSource 是繫結、且 DataContext 由外殼
    /// 延遲繼承——控制項初始化時清單還是空的，字面值 0 會被強制成 -1，之後不會自己回到 0。
    /// 結果是「讀取」按了沒反應、SLC 測試也拿不到選定磁碟區（悄悄用預設的 C:）。
    /// 載入完成時清單已備妥，這裡補上第一項並同步一次 SLC 目標。
    /// </summary>
    private void StorageView_Loaded(object sender, RoutedEventArgs e)
    {
        if (SmartDrives.SelectedIndex < 0 && SmartDrives.Items.Count > 0) SmartDrives.SelectedIndex = 0;
        if (SlcVolume.SelectedIndex < 0 && SlcVolume.Items.Count > 0) SlcVolume.SelectedIndex = 0;
        SyncSlcVolume();
    }

    private void SmartRead_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        // 索引由服務決定並回報，選單跟著顯示實際讀的那顆（清單晚到時才會用到這個後備）
        int used = Vm.DiskSmart.ReadSelected(SmartDrives.SelectedIndex);
        if (used >= 0 && used < SmartDrives.Items.Count) SmartDrives.SelectedIndex = used;
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
