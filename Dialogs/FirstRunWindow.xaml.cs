using System.Windows;
using System.Windows.Input;

namespace XinSpect;

/// <summary>
/// 首次啟動時問一次「簡略還是詳細進階」的選擇視窗。
/// </summary>
/// <remarks>
/// 為什麼要問。這個程式的頁數對第一次打開的人是壓迫性的：四十幾頁裡有一半是直讀暫存器、
/// 編程效能計數器那一類，只想知道「我這台是什麼配備」的人根本用不到，卻要先在裡面找路。
/// 「簡易模式」這個開關一直都在設定裡，問題是沒人會在第一次打開程式時先去翻設定頁。
///
/// 這個視窗只負責<b>取得決定</b>，不寫任何設定——持久化由呼叫端（<c>MainWindow</c>）做，
/// 和 <see cref="OcRiskWindow"/> 的分工一致。
///
/// 下方那段「之後去哪裡改」不是客套話，是這個對話框最重要的一行：選錯版本的人第一個念頭
/// 就是「怎麼換回來」，如果只能自己摸索，這個對話框就從幫忙變成了阻礙。
/// </remarks>
public partial class FirstRunWindow : Window
{
    /// <summary>使用者選了「簡略」（＝開啟簡易模式）。</summary>
    public bool SimpleMode { get; private set; }

    public FirstRunWindow() => InitializeComponent();

    // 無邊框視窗：允許拖曳移動。
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            try { DragMove(); } catch { /* 非滑鼠拖曳狀態呼叫會丟例外，忽略 */ }
    }

    private void Simple_Click(object sender, RoutedEventArgs e)
    {
        SimpleMode = true;
        Close();
    }

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        SimpleMode = false;
        Close();
    }

    /// <summary>
    /// Esc 視為「詳細進階」：那是 1.9.0 之前的既有行為，直接關掉視窗的人不該因此少掉一半頁面。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SimpleMode = false;
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
