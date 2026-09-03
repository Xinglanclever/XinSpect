using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>總覽分頁：即時儀表、走勢、硬體識別與快速規格。</summary>
/// <remarks>
/// 各區塊已改為由 <see cref="DashboardLayout"/> 驅動的「磁貼」：顯示哪些、以什麼順序排都由使用者決定，
/// 外觀仍寫在本頁 XAML 的 <c>Tile.{識別碼}</c> 樣板裡。
///
/// 1.9.0 起這裡不再有「AI 評價」磁貼：側邊欄第二項就是完整的 AI 評價頁，在總覽再放一塊只做同一件事的
/// 磁貼是重複入口，佔掉的又是使用者第一眼會看到的位置。
/// </remarks>
public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();

    // Host.Content 延遲載入時 DataContext 由父容器繼承；點選當下已就緒。仍以主視窗為後備。
    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;

    // ── 自訂磁貼 ────────────────────────────────────────────
    // 版面狀態都在 DashboardLayout 上（它負責存檔），這裡只把點擊轉過去。

    private void Customize_Click(object sender, RoutedEventArgs e)
    {
        if (Vm?.Dashboard is { } d) d.Editing = !d.Editing;
    }

    /// <summary>
    /// 把八行規格摘要放進剪貼簿。給「要問人」的情境用：整機報告匯出是留存用的完整檔案，
    /// 這個是貼得進聊天室的長度，而且不含電腦名稱、使用者名稱與任何序號。
    /// </summary>
    private void CopySpec_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        try
        {
            string text = SpecSummary.Build(SpecFactsCollector.Collect(Vm));
            Clipboard.SetText(text);
            // 按鈕自己回報結果：跳一個對話框只是多一次點擊
            CopySpecButton.Content = "已複製 ✓";
        }
        catch (Exception ex)
        {
            // 剪貼簿被別的程式鎖住是真的會發生的事，別讓它變成未處理例外
            Diag.Swallow("OverviewView.CopySpec", ex, "規格摘要沒有複製成功，按鈕會顯示失敗。");
            CopySpecButton.Content = "複製失敗";
        }

        // 兩秒後恢復原本的字，不留一個永遠寫著「已複製」的按鈕
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CopySpecButton.Content = "複製規格摘要";
        };
        timer.Start();
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
