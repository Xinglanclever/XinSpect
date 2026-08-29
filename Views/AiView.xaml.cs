using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace XinSpect;

/// <summary>獨立的 AI 助手分頁：以使用者自選的 AI 模型評價本機硬體並進行問答對話。</summary>
public partial class AiView : UserControl
{
    public AiView()
    {
        InitializeComponent();
        // 有新訊息時自動捲到底
        Loaded += (_, _) =>
        {
            if (Vm?.Ai is { } ai)
                ai.Messages.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd());
        };
    }

    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Application.Current?.MainWindow?.DataContext as MainViewModel;

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async void Evaluate_Click(object sender, RoutedEventArgs e)
    {
        if (Vm?.Ai is { } ai) await ai.EvaluateAsync();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Vm?.Ai.Clear();

    // 快問按鈕：Tag 帶的是實際送出的完整問題（按鈕上只顯示短標籤）
    private async void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string prompt } || prompt.Length == 0) return;
        var ai = Vm?.Ai;
        if (ai is null || !ai.CanSend) return;
        await ai.SendAsync(prompt);
    }

    // Enter 送出、Shift+Enter 換行。
    private async void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async Task SendAsync()
    {
        var ai = Vm?.Ai;
        if (ai is null || !ai.CanSend) return;
        var text = Input.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        Input.Clear();
        await ai.SendAsync(text);
    }
}
