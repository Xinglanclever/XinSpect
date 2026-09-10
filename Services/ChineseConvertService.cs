using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 繁體↔簡體中文轉換。使用 Windows 內建的 LCMapStringEx（核心層級，不依賴外部字典）。
/// </summary>
public static class ChineseConvertService
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

    /// <summary>繁體→簡體。</summary>
    public static string ToSimplified(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        int len = LCMapStringEx(LOCALE_NAME_ZH, LCMAP_SIMPLIFIED_CHINESE, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (len <= 0) return text;
        var buf = new char[len];
        LCMapStringEx(LOCALE_NAME_ZH, LCMAP_SIMPLIFIED_CHINESE, text, text.Length, buf, len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new string(buf);
    }

    /// <summary>簡體→繁體。</summary>
    public static string ToTraditional(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        int len = LCMapStringEx(LOCALE_NAME_ZH, LCMAP_TRADITIONAL_CHINESE, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (len <= 0) return text;
        var buf = new char[len];
        LCMapStringEx(LOCALE_NAME_ZH, LCMAP_TRADITIONAL_CHINESE, text, text.Length, buf, len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new string(buf);
    }

    /// <summary>依目前設定轉換。</summary>
    public static string Convert(string? text, bool simplified)
        => simplified ? ToSimplified(text) : text ?? "";

    /// <summary>
    /// 遍歷 WPF 視覺樹，把所有 TextBlock 和 TextBox 的文字轉換。
    /// 在主題切換或語言切換時呼叫一次即可。
    /// </summary>
    public static void ConvertVisualTree(System.Windows.DependencyObject root, bool simplified)
    {
        if (root is null) return;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            switch (child)
            {
                case System.Windows.Controls.TextBlock tb when tb.GetBindingExpression(System.Windows.Controls.TextBlock.TextProperty) is null:
                    // 只轉換沒有繫結的硬編碼文字（有繫結的由來源資料自行處理）
                    tb.Text = simplified ? ToSimplified(tb.Text) : ToTraditional(tb.Text);
                    break;
                case System.Windows.Controls.HeaderedContentControl hcc when hcc.Header is string h:
                    hcc.Header = simplified ? ToSimplified(h) : ToTraditional(h);
                    break;
            }
            ConvertVisualTree(child, simplified);
        }
    }
}
