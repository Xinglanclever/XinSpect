using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
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

    /// <summary>繁體→簡體：先查詞組表（「記憶體」→「内存」），再讓 Windows 處理剩下的逐字轉換。</summary>
    public static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        // 先替換技術用語（LCMapStringEx 會把「記憶體」變成「记忆体」而不是「内存」）
        text = ZhTerms.ApplyToSimp(text);
        // 再讓 Windows 處理剩下的逐字繁→簡
        int len = LCMapStringEx(LOCALE_NAME_ZH, LCMAP_SIMPLIFIED_CHINESE,
            text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (len <= 0) return text;
        var buf = new char[len];
        int written = LCMapStringEx(LOCALE_NAME_ZH, LCMAP_SIMPLIFIED_CHINESE,
            text, text.Length, buf, len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new string(buf, 0, written > 0 ? written : len);   // 用實際寫入長度，絕不夾帶結尾 NUL
    }

    /// <summary>簡體→繁體：先查反向詞組表，再讓 Windows 處理逐字轉換。</summary>
    public static string ToTraditional(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        text = ZhTerms.ApplyToTrad(text);
        int len = LCMapStringEx(LOCALE_NAME_ZH, LCMAP_TRADITIONAL_CHINESE,
            text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (len <= 0) return text;
        var buf = new char[len];
        int written = LCMapStringEx(LOCALE_NAME_ZH, LCMAP_TRADITIONAL_CHINESE,
            text, text.Length, buf, len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new string(buf, 0, written > 0 ? written : len);   // 用實際寫入長度，絕不夾帶結尾 NUL
    }

    // 原文保存：第一次轉換某屬性時把原文（XAML 裡的繁體）存起來，之後永遠從原文出發轉換——
    // 繁體＝原文（無損）、簡體＝ToSimplified(原文)，往返可逆且冪等（根治「暫存→缓存→快取」漂移）。
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, string>> _orig = new();

    private static string FromOriginal(DependencyObject o, string slot, string current, bool simplified)
    {
        var map = _orig.GetOrCreateValue(o);
        if (!map.TryGetValue(slot, out var original)) { original = current; map[slot] = current; }
        return simplified ? ToSimplified(original) : original;
    }

    /// <summary>
    /// 遍歷視覺樹把硬編碼文字轉為目前語言。永遠從保存的原文出發，故可逆、冪等——
    /// 快取頁重新顯示、多次往返都不會累積損失。有 Binding 的走 <see cref="ChineseConverter"/>，此處不動。
    /// 涵蓋 TextBlock.Text／Run 行內文字／ContentControl.Content／Header／任何 FrameworkElement 的字串 ToolTip。
    /// </summary>
    public static void ConvertVisualTree(DependencyObject root, bool simplified)
    {
        if (root is null) return;
        ConvertNode(root, simplified);
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            ConvertVisualTree(VisualTreeHelper.GetChild(root, i), simplified);
    }

    private static void ConvertNode(DependencyObject d, bool simplified)
    {
        if (d is TextBlock tb)
        {
            if (tb.Inlines.Count > 0)
            {
                // 先快照成清單再改：改 Run.Text 會觸發 InlineCollection 變更，邊列舉邊改會丟例外。
                foreach (var r in tb.Inlines.OfType<Run>().ToList())
                    if (r.GetBindingExpression(Run.TextProperty) is null && !string.IsNullOrEmpty(r.Text))
                        r.Text = FromOriginal(r, "Text", r.Text, simplified);
            }
            else if (tb.GetBindingExpression(TextBlock.TextProperty) is null && !string.IsNullOrEmpty(tb.Text))
                tb.Text = FromOriginal(tb, "Text", tb.Text, simplified);
        }
        else if (d is ContentControl cc)   // 涵蓋 Button 等；HeaderedContentControl 亦是，故 Content 與 Header 都查
        {
            if (cc.Content is string s && !string.IsNullOrEmpty(s) && cc.GetBindingExpression(ContentControl.ContentProperty) is null)
                cc.Content = FromOriginal(cc, "Content", s, simplified);
            if (cc is HeaderedContentControl hcc && hcc.Header is string h && !string.IsNullOrEmpty(h))
                hcc.Header = FromOriginal(hcc, "Header", h, simplified);
        }
        if (d is FrameworkElement fe && fe.ToolTip is string tip && !string.IsNullOrEmpty(tip))
            fe.ToolTip = FromOriginal(fe, "ToolTip", tip, simplified);
    }

    /// <summary>
    /// 頁面顯示時呼叫：把該頁轉為目前語言（含繁體＝還原原文）。冪等，可安全重複呼叫。
    /// </summary>
    public static void ConvertPage(UserControl page) => ConvertVisualTree(page, _simplified);
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
