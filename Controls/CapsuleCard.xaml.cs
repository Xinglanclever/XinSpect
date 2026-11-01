using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 總覽最上面的膠囊卡：一個圖示、一行標籤、一個主值，外加一行可省略的副值。
///
/// <para>
/// 為什麼是一個控制項而不是在 <c>OverviewView.xaml</c> 裡抄三遍：三張卡只有文字與色相不同，
/// 抄三遍的下場是「改一次圓角要改三個地方」——這個專案已經在別的地方吃過這種虧。
/// </para>
///
/// <para>
/// 圖示用 Segoe MDL2 Assets 的字型字符（Windows 10 1607 起內建），不另外塞圖檔：
/// 一個字符就是一個向量，縮放不糊，也不必為了三顆圖示多背三個資源。
/// </para>
/// </summary>
public partial class CapsuleCard : UserControl
{
    public CapsuleCard()
    {
        InitializeComponent();
        // 沒指定色相就用主題強調色；在這裡補一次，否則沒設 Tint 的卡片會是黑字黑底（看不見）。
        Loaded += (_, _) => { if (Tint is null) Tint = (Brush)FindResource("AccentBrush"); };
    }

    /// <summary>卡片左上角的小字（例如「機型資訊」）。</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(CapsuleCard), new PropertyMetadata(""));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>主值（粗體大字）。</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(CapsuleCard), new PropertyMetadata("—"));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>副值（小字、次要顏色）。空字串時整行收起，不留一段空白。</summary>
    public static readonly DependencyProperty SubProperty = DependencyProperty.Register(
        nameof(Sub), typeof(string), typeof(CapsuleCard), new PropertyMetadata("", OnSubChanged));

    public string Sub
    {
        get => (string)GetValue(SubProperty);
        set => SetValue(SubProperty, value);
    }

    /// <summary>Segoe MDL2 Assets 的字符（例如 <c>\uE770</c> 是「系統」）。</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(CapsuleCard), new PropertyMetadata("", OnGlyphChanged));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>圖示與底板色相。留空時用主題強調色。</summary>
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(CapsuleCard), new PropertyMetadata(null, OnTintChanged));

    public Brush? Tint
    {
        get => (Brush?)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    private static void OnSubChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (CapsuleCard)d;
        string text = e.NewValue as string ?? "";
        card.SubText.Text = text;
        card.SubText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CapsuleCard)d).IconGlyph.Text = e.NewValue as string ?? "";

    private static void OnTintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (CapsuleCard)d;
        Brush brush = e.NewValue as Brush ?? (Brush)card.FindResource("AccentBrush");
        card.IconPlane.Background = brush;
        card.IconGlyph.Foreground = brush;
    }
}
