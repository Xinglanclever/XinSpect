using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 菜鳥儀表板：簡易模式首頁，把「這台電腦是什麼、健不健康、能做什麼」濃縮成一頁。
/// 所有資料繫結到 MainViewModel 的既有屬性，不建立新的服務或硬體存取。
/// </summary>
public partial class BeginnerDashboardView : UserControl
{
    public BeginnerDashboardView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    private void Cleanup_Click(object sender, RoutedEventArgs e)
        => Shell.Main?.NavigateToUtility("cleanup");

    private void MemClean_Click(object sender, RoutedEventArgs e)
        => Shell.Main?.NavigateToUtility("memclean");

    private void Export_Click(object sender, RoutedEventArgs e)
        => Vm?.ExportReport();

    /// <summary>
    /// 症狀鈕：把使用者送到故障排查嚮導並直接帶到對應的那張卡。
    /// <para>
    /// <b>這裡不再自己決定「某某症狀要跳哪一頁」。</b>那張對照表原本寫在這裡，
    /// 而嚮導裡的六個場景每個都已經有「可能原因／檢查步驟／建議動作／深入頁面」——
    /// 兩份對照表講同一件事，遲早會不一樣；現在單一來源是 <see cref="TroubleshootService"/>，
    /// 這一頁只負責把症狀名帶過去（見 <see cref="TroubleshootView.Open"/>）。
    /// </para>
    /// </summary>
    private void Troubleshoot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string symptom) return;
        var main = Shell.Main;
        if (main is null) return;

        // 嚮導不是進階頁，所以不必離開簡易模式；症狀名 → 場景編號只對應名字，不重做判斷。
        string scenario = symptom switch
        {
            "game" => "game-lag",
            "fan" => "fan-noise",
            "boot" => "slow-boot",
            "net" => "network",
            _ => symptom,   // slow／bsod 的症狀名與場景編號同名
        };

        TroubleshootView.Open(main, scenario);
    }
}
