using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 菜鳥儀表板：簡易模式首頁，把「這台電腦是什麼、健不健康、能做什麼」濃縮成一頁。
/// 所有資料繫結到 MainViewModel 的既有屬性，不建立新的服務或硬體存取。
/// </summary>
public partial class BeginnerDashboardView : UserControl
{
    public BeginnerDashboardView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    private void Cleanup_Click(object sender, RoutedEventArgs e)
        => Shell.Main?.NavigateToUtility("cleanup");

    private void MemClean_Click(object sender, RoutedEventArgs e)
        => Shell.Main?.NavigateToUtility("memclean");

    private void Export_Click(object sender, RoutedEventArgs e)
        => Vm?.ExportReport();

    /// <summary>
    /// 一鍵評價本機：先跳到 AI 分頁（結果在那裡），再送出評價請求。
    /// 順序刻意是「先跳再送」——請求要好幾秒，使用者要能看到它正在跑，而不是畫面沒反應。
    /// </summary>
    private async void Evaluate_Click(object sender, RoutedEventArgs e)
    {
        Shell.Main?.NavigateToAi();
        if (Vm?.Ai is { } ai) await ai.EvaluateAsync();
    }
}
