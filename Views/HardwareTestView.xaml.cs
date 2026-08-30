using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 硬體檢測（實用工具子頁）：把五個全螢幕檢測視窗集中在一處。
/// <para>
/// 1.6.2 之前這五項掛在工具箱的「曦覽內建」那一組裡。工具箱的職責是「Windows 內建工具
/// 一鍵開啟＋第三方工具導向官方下載」，讓它同時兼任導覽入口，等於同一件事在兩個地方各寫
/// 一份；螢幕檢測尤其常被找不到。現在檢測全部落在這一頁，工具箱不再有任何內建按鈕。
/// </para>
/// <para>
/// 視窗代號（screen／mouse／keyboard／speaker／motion）只在這裡出現一次，以免 XAML 與
/// 服務兩邊各記一份而走鏽。
/// </para>
/// </summary>
public partial class HardwareTestView : UserControl
{
    public HardwareTestView() => InitializeComponent();

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string code }) return;

        Window? w = code switch
        {
            "screen"   => new ScreenTestWindow(),
            "mouse"    => new MouseTestWindow(),
            "keyboard" => new KeyboardTestWindow(),
            "speaker"  => new SpeakerTestWindow(),
            "motion"   => new MotionTestWindow(),
            _          => null,
        };
        if (w is null)
        {
            Status.Text = $"未知的檢測視窗代號 {code}（內部錯誤，請回報）。";
            return;
        }

        w.Owner = Shell.TopWindow;
        w.Show();
        Status.Text = "檢測視窗已開啟；按 Esc 即可回到曦覽。";
    }
}
