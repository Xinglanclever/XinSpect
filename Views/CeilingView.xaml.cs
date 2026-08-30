using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 效能天花板頁。進頁時只做靜態讀取（讀 MSR，不製造負載）；要撞牆得使用者自己按。
/// </summary>
/// <remarks>
/// 進頁自動量測是不能做的：這一頁的完整量測會把全部核心壓滿數十秒。
/// 使用者只是點到這一頁，不代表他同意讓機器開始發熱。
/// </remarks>
public partial class CeilingView : UserControl, IPageLifecycle
{
    public CeilingView() => InitializeComponent();

    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;

    public void OnActivated()
    {
        if (Vm is { } vm && vm.Ceiling.LimitRows.Count == 0) vm.Ceiling.LoadStatic();
    }

    public void OnDeactivated() => Vm?.Ceiling.Stop();

    private void Dur_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && int.TryParse(rb.Tag as string, out int sec) && Vm is not null)
            Vm.Ceiling.DurationSec = sec;
    }

    private void Static_Click(object sender, RoutedEventArgs e) => Vm?.Ceiling.LoadStatic();
    private void Start_Click(object sender, RoutedEventArgs e) => Vm?.Ceiling.Start();
    private void Stop_Click(object sender, RoutedEventArgs e) => Vm?.Ceiling.Stop();
}
