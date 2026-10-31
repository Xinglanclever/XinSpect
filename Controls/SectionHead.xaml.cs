using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XinSpect;

/// <summary>
/// 卡片標題列：<see cref="Text"/> ＋ 一顆 <see cref="HelpDot"/>。
/// <para>
/// 鍵值一律明寫（形如 <c>cpu/快取 Cache</c>，前綴是所在分頁）而不是由標題自動推導：
/// 版面上看得見鍵值，測試才能純靠讀取 XAML 檢查「每個鍵都查得到、說明表裡沒有孤兒」，
/// 不必在執行期沿著視覺樹往上猜自己屬於哪一頁。
/// </para>
/// </summary>
public partial class SectionHead : UserControl
{
    private bool _ruleDrawn;

    public SectionHead()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 標題左側那道紅規「畫出來」：140 ms 由左端向右長滿，Opacity 同時淡入。
    /// <para>
    /// 用 <see cref="ScaleTransform"/> 而不是動 <c>Width</c>，避免每一格都重新排版文字（3 px 的
    /// 位移會讓整行標題持續抖動）；動畫結束時還原成靜態狀態，不留下任何 RenderTransform。
    /// 只演第一次：回到同一頁時分頁本身已經有 <see cref="PageTransition"/>，每道規再各演一次就吵了。
    /// </para>
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ruleDrawn || !Motion.Enabled) return;
        _ruleDrawn = true;

        var scale = new ScaleTransform(0, 1);
        Rule.RenderTransformOrigin = new Point(0, 0.5);
        Rule.RenderTransform = scale;
        Rule.Opacity = 0;

        var dur = TimeSpan.FromMilliseconds(140);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        Rule.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        grow.Completed += (_, _) =>
        {
            // 交還給版面：動畫基準值若留著，換主題重繪時會多繞一層。
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Rule.BeginAnimation(OpacityProperty, null);
            scale.ScaleX = 1;
            Rule.Opacity = 1;
        };
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SectionHead), new PropertyMetadata(""));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty HelpKeyProperty = DependencyProperty.Register(
        nameof(HelpKey), typeof(string), typeof(SectionHead), new PropertyMetadata(""));

    /// <summary>說明表鍵值，形如 <c>cpu/快取 Cache</c>；查不到時問號自動隱藏。</summary>
    public string HelpKey
    {
        get => (string)GetValue(HelpKeyProperty);
        set => SetValue(HelpKeyProperty, value);
    }
}
