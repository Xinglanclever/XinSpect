using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// NVMe 電源狀態頁：宣告值一按即讀（唯讀查詢），實測則要使用者自己按下才跑。
/// </summary>
/// <remarks>
/// 實測會刻意閒置約 8 秒，那段時間畫面上要看得出「正在等」，所以進度文字綁在服務上而非這裡。
/// </remarks>
public partial class NvmePowerView : UserControl
{
    public NvmePowerView()
    {
        InitializeComponent();
        // 磁碟清單在第一次進頁時才列（建構頁面不碰硬體）
        Loaded += (_, _) => Vm?.NvmePower.EnsureDrives();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void Read_Click(object sender, System.Windows.RoutedEventArgs e) => Vm?.NvmePower.Read();

    private void Measure_Click(object sender, System.Windows.RoutedEventArgs e) => Vm?.NvmePower.Measure();
}
