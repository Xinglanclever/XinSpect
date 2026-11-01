using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 總覽「詳細資訊」的一列：小圓點、標籤、值。
///
/// <para>
/// <see cref="Value"/> 是 <see cref="object"/> 而不是字串——同一個區塊裡，主機板是兩行文字、
/// 處理器是「型號 ＋ 核心數」兩行、硬碟與網卡是一串裝置（ItemsControl）。把它壓成字串會逼
/// 呼叫端在程式碼裡拼字，而那正是「畫面上看得懂、程式裡查不到」的老問題。
/// </para>
/// </summary>
public partial class InfoRow : UserControl
{
    public InfoRow() => InitializeComponent();

    /// <summary>左欄標籤（例如「主機板」）。</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(InfoRow), new PropertyMetadata(""));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>右欄內容：字串、面板或 ItemsControl 皆可。</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(object), typeof(InfoRow), new PropertyMetadata(null));

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
