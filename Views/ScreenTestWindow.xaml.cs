using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 螢幕檢測：全螢幕純色循環，用於檢查亮點／暗點（壞點）、漏光與背光均勻度。
/// 純 WPF、零相依、零驅動；點擊或方向鍵切換顏色，Esc 離開。
/// </summary>
public partial class ScreenTestWindow : Window
{
    private static readonly (string Name, Color Color)[] Swatches =
    {
        ("白", Colors.White),
        ("黑", Colors.Black),
        ("紅", Color.FromRgb(255, 0, 0)),
        ("綠", Color.FromRgb(0, 255, 0)),
        ("藍", Color.FromRgb(0, 0, 255)),
        ("青", Color.FromRgb(0, 255, 255)),
        ("洋紅", Color.FromRgb(255, 0, 255)),
        ("黃", Color.FromRgb(255, 255, 0)),
        ("灰 50%", Color.FromRgb(128, 128, 128)),
    };

    private int _i;
    private bool _hintOn = true;

    public ScreenTestWindow()
    {
        InitializeComponent();
        Apply();
    }

    private void Apply()
    {
        var (name, color) = Swatches[_i];
        Root.Background = new SolidColorBrush(color);
        HintTitle.Text = $"螢幕檢測 ・ 純色 {_i + 1}/{Swatches.Length}（{name}）";
    }

    private void Next() { _i = (_i + 1) % Swatches.Length; Apply(); }
    private void Prev() { _i = (_i - 1 + Swatches.Length) % Swatches.Length; Apply(); }
    private void ToggleHint() { _hintOn = !_hintOn; Hint.Visibility = _hintOn ? Visibility.Visible : Visibility.Collapsed; }

    private void Window_Click(object sender, MouseButtonEventArgs e) => Next();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left: Prev(); break;
            case Key.Right or Key.Space or Key.Enter: Next(); break;
            case Key.H: ToggleHint(); break;
        }
    }
}
