using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

public partial class ToolboxView : UserControl
{
    public ToolboxView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    // 點選工具按鈕：由 Tag 取回對應項目，交由工具箱服務啟動 / 導向下載
    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ToolItem tool })
            Vm?.Toolbox.Launch(tool);
    }
}
