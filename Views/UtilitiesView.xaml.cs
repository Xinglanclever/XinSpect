using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 實用工具分頁：內建小工具的容器頁。左側子導覽切換各工具，右側承載工具面板。
/// 每個工具為獨立自持的 UserControl（自帶服務執行個體），採「一個一個加入」的方式擴充：
/// 於 _tools 陣列與左側 ListBox 相同索引處各加入一筆即可。
/// 目前收錄：連接埠占用。後續預定：垃圾清理、Hosts 編輯、藍屏(minidump)分析、右鍵選單管理……
/// </summary>
public partial class UtilitiesView : UserControl
{
    // 與左側 SubNav 的 ListBoxItem 平行對應（同索引 = 同工具）。
    private readonly UserControl[] _tools;

    public UtilitiesView()
    {
        InitializeComponent();
        _tools = new UserControl[]
        {
            new PortUsageView(),
            new HostsEditorView(),
            new BsodView(),
            new CleanupView(),
            new BatteryView(),
            new ContextMenuView(),
            new NetworkSpeedView(),
            new MemoryCleanView(),
            new StartupView(),
            new DnsView(),
            new DiskScanView(),
            new RankingView(),
        };
        SubNav.SelectedIndex = 0;
    }

    private void SubNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = SubNav.SelectedIndex;
        if (i >= 0 && i < _tools.Length)
            ToolHost.Content = _tools[i];
    }
}
