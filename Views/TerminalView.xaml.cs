using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace XinSpect;

/// <summary>終端分頁：驅動 <see cref="TerminalService"/>——Enter 執行、上/下鍵回溯歷史、輸出自動捲到底。</summary>
public partial class TerminalView : UserControl
{
    private int _histIndex = -1;   // -1＝目前輸入（未在瀏覽歷史）
    private bool _wired;           // 只掛一次 Output 變更捲動處理（Loaded 可能多次觸發）

    public TerminalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private TerminalService? Term => DataContext as TerminalService;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // UserControl 根層級的 DataContext="{Binding Terminal}" 在此宿主結構（延後掛入 Host.Content）
        // 下無法解析為 TerminalService，改由程式碼於載入時自主視窗的 MainViewModel 取得並指派。
        if (DataContext is not TerminalService && Shell.Vm is { } vm)
            DataContext = vm.Terminal;

        if (Term is { } t)
        {
            if (!_wired)
            {
                _wired = true;
                t.PropertyChanged += (_, ev) =>
                {
                    if (ev.PropertyName == nameof(TerminalService.Output)) OutputBox.ScrollToEnd();
                };
            }
            if (!t.IsRunning) t.Start();
        }
        InputBox.Focus();
    }

    private void Run_Click(object sender, RoutedEventArgs e) => Submit();

    private void Restart_Click(object sender, RoutedEventArgs e) => Term?.Start();

    private void Interrupt_Click(object sender, RoutedEventArgs e) => Term?.Interrupt();

    private void Clear_Click(object sender, RoutedEventArgs e) => Term?.Clear();

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Submit();
                e.Handled = true;
                break;
            case Key.Up:
                NavigateHistory(-1);
                e.Handled = true;
                break;
            case Key.Down:
                NavigateHistory(+1);
                e.Handled = true;
                break;
        }
    }

    private void Submit()
    {
        if (Term is not { } t) return;
        t.Send(InputBox.Text);
        InputBox.Clear();
        _histIndex = -1;
    }

    // 上/下鍵在指令歷史間移動；下超過最後一筆即回到空白輸入行
    private void NavigateHistory(int dir)
    {
        if (Term is not { } t || t.History.Count == 0) return;
        var h = t.History;
        if (_histIndex == -1) _histIndex = dir < 0 ? h.Count - 1 : -1;
        else _histIndex = Math.Clamp(_histIndex + dir, 0, h.Count - 1);

        if (_histIndex < 0 || _histIndex >= h.Count) { InputBox.Clear(); _histIndex = -1; return; }
        InputBox.Text = h[_histIndex];
        InputBox.CaretIndex = InputBox.Text.Length;
    }
}
