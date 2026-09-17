using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>進階驅動分析子頁：進入時自動分析一次（基於驅動稽核結果），也可按按鈕重來（唯讀）。</summary>
public partial class DriverAnalysisView : UserControl
{
    private bool _loaded;

    public DriverAnalysisView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            _ = AnalyzeAsync();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    private void OnAnalyze_Click(object sender, RoutedEventArgs e) => _ = AnalyzeAsync();

    private async Task AnalyzeAsync()
    {
        var vm = Vm;
        if (vm is null) return;
        await vm.DriverAnalysis.AnalyzeAsync(vm.DriverAudit);
    }
}
