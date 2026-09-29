using System.Windows;

namespace XinSpect;

/// <summary>
/// MessageBox 的在地化包裝：訊息與標題一律先過 <see cref="LanguageService.T"/>，簡體模式下對話框
/// 文字也跟著轉。全專案的 MessageBox 一律走這裡——直接呼叫 System.Windows.MessageBox.Show 那句話會漏翻。
/// （視覺樹裡的文字由 ConvertVisualTree／ChineseConverter 處理；對話框不在視覺樹上，故需在產出點轉。）
/// </summary>
public static class XMsg
{
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon)
        => MessageBox.Show(LanguageService.T(text), LanguageService.T(caption), button, icon);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
        => MessageBox.Show(LanguageService.T(text), LanguageService.T(caption), button, icon, defaultResult);
}
