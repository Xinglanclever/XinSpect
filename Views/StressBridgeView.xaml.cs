using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 運算穩定性壓測頁：自持 <see cref="StressBridgeService"/>（顯式指派 DataContext，避開延遲載入外殼
/// 對根元素 DataContext 綁定不解析的陷阱）。偵測→啟動→擷取 y-cruncher，結果由服務判定後顯示。
/// </summary>
public partial class StressBridgeView : UserControl
{
    private readonly StressBridgeService _svc = new();

    public StressBridgeView()
    {
        InitializeComponent();
        DataContext = _svc;
    }

    private void Redetect_Click(object sender, RoutedEventArgs e) => _svc.Redetect();

    private void OfficialPage_Click(object sender, RoutedEventArgs e) => _svc.OpenOfficialPage();

    private void StartStop_Click(object sender, RoutedEventArgs e) => _svc.Toggle();

    private void Duration_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string s } && int.TryParse(s, out int min))
            _svc.DurationMinutes = min;
    }

    private void ManualPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = LanguageService.T("指定 y-cruncher.exe"),
            // Filter 是「顯示名|模式」成對字串，整串過 T() 會因查無此鍵原樣回退。
            // 改為逐段翻譯顯示名，模式（*.exe）保持原樣。
            Filter = "y-cruncher (y-cruncher.exe)|y-cruncher.exe|" + LanguageService.T("執行檔 (*.exe)") + "|*.exe",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true) _svc.SetManualPath(dlg.FileName);
    }
}
