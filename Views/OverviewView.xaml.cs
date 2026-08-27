using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>總覽分頁：即時儀表、走勢、硬體識別與快速規格；並提供 AI 評價的獨立入口。</summary>
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
}
