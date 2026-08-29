using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>總覽分頁：即時儀表、走勢、硬體識別與快速規格；並提供 AI 評價的獨立入口。</summary>
/// <remarks>
/// 各區塊已改為由 <see cref="DashboardLayout"/> 驅動的「磁貼」：顯示哪些、以什麼順序排都由使用者決定，
/// 外觀仍寫在本頁 XAML 的 <c>Tile.{識別碼}</c> 樣板裡。
/// </remarks>
public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();

    // Host.Content 延遲載入時 DataContext 由父容器繼承；點選當下已就緒。仍以主視窗為後備。
    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Application.Current?.MainWindow?.DataContext as MainViewModel;

    // 就地觸發一鍵評價：先切到 AI 助手分頁看完整對話，再開始評價。
    private async void AiEvaluate_Click(object sender, RoutedEventArgs e)
    {
        (Application.Current?.MainWindow as MainWindow)?.NavigateToAi();
        if (Vm?.Ai is { } ai) await ai.EvaluateAsync();
    }

    // 開啟獨立的 AI 助手分頁。
    private void OpenAi_Click(object sender, RoutedEventArgs e)
        => (Application.Current?.MainWindow as MainWindow)?.NavigateToAi();

    // ── 自訂磁貼 ────────────────────────────────────────────
    // 版面狀態都在 DashboardLayout 上（它負責存檔），這裡只把點擊轉過去。

    private void Customize_Click(object sender, RoutedEventArgs e)
    {
        if (Vm?.Dashboard is { } d) d.Editing = !d.Editing;
    }

    // 清單項的 DataContext 就是那塊磁貼，直接取即可（不必反查索引）
    private static DashboardTile? TileOf(object sender) => (sender as FrameworkElement)?.DataContext as DashboardTile;

    private void TileUp_Click(object sender, RoutedEventArgs e)
    {
        if (TileOf(sender) is { } t) Vm?.Dashboard.MoveUp(t);
    }

    private void TileDown_Click(object sender, RoutedEventArgs e)
    {
        if (TileOf(sender) is { } t) Vm?.Dashboard.MoveDown(t);
    }

    private void TileReset_Click(object sender, RoutedEventArgs e) => Vm?.Dashboard.Reset();
}
