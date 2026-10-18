using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 事實總表頁：把本版建置實作的每一把事實鍵攤成一張可搜尋的表（見 <see cref="FactAtlasService"/>）。
/// <para>
/// <b>資料來源是本輪已經收集的事實集，不是這一頁自己去讀硬體。</b>
/// 這一頁的職責是把「已經算出來的東西」攤開——如果它自己去碰驅動或感測器，
/// 就會變成第二條掃描路徑，兩條路徑的數字遲早不一致。
/// </para>
/// </summary>
public partial class FactAtlasView : UserControl
{
    public FactAtlasView()
    {
        InitializeComponent();
        Load();
    }

    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;

    /// <summary>載入本輪事實集（沒有收集到東西時如實顯示「本次未收集」）。</summary>
    private void Load()
    {
        var vm = Vm;
        vm?.FactAtlas.Load(vm.EvidenceLab.AllFacts);
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Load();
}
