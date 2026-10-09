using System.Windows;

namespace XinSpect;

/// <summary>
/// 「每核心明細」彈出視窗。DataContext 由開啟端指定為 <see cref="OverclockService"/>，
/// 呈現每核心即時溫度、每核心電壓設定值與封裝級 Vcore／電流波形。純唯讀呈現，不做任何寫入。
/// </summary>
public partial class CoreDetailWindow : Window
{
    /// <param name="overclock">每核心資料來源（此視窗的 DataContext）。</param>
    /// <param name="physicalCores">拓撲回報的實體核心數（0＝未知）：熱區圖靠它補出缺讀值的格子。</param>
    public CoreDetailWindow(OverclockService overclock, int physicalCores = 0)
    {
        InitializeComponent();
        DataContext = overclock;
        HeatMap.PhysicalCores = physicalCores;
        // 獨立視窗不在主視覺樹的逐頁轉換範圍：開啟時自己轉換一次（冪等，重複呼叫安全）。
        if (LanguageService.IsSimplified)
            LanguageService.ConvertVisualTree(this, true);
    }
}
