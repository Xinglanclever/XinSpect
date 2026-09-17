using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>SATA / eSATA 分析子頁：進入時自動掃描一次，也可按「重新掃描」重來（唯讀）。</summary>
public partial class SataAnalysisView : UserControl
{
    private bool _loaded;

    public SataAnalysisView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            Vm?.SataAnalysis.Refresh();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm?.SataAnalysis.Refresh();
}
