using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>標籤 + 數值的一列，供各資訊卡重複使用。</summary>
public partial class FieldRow : UserControl
{
    public FieldRow() => InitializeComponent();

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(FieldRow), new PropertyMetadata(""));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(FieldRow), new PropertyMetadata("—"));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
