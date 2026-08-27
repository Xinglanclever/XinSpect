using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 鍵盤檢測：全螢幕虛擬鍵盤，逐鍵確認觸發、標記已測試鍵、統計同時按鍵數（防鬼鍵／NKRO），
/// 並顯示最後按鍵的虛擬鍵碼與硬體掃描碼。純 WPF、零相依；連按兩下 Esc 離開。
/// </summary>
public partial class KeyboardTestWindow : Window
{
    private static readonly Brush KeyIdle = new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x29));
    private static readonly Brush KeyTested = new SolidColorBrush(Color.FromRgb(0x2A, 0x3C, 0x30)); // 按過：偏綠
    private static readonly Brush KeyActive = new SolidColorBrush(Color.FromRgb(0x39, 0x87, 0xE5)); // 按住：藍
    private static readonly Brush TextInk = new SolidColorBrush(Color.FromRgb(0xC7, 0xCC, 0xD6));

    private readonly Dictionary<Key, Border> _caps = new();
    private readonly HashSet<Key> _held = new();
    private readonly HashSet<Key> _tested = new();
    private int _maxHeld;
    private double _lastEscMs = double.NegativeInfinity;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public KeyboardTestWindow()
    {
        InitializeComponent();
        BuildKeyboard();
        Loaded += (_, _) => { Focus(); Keyboard.Focus(this); };
    }

    // 一個按鍵的版面定義：顯示字、對應 Key、相對寬度（1 = 標準鍵）。K==Key.None 表示間隔佔位。
    private readonly record struct Cap(string Text, Key K, double Units = 1);
    private static Cap Gap(double u) => new("", Key.None, u);

    private void BuildKeyboard()
    {
        var rows = new[]
        {
            new[] { new Cap("Esc", Key.Escape), Gap(0.55),
                    new Cap("F1", Key.F1), new Cap("F2", Key.F2), new Cap("F3", Key.F3), new Cap("F4", Key.F4), Gap(0.3),
                    new Cap("F5", Key.F5), new Cap("F6", Key.F6), new Cap("F7", Key.F7), new Cap("F8", Key.F8), Gap(0.3),
                    new Cap("F9", Key.F9), new Cap("F10", Key.F10), new Cap("F11", Key.F11), new Cap("F12", Key.F12), Gap(0.55),
                    new Cap("PrtSc", Key.PrintScreen), new Cap("ScrLk", Key.Scroll), new Cap("Pause", Key.Pause) },

            new[] { new Cap("`", Key.OemTilde), new Cap("1", Key.D1), new Cap("2", Key.D2), new Cap("3", Key.D3),
                    new Cap("4", Key.D4), new Cap("5", Key.D5), new Cap("6", Key.D6), new Cap("7", Key.D7),
                    new Cap("8", Key.D8), new Cap("9", Key.D9), new Cap("0", Key.D0), new Cap("-", Key.OemMinus),
                    new Cap("=", Key.OemPlus), new Cap("Backspace", Key.Back, 2), Gap(0.55),
                    new Cap("Ins", Key.Insert), new Cap("Home", Key.Home), new Cap("PgUp", Key.PageUp) },

            new[] { new Cap("Tab", Key.Tab, 1.5), new Cap("Q", Key.Q), new Cap("W", Key.W), new Cap("E", Key.E),
                    new Cap("R", Key.R), new Cap("T", Key.T), new Cap("Y", Key.Y), new Cap("U", Key.U),
                    new Cap("I", Key.I), new Cap("O", Key.O), new Cap("P", Key.P), new Cap("[", Key.OemOpenBrackets),
                    new Cap("]", Key.OemCloseBrackets), new Cap("\\", Key.OemPipe, 1.5), Gap(0.55),
                    new Cap("Del", Key.Delete), new Cap("End", Key.End), new Cap("PgDn", Key.PageDown) },

            new[] { new Cap("Caps", Key.CapsLock, 1.75), new Cap("A", Key.A), new Cap("S", Key.S), new Cap("D", Key.D),
                    new Cap("F", Key.F), new Cap("G", Key.G), new Cap("H", Key.H), new Cap("J", Key.J),
                    new Cap("K", Key.K), new Cap("L", Key.L), new Cap(";", Key.OemSemicolon), new Cap("'", Key.OemQuotes),
                    new Cap("Enter", Key.Return, 2.25) },

            new[] { new Cap("Shift", Key.LeftShift, 2.25), new Cap("Z", Key.Z), new Cap("X", Key.X), new Cap("C", Key.C),
                    new Cap("V", Key.V), new Cap("B", Key.B), new Cap("N", Key.N), new Cap("M", Key.M),
                    new Cap(",", Key.OemComma), new Cap(".", Key.OemPeriod), new Cap("/", Key.OemQuestion),
                    new Cap("Shift", Key.RightShift, 2.75), Gap(1.55), new Cap("↑", Key.Up) },

            new[] { new Cap("Ctrl", Key.LeftCtrl, 1.25), new Cap("Win", Key.LWin, 1.25), new Cap("Alt", Key.LeftAlt, 1.25),
                    new Cap("Space", Key.Space, 6.25), new Cap("Alt", Key.RightAlt, 1.25), new Cap("Menu", Key.Apps, 1.25),
                    new Cap("Ctrl", Key.RightCtrl, 1.25), Gap(0.55),
                    new Cap("←", Key.Left), new Cap("↓", Key.Down), new Cap("→", Key.Right) },
        };

        const double U = 44, H = 42, M = 3;
        foreach (var row in rows)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var cap in row)
            {
                if (cap.K == Key.None)
                {
                    sp.Children.Add(new Border { Width = cap.Units * U, Height = H, Margin = new Thickness(M, M, M, M), Background = Brushes.Transparent });
                    continue;
                }
                double w = cap.Units * U + (cap.Units - 1) * (2 * M); // 寬鍵吃掉間距以對齊網格
                var tb = new TextBlock { Text = cap.Text, Foreground = TextInk, FontSize = 12.5, TextAlignment = TextAlignment.Center };
                var b = new Border
                {
                    Width = w, Height = H, Margin = new Thickness(M), CornerRadius = new CornerRadius(6),
                    Background = KeyIdle, Child = tb,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                tb.HorizontalAlignment = HorizontalAlignment.Center;
                tb.VerticalAlignment = VerticalAlignment.Center;
                sp.Children.Add(b);
                _caps[cap.K] = b;
            }
            KeyRows.Children.Add(sp);
        }
    }

    private void Win_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key k = e.Key == Key.System ? e.SystemKey : e.Key;

        // Alt+F4 照常關閉；連按兩下 Esc 離開（單次 Esc 仍當一般鍵測試）。
        if (k == Key.F4 && (Keyboard.Modifiers & ModifierKeys.Alt) != 0) { Close(); return; }
        if (k == Key.Escape && !e.IsRepeat)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            if (now - _lastEscMs < 500) { Close(); return; }
            _lastEscMs = now;
        }

        e.Handled = true;   // 攔截 Tab／方向鍵／空白的系統導覽與功能鍵嗶聲
        if (e.IsRepeat) return;

        _held.Add(k);
        _tested.Add(k);
        if (_held.Count > _maxHeld) _maxHeld = _held.Count;
        Highlight(k);
        UpdateStats(k, down: true);
    }

    private void Win_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key k = e.Key == Key.System ? e.SystemKey : e.Key;
        _held.Remove(k);
        Highlight(k);
        UpdateStats(k, down: false);
    }

    private void Highlight(Key k)
    {
        if (!_caps.TryGetValue(k, out var b)) return;
        b.Background = _held.Contains(k) ? KeyActive
                     : _tested.Contains(k) ? KeyTested
                     : KeyIdle;
    }

    private void UpdateStats(Key k, bool down)
    {
        if (down)
        {
            int vk = KeyInterop.VirtualKeyFromKey(k);
            uint sc = MapVirtualKey((uint)vk, 0); // MAPVK_VK_TO_VSC
            LastKeyText.Text = k.ToString();
            CodeText.Text = $"0x{vk:X2} / 0x{sc:X2}";
        }
        HeldText.Text = _held.Count.ToString();
        MaxHeldText.Text = _maxHeld.ToString();
        TestedText.Text = _tested.Count.ToString();
    }
}
