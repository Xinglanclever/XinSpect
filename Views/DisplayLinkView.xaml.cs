using System.Windows.Controls;

namespace XinSpect;

/// <summary>顯示鏈路真相頁：唯讀查詢，成本很低，故第一次進頁即自動讀一次。</summary>
public partial class DisplayLinkView : UserControl
{
    public DisplayLinkView()
    {
        InitializeComponent();
        Loaded += (_, _) => Vm?.DisplayLink.EnsureLoaded();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void Refresh_Click(object sender, System.Windows.RoutedEventArgs e) => Vm?.DisplayLink.Refresh();
}
