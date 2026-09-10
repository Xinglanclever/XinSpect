using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 全域語言服務：繁體↔簡體中文。使用 Windows 核心的 LCMapStringEx，不依賴外部字典。
/// </summary>
/// <remarks>
/// <para>
/// 轉換策略分三層：
/// ① 硬編碼在 XAML 裡的文字 → 頁面載入時由 <see cref="ConvertVisualTree"/> 遍歷轉換。
/// ② 透過 Binding 顯示的文字 → 由全域 <see cref="ChineseConverter"/> 掛在 Binding.Converter 上。
/// ③ 程式碼裡產生的文字（HelpCatalog、Changelog、狀態列）→ 在產出點呼叫 <see cref="T"/>。
/// </para>
/// <para>
/// 呼叫 <see cref="SetLanguage"/> 會立即轉換整棵視覺樹、觸發 <see cref="Changed"/> 事件
/// 並持久化到 SettingsService。
/// </para>
/// </remarks>
public static class LanguageService
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string lpLocaleName, uint dwMapFlags,
        string lpSrcStr, int cchSrc,
        char[]? lpDestStr, int cchDest,
        IntPtr lpVersionInformation, IntPtr lpReserved, IntPtr sortHandle);

    private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;
    private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;
    private const string LOCALE_NAME_ZH = "zh";

    private static bool _simplified;

    /// <summary>目前是否為簡體模式。</summary>
    public static bool IsSimplified => _simplified;

    /// <summary>語言切換時觸發。訂閱者應重新產生動態文字。</summary>
    public static event Action? Changed;

    /// <summary>
    /// 初始化：從 SettingsService 讀取已存偏好。在 MainWindow 建構前呼叫。
    /// </summary>
    public static void Initialize(SettingsService settings)
        => _simplified = settings.SimplifiedChinese;

    /// <summary>
    /// 切換語言並立即套用到整棵視覺樹。
    /// </summary>
    public static void SetLanguage(bool simplified, SettingsService settings)
    {
        _simplified = simplified;
        settings.SimplifiedChinese = simplified;

        // 轉換整棵視覺樹
        if (Shell.Main is { } main)
        {
            ConvertVisualTree(main, simplified);
            // 重建導覽（頁面標題需要轉換）
            main.RebuildNavIfNeeded();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// 核心轉換函式：把一段文字轉為目前語言。
    /// 全專案統一透過此函式轉換，不直接呼叫 Windows API。
    /// </summary>
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text) || !_simplified) return text ?? "";
        return ToSimplified(text);
    }

    /// <summary>繁體→簡體。</summary>
    public static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        int len = LCMapStringEx(LOCALE_NAME_ZH, LCMAP_SIMPLIFIED_CHINESE,
            text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (len <= 0) return text;
        var buf = new char[len];
        LCMapStringEx(LOCALE_NAME_ZH, LCMAP_SIMPLIFIED_CHINESE,
            text, text.Length, buf, len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new string(buf);
    }

    /// <summary>簡體→繁體。</summary>
    public static string ToTraditional(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        int len = LCMapStringEx(LOCALE_NAME_ZH, LCMAP_TRADITIONAL_CHINESE,
            text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (len <= 0) return text;
        var buf = new char[len];
        LCMapStringEx(LOCALE_NAME_ZH, LCMAP_TRADITIONAL_CHINESE,
            text, text.Length, buf, len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new string(buf);
    }

    /// <summary>
    /// 遍歷 WPF 視覺樹，轉換所有硬編碼文字。
    /// 有 Binding 的 TextBlock 不動（那些透過 Converter 處理）。
    /// </summary>
    public static void ConvertVisualTree(DependencyObject root, bool simplified)
    {
        if (root is null) return;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            switch (child)
            {
                case TextBlock tb when tb.GetBindingExpression(TextBlock.TextProperty) is null
                                    && !string.IsNullOrEmpty(tb.Text):
                    tb.Text = simplified ? ToSimplified(tb.Text) : ToTraditional(tb.Text);
                    break;
                case ContentControl cc when cc.Content is string s && !string.IsNullOrEmpty(s)
                                         && cc.GetBindingExpression(ContentControl.ContentProperty) is null:
                    cc.Content = simplified ? ToSimplified(s) : ToTraditional(s);
                    break;
                case HeaderedContentControl hcc when hcc.Header is string h && !string.IsNullOrEmpty(h):
                    hcc.Header = simplified ? ToSimplified(h) : ToTraditional(h);
                    break;
            }
            ConvertVisualTree(child, simplified);
        }
    }

    /// <summary>
    /// 頁面首次載入時呼叫：轉換該頁的硬編碼文字。
    /// </summary>
    public static void ConvertPage(UserControl page)
    {
        if (_simplified)
            ConvertVisualTree(page, true);
    }
}

/// <summary>
/// WPF Binding 用的繁簡轉換器。掛在全域資源字典中，
/// 任何 TextBlock 的 Binding 加上 Converter={StaticResource Lang} 即可。
/// </summary>
public sealed class ChineseConverter : IValueConverter
{
    public static readonly ChineseConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? LanguageService.T(s) : value ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
