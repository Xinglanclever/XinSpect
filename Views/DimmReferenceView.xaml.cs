using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>DIMM 插槽定義：用 MainViewModel 上那一份服務，不自建第二份。</summary>
public partial class DimmReferenceView : UserControl
{
    private bool _loaded;

    public DimmReferenceView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            if (Vm is not { } vm) return;
            _loaded = true;
            DataContext = vm.DimmReference;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;
}
