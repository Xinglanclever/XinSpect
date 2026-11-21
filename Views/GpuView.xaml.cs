using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
namespace XinSpect;
public partial class GpuView : UserControl
{
    public GpuView() => InitializeComponent();

    private async void ReinitializeAll_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try { await vm.ReinitializeAllAsync(); }
        catch (Exception ex) { vm.StatusText = "重新擷取失敗：" + ex.Message; }
    }
}
