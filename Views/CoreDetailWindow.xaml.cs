using System.Windows;

namespace XinSpect;

/// <summary>
/// 「每核心明細」彈出視窗。DataContext 由開啟端指定為 <see cref="OverclockService"/>，
/// 呈現每核心即時溫度、每核心電壓設定值與封裝級 Vcore／電流波形。純唯讀呈現，不做任何寫入。
/// </summary>
public partial class CoreDetailWindow : Window
{
    public CoreDetailWindow(OverclockService overclock)
    {
        InitializeComponent();
        DataContext = overclock;
        // 獨立視窗不在主視覺樹的逐頁轉換範圍：開啟時自己轉換一次（冪等，重複呼叫安全）。
        if (LanguageService.IsSimplified)
            LanguageService.ConvertVisualTree(this, true);
    }
}
