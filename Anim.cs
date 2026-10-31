using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XinSpect;

/// <summary>
/// 進場動畫小工具：
///   • Anim.RevealDelay 附加屬性 → 元素載入時做「淡入 + 上滑」，可指定延遲以達成卡片交錯進場。
///   • PageTransition.PlayEnter → 分頁切換時對承載容器做整體淡入 + 輕微上滑。
///
/// <para>
/// <b>乾淨幹練而不是花俏</b>：位移只到個位數像素、時長壓在 150–200 ms、一律 EaseOut。
/// 這個介面的主要工作是讀數字，動畫只負責「東西從哪裡來」，不該讓人等它演完。
/// 設定頁關掉「動態效果」（<see cref="Motion.Enabled"/>）時整套直接跳到終點，不留殘影。
/// </para>
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
        Reveal(fe, delay, 8);
    }

    /// <summary>對元素做淡入 + 由下上滑；beginMs 提供交錯延遲。</summary>
    public static void Reveal(FrameworkElement fe, double beginMs, double slide)
    {
        if (!Motion.Enabled) { fe.Opacity = 1; return; }   // 關掉動態效果：直接到位

        var tt = new TranslateTransform(0, slide);
        fe.RenderTransform = tt;
        fe.Opacity = 0;                        // 基準值先歸零，延遲期間不會閃現

        var begin = TimeSpan.FromMilliseconds(beginMs);
        var dur = TimeSpan.FromMilliseconds(200);
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
        if (!Motion.Enabled) { host.Opacity = 1; return; }

        // 5 px、150 ms：足以交代「換頁了」，又不會讓每一次點擊都像在等轉場。
        var tt = new TranslateTransform(0, 5);
        host.RenderTransform = tt;
        host.Opacity = 0;

        var dur = TimeSpan.FromMilliseconds(150);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, dur) { EasingFunction = ease });
    }
}
