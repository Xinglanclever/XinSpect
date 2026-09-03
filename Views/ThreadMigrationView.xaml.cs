using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 執行緒遷移與排程落點頁。量測需要使用者親自按下——ETW 的核心追蹤本身有成本，
/// 不該一進頁就自己開一個追蹤會期。
/// </summary>
public partial class ThreadMigrationView : UserControl, IPageLifecycle
{
    public ThreadMigrationView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    private void Start_Click(object sender, RoutedEventArgs e) => Vm?.Migration.Start();
    private void Stop_Click(object sender, RoutedEventArgs e) => Vm?.Migration.Stop();

    public void OnActivated() { }

    /// <summary>離開本頁就把追蹤會期收掉：ETW 的核心會期是全機資源，不該在背景一直開著。</summary>
    public void OnDeactivated() => Vm?.Migration.Stop();
}
