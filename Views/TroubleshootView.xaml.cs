using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>故障排查導引：列出常見場景，逐步引導檢查並導向進階頁面。</summary>
public partial class TroubleshootView : UserControl
{
    /// <summary>
    /// 下一次開啟這一頁時要直接帶到的場景（由菜鳥儀表板的症狀鈕設定）。
    /// <para>
    /// <b>為什麼用「待處理」而不是直接選取：</b>按下症狀鈕時這一頁可能還沒被實體化
    /// （延遲載入），選取的對象還不存在。所以只記下名字，等這一頁真的掛上視覺樹
    /// （<see cref="OnLoaded"/>）再找那張卡——順序反過來就會靜默地什麼都不做。
    /// </para>
    /// </summary>
    private static string? _pendingScenarioId;

    public TroubleshootView()
    {
        InitializeComponent();
        ScenarioList.ItemsSource = TroubleshootService.Scenarios;
    }

    /// <summary>
    /// 開啟故障排查頁並帶到指定症狀。<b>症狀 → 方向只由這一頁回答</b>——
    /// 儀表板那六顆鈕走這裡，不各自再決定一次要跳哪一頁，
    /// 否則同一個症狀會有兩條路，而兩條路遲早講不一樣的話。
    /// </summary>
    public static void Open(MainWindow main, string scenarioId)
    {
        _pendingScenarioId = scenarioId;
        main.NavigateToKey("troubleshoot");
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => BringPendingScenarioIntoView();

    /// <summary>把待處理的那張場景卡帶進可視範圍；找不到就照常停在頁首（不猜、不報錯）。</summary>
    private void BringPendingScenarioIntoView()
    {
        string? id = _pendingScenarioId;
        if (string.IsNullOrEmpty(id)) return;
        _pendingScenarioId = null;

        for (int i = 0; i < ScenarioList.Items.Count; i++)
        {
            if (ScenarioList.Items[i] is not TroubleshootScenario s || !string.Equals(s.Id, id, System.StringComparison.Ordinal))
                continue;
            // 容器可能還沒產生（版面尚未完成）——那就只停在頁首，不做第二次嘗試。
            if (ScenarioList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
                fe.BringIntoView();
            return;
        }
    }

    private void NavigateToPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string key) return;
        var main = Shell.Main;
        if (main is null) return;

        // 根據頁面鍵決定導向主頁面或實用工具子頁
        if (PageRegistry.Find(key) is not null)
            main.NavigateToKey(key);
        else if (PageRegistry.FindUtility(key) is not null)
            main.NavigateToUtility(key);
    }
}
