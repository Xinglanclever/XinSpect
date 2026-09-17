using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>CPU 腳座腳位參考：用 MainViewModel 上那一份服務，不自建第二份。</summary>
/// <remarks>
/// 這裡先前還畫了一條「腳位分類分佈」色條（供電／接地／訊號／保留各幾支）。
/// 那些數字是生成的——12 個腳座的四類相加剛好等於總腳數，但 Intel／AMD 從未公佈這種分解。
/// 專案的核心是「只寫真的知道的事」，所以整條移除，只留真的總腳數。
/// </remarks>
public partial class CpuPinoutView : UserControl
{
    private bool _loaded;

    public CpuPinoutView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            if (Vm is not { } vm) return;   // 拿不到主檢視模型時不設旗標，下次再試
            _loaded = true;
            DataContext = vm.CpuPinout;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;
}
