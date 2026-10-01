using System.Windows;

namespace XinSpect;

/// <summary>特殊型號徽章一覽視窗（自「關於」開啟）：陳列所有專屬 CPU / GPU 徽章示意。</summary>
public partial class IconGalleryWindow : Window
{
    public IconGalleryWindow()
    {
        InitializeComponent();
        // 獨立視窗不在主視覺樹的逐頁轉換範圍：開啟時自己轉換一次（冪等，重複呼叫安全）。
        if (LanguageService.IsSimplified)
            LanguageService.ConvertVisualTree(this, true);
    }

    // 「朕知道了」頁腳鈕：關閉本視窗。
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
