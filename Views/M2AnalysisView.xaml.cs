using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>M.2 / U.2 介面分析：用 MainViewModel 上那一份服務，不自建第二份。</summary>
public partial class M2AnalysisView : UserControl
{
    private bool _loaded;

    public M2AnalysisView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            if (Vm is not { } vm) return;
            _loaded = true;
            DataContext = vm.M2Analysis;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;
}
