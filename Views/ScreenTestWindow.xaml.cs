using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 螢幕檢測：全螢幕測試畫面循環。
/// 純色（色卡）用於檢查亮點／暗點（壞點）、漏光與背光均勻度；
/// 圖形模式（灰階階梯／灰階漸層／彩色漸層／棋盤）用於檢查階調表現、色帶與像素響應。
/// 純 WPF、零相依、零驅動；點擊或方向鍵切換，Esc 離開。
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

    private const string PatGraySteps = "灰階階梯";
    private const string PatGrayRamp = "灰階漸層";
    private const string PatRgbRamp = "彩色漸層";
    private const string PatCheckerFine = "棋盤（1px）";
    private const string PatCheckerCoarse = "棋盤（8px）";

    /// <summary>全部測試畫面：前段純色色卡、後段圖形模式。Color 為 null 代表圖形模式（用 Name 建）。</summary>
    private static readonly (string Name, Color? Color)[] Screens =
        Swatches.Select(s => (s.Name, (Color?)s.Color))
                .Concat(new (string, Color?)[]
                {
                    (PatGraySteps, null),
                    (PatGrayRamp, null),
                    (PatRgbRamp, null),
                    (PatCheckerFine, null),
                    (PatCheckerCoarse, null),
                }).ToArray();

    private int _i;
    private bool _hintOn = true;
    private FrameworkElement? _pattern;

    public ScreenTestWindow()
    {
        InitializeComponent();
        Apply();
    }

    private void Apply()
    {
        var (name, color) = Screens[_i];
        if (_pattern is not null)
        {
            Root.Children.Remove(_pattern);
            _pattern = null;
        }
        if (color is { } c)
        {
            Root.Background = new SolidColorBrush(c);
        }
        else
        {
            Root.Background = Brushes.Black;
            _pattern = BuildPattern(name);
            Root.Children.Insert(0, _pattern);
        }
        HintTitle.Text = $"螢幕檢測 ・ {_i + 1}/{Screens.Length}（{name}）";
    }

    /// <summary>依名稱建出全螢幕測試圖形。</summary>
    private static FrameworkElement BuildPattern(string name)
    {
        switch (name)
        {
            case PatGraySteps:
            {
                // 16 階灰階：由黑到白等亮度階梯，檢查相鄰階調能否分得出來（banding／dithering 品質）
                var grid = new UniformGrid { Rows = 1 };
                for (int i = 0; i < 16; i++)
                {
                    byte v = (byte)(i * 255 / 15);
                    grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(v, v, v)) });
                }
                return grid;
            }
            case PatGrayRamp:
                return new Border
                {
                    Background = new LinearGradientBrush(Color.FromRgb(0, 0, 0), Color.FromRgb(255, 255, 255), 0),
                };
            case PatRgbRamp:
            {
                // 三段橫向漸層 R→G→B：檢查色帶與過渡平滑度
                var grid = new UniformGrid { Rows = 1 };
                grid.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromRgb(255, 0, 0), Color.FromRgb(0, 255, 0), 0) });
                grid.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromRgb(0, 255, 0), Color.FromRgb(0, 0, 255), 0) });
                grid.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromRgb(0, 0, 255), Color.FromRgb(255, 0, 0), 0) });
                return grid;
            }
            default:
            {
                int px = name == PatCheckerFine ? 1 : 8;
                // 貼磚式棋盤：1px 版以原生解析度檢查像素響應與銳利度，8px 版檢查對比與摩爾紋
                var brush = new DrawingBrush
                {
                    TileMode = TileMode.Tile,
                    Viewport = new Rect(0, 0, px * 2, px * 2),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Drawing = new GeometryDrawing
                    {
                        Brush = Brushes.White,
                        Geometry = new RectangleGeometry(new Rect(0, 0, px, px)),
                    },
                };
                return new Border { Background = brush };
            }
        }
    }

    private void Next() { _i = (_i + 1) % Screens.Length; Apply(); }
    private void Prev() { _i = (_i - 1 + Screens.Length) % Screens.Length; Apply(); }
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
