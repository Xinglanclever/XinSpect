using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XinSpect;

/// <summary>
/// 進場動畫小工具：
///   • Anim.RevealDelay 附加屬性 → 元素載入時做「淡入 + 上滑」，可指定延遲以達成卡片交錯進場。
///   • PageTransition.PlayEnter → 分頁切換時對承載容器做整體淡入 + 輕微上滑。
/// </summary>
public static class Anim
{
    public static readonly DependencyProperty RevealDelayProperty =
        DependencyProperty.RegisterAttached(
            "RevealDelay", typeof(double), typeof(Anim),
            new PropertyMetadata(double.NaN, OnRevealDelayChanged));

    public static void SetRevealDelay(DependencyObject o, double v) => o.SetValue(RevealDelayProperty, v);
    public static double GetRevealDelay(DependencyObject o) => (double)o.GetValue(RevealDelayProperty);

    private static void OnRevealDelayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= OnLoaded;
        if (!double.IsNaN((double)e.NewValue))
            fe.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        double delay = GetRevealDelay(fe);
        if (double.IsNaN(delay)) return;
        Reveal(fe, delay, 16);
    }

    /// <summary>對元素做淡入 + 由下上滑；beginMs 提供交錯延遲。</summary>
    public static void Reveal(FrameworkElement fe, double beginMs, double slide)
    {
        var tt = new TranslateTransform(0, slide);
        fe.RenderTransform = tt;
        fe.Opacity = 0;                        // 基準值先歸零，延遲期間不會閃現

        var begin = TimeSpan.FromMilliseconds(beginMs);
        var dur = TimeSpan.FromMilliseconds(360);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation(0, 1, dur) { BeginTime = begin, EasingFunction = ease };
        var rise = new DoubleAnimation(slide, 0, dur) { BeginTime = begin, EasingFunction = ease };

        fe.BeginAnimation(UIElement.OpacityProperty, fade);
        tt.BeginAnimation(TranslateTransform.YProperty, rise);
    }
}

/// <summary>分頁切換時的整體進場動畫。</summary>
public static class PageTransition
{
    public static void PlayEnter(FrameworkElement host)
    {
        var tt = new TranslateTransform(0, 10);
        host.RenderTransform = tt;
        host.Opacity = 0;

        var dur = TimeSpan.FromMilliseconds(240);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, dur) { EasingFunction = ease });
    }
}
