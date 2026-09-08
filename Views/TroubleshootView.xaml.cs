using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>故障排查導引：列出常見場景，逐步引導檢查並導向進階頁面。</summary>
public partial class TroubleshootView : UserControl
{
    public TroubleshootView()
    {
        InitializeComponent();
        ScenarioList.ItemsSource = TroubleshootService.Scenarios;
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
