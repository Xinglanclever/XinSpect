using System.Windows;
using System.Windows.Controls;

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
    public SectionHead() => InitializeComponent();

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
