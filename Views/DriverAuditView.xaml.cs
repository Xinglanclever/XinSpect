using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>驅動程式稽核頁：使用者按下「重新掃描」才去查 WMI 驅動存放區（唯讀）。</summary>
public partial class DriverAuditView : UserControl
{
    public DriverAuditView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm?.DriverAudit.Refresh();
}
