using System.Windows;

namespace XinSpect;

/// <summary>特殊型號徽章一覽視窗（自「關於」開啟）：陳列所有專屬 CPU / GPU 徽章示意。</summary>
public partial class IconGalleryWindow : Window
{
    public IconGalleryWindow()
    {
        InitializeComponent();
    }

    // 「朕知道了」頁腳鈕：關閉本視窗。
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
