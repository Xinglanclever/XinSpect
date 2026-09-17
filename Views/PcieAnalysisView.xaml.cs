using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>PCIe 分析子頁：進入時自動分析一次，也可按「重新分析」重來（唯讀）。</summary>
public partial class PcieAnalysisView : UserControl
{
    private bool _loaded;

    public PcieAnalysisView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            Vm?.PcieAnalysis.Refresh();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm?.PcieAnalysis.Refresh();
}
