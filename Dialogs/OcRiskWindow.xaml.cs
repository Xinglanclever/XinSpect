using System.Windows;
using System.Windows.Input;

namespace XinSpect;

/// <summary>
/// 超頻功能進入前的兩階段風險確認對話框。
/// 第一階段：風險告知，三個選項
///   A「嚇哭了」        → 放棄，不進入（Proceed = false）
///   B「我要超 10Ghz…」 → 進入第二階段免責聲明
///   C「我知道我在做什麼（今後不再顯示）」→ 進入第二階段，並在確認後設定「不再顯示」旗標
/// 第二階段：免責聲明（測試版、未開發完全、開發者不負責檔案丟失／硬體損毀／軟體奔潰）
///   確認 → Proceed = true；返回 → 回到第一階段。
///
/// 呼叫端以 ShowDialog() 開啟，結束後讀取 <see cref="Proceed"/> 與 <see cref="DontShowAgain"/>。
/// 此對話框只負責「取得使用者決定」，不寫入任何設定；持久化由呼叫端依 DontShowAgain 決定。
/// </summary>
public partial class OcRiskWindow : Window
{
    /// <summary>使用者是否確認進入超頻功能。</summary>
    public bool Proceed { get; private set; }

    /// <summary>使用者是否選擇「今後不再顯示」（僅在選 C 且完成第二階段確認時為真）。</summary>
    public bool DontShowAgain { get; private set; }

    // 是否走 C 路徑（今後不再顯示）；於第二階段確認時才落實到 DontShowAgain。
    private bool _choseDontShow;

    public OcRiskWindow() => InitializeComponent();

    // 無邊框視窗：允許拖曳標題區移動。
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            try { DragMove(); } catch { /* 非滑鼠拖曳狀態呼叫會丟例外，忽略 */ }
    }

    // A：嚇哭了 → 放棄離開。
    private void OptionA_Click(object sender, RoutedEventArgs e)
    {
        Proceed = false;
        DontShowAgain = false;
        Close();
    }

    // B：明白風險，繼續 → 第二階段。
    private void OptionB_Click(object sender, RoutedEventArgs e)
    {
        _choseDontShow = false;
        GoToStage2();
    }

    // C：我知道我在做什麼（今後不再顯示）→ 第二階段（確認後才落實不再顯示）。
    private void OptionC_Click(object sender, RoutedEventArgs e)
    {
        _choseDontShow = true;
        GoToStage2();
    }

    private void GoToStage2()
    {
        Stage1.Visibility = Visibility.Collapsed;
        Stage2.Visibility = Visibility.Visible;
    }

    // 第二階段「返回」→ 回到第一階段重新選擇。
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        Stage2.Visibility = Visibility.Collapsed;
        Stage1.Visibility = Visibility.Visible;
    }

    // 第二階段「我已了解風險，繼續」→ 正式進入。
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Proceed = true;
        DontShowAgain = _choseDontShow;
        Close();
    }

    // Esc 等同 A：放棄離開。
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Proceed = false;
            DontShowAgain = false;
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
