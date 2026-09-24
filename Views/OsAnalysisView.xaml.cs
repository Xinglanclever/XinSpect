using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>作業系統分析子頁：進入時自動讀取一次，也可按「重新掃描」重來（唯讀）。</summary>
public partial class OsAnalysisView : UserControl
{
    private bool _loaded;

    public OsAnalysisView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            Vm?.OsAnalysis.Refresh();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm?.OsAnalysis.Refresh();
}
