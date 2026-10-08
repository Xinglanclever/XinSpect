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
    private static bool _isEnglish;

    /// <summary>目前是否為簡體模式。</summary>
    public static bool IsSimplified => _simplified;

    /// <summary>目前是否為英語模式。</summary>
    public static bool IsEnglish => _isEnglish;

    /// <summary>目前語言。</summary>
    public static AppLanguage Language => _isEnglish ? AppLanguage.English
        : _simplified ? AppLanguage.Simplified : AppLanguage.Traditional;

    /// <summary>語言切換時觸發。訂閱者應重新產生動態文字。</summary>
    public static event Action? Changed;

    /// <summary>
    /// 初始化：從 SettingsService 讀取已存偏好。在 MainWindow 建構前呼叫。
    /// </summary>
    /// <summary>
    /// 設定頁的唯一切換入口：三個互斥模式（繁中／簡中／英語）。<b>一律以引數決定狀態，不讀控制項</b>——
    /// 舊版從 CheckBox.IsChecked 讀，事件順序一變就切到舊值（使用者回報的「卡住原來的」）。
    /// 收尾時把「語言相依的動態清單」（紀年名稱等）一起通知，否則下拉選單會停在舊語言。
    /// </summary>
    public static void Apply(AppLanguage lang, SettingsService settings)
    {
        SetLanguage(lang, settings);
        NotifyLanguageDependentLists?.Invoke();
    }

    /// <summary>語言相依的動態清單重算（紀年名稱…）。由 MainViewModel 在開機時掛上。</summary>
    public static Action? NotifyLanguageDependentLists;

    /// <summary>測試用：直接切英語旗標（InternalsVisibleTo；不走 SetLanguage 的 Shell／設定路徑）。</summary>
    internal static void SetEnglishForTests(bool english) => _isEnglish = english;

    /// <summary>測試用：直接切簡體旗標（同上；英語優先，故同時把英語關掉）。</summary>
    internal static void SetSimplifiedForTests(bool simplified)
    {
        _simplified = simplified;
        if (simplified) _isEnglish = false;
    }

    public static void Initialize(SettingsService settings)
    {
        _simplified = settings.SimplifiedChinese;
        _isEnglish = settings.IsEnglish;
    }

    /// <summary>切換語言（三語版）。英文字串未收錄者回退繁中原文。</summary>
    public static void SetLanguage(AppLanguage lang, SettingsService settings)
    {
        _isEnglish = lang == AppLanguage.English;
        _simplified = lang == AppLanguage.Simplified && !_isEnglish;
        settings.SimplifiedChinese = _simplified;
        settings.IsEnglish = _isEnglish;
        if (Shell.Main is { } main)
        {
            // 兩種模式都走同一棵樹：簡體由 LCMapStringEx 轉，英語由 FromOriginal 的翻譯表分支轉。
            ConvertVisualTree(main, _simplified);
            main.RebuildNavIfNeeded();
        }
        Changed?.Invoke();
    }

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
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (_isEnglish) return EnglishStrings.Lookup(text) ?? text;
        if (!_simplified) return text;
        // 這裡吃到的 text 若是英語模式的產物（T() 寫進控制項又被讀回來的），先反查回繁中再轉簡體。
        // 沒有這步，切到簡體後這批字串原樣卡英文。
        if (EnglishStrings.Reverse(text) is { } zhOriginal) text = zhOriginal;
        return ToSimplified(text);
    }

    /// <summary>
    /// 複合字串的翻譯：狀態列是「就緒 ・ 每秒更新中 ・ 深度規格已讀取 ・ 啟動耗時 2.6 秒」
    /// 這樣拼出來的，整串查表一定查不到。這裡按「 ・ 」切開逐段查，段尾帶數字時
    /// （「啟動耗時 2.6 秒」）取最長前綴查表、數字與單位原樣接回。
    /// 簡體模式不需要這套——LCMapStringEx 逐字轉換本來就吃得下整串。
    /// </summary>
    public static string TComposite(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!_isEnglish) return T(text);
        if (EnglishStrings.Lookup(text) is { } whole) return whole;

        var parts = text.Split(" ・ ");
        for (int i = 0; i < parts.Length; i++)
        {
            if (EnglishStrings.Lookup(parts[i]) is { } exact) { parts[i] = exact; continue; }
            // 「啟動耗時 2.6 秒」→ 前綴「啟動耗時」＋尾碼「2.6 秒」；尾碼逐詞查（「秒」→「s」）
            int cut = parts[i].IndexOf(' ');
            while (cut > 0)
            {
                if (EnglishStrings.Lookup(parts[i][..cut]) is { } head)
                {
                    var tail = parts[i][(cut + 1)..].Split(' ')
                        .Select(w => EnglishStrings.Lookup(w) ?? w);
                    parts[i] = head + " " + string.Join(" ", tail);
                    break;
                }
                cut = parts[i].LastIndexOf(' ', cut - 1);
            }
        }
        return string.Join(" ・ ", parts);
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
        // 存槽正規化（無條件）：若 current 是英語模式的產物（翻譯表的值），先反查回繁中鍵再存。
        // 場景：英語模式下 T() 寫進控制項的英文字串 → 之後切簡體才對這棵樹跑轉換——
        // 此時 _isEnglish 已是 false，若只在英語分支做正規化就漏掉這條路，
        // 英文字串被當「原文」釘在槽裡，簡體轉換原樣放行——使用者看到的「簡體模式夾英文」。
        // 正規化必須在讀槽「之前」對 current 做：鍵值零碰撞（有測試釘住）故不會誤傷繁中鍵。
        if (EnglishStrings.Reverse(current) is { } zhKey) current = zhKey;
        var map = _orig.GetOrCreateValue(o);
        if (!map.TryGetValue(slot, out var original)) { original = current; map[slot] = current; }
        // 英語模式優先：未收錄回繁中原文。永遠從原文出發，繁／簡／英三方往返可逆、冪等。
        if (_isEnglish) return EnglishStrings.Lookup(original) ?? original;
        return simplified ? ToSimplified(original) : original;
    }

    /// <summary>
    /// 轉換自繪控制項的字串相依屬性（FieldRow.Label、RadialGauge.Caption…）。
    /// 這類屬性不在 <see cref="TextBlock"/> 上，視覺樹遍歷碰不到；
    /// 但它們通常以 RelativeSource 繫結到控制項自己的內部 TextBlock，所以改 DP 畫面就會跟著變。
    /// <b>DP 本身是繫結來的就跳過</b>——直接寫入會把繫結覆蓋掉，來源之後再變也不會更新。
    /// </summary>
    private static void TranslateStringDp(DependencyObject d, DependencyProperty prop, string slot, bool simplified)
    {
        if (d.GetValue(prop) is not string s || s.Length == 0) return;
        if (BindingOperations.IsDataBound(d, prop)) return;
        string converted = FromOriginal(d, slot, s, simplified);
        if (!ReferenceEquals(converted, s) && converted != s) d.SetValue(prop, converted);
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
        // Popup（ComboBox 下拉、ContextMenu、ToolTip 的宿主）不是視覺子節點，內容要自己走一遍。
        if (d is System.Windows.Controls.Primitives.Popup { Child: { } popupChild })
            ConvertVisualTree(popupChild, simplified);

        // 自繪控制項的文字是「自訂相依屬性」，不是 TextBlock.Text——遍歷看不到，翻譯表也永遠碰不到。
        // 這些屬性大多以 RelativeSource 繫結到自己的內部 TextBlock，所以改 DP 就等於改畫面。
        // 兩件事必須顧到：
        // ① 一定要先確認控制項型別。相依屬性的預設值是「整型別共用」的——對任何物件呼叫
        //    GetValue(AnalogVoltMeter.CaptionProperty) 都會拿到預設字串「電壓錶」，
        //    不擋型別就會把譯文寫到無關的節點上。
        // ② DP 本身是繫結來的就跳過（例如 Label="{Binding Label}"），寫入會覆蓋繫結。
        // 只收「版面標籤」性質的字串：FieldRow.Value 這類承載資料的屬性刻意不碰。
        switch (d)
        {
            case FieldRow:
                TranslateStringDp(d, FieldRow.LabelProperty, "Label", simplified);
                break;
            case RadialGauge:
                TranslateStringDp(d, RadialGauge.CaptionProperty, "Caption", simplified);
                TranslateStringDp(d, RadialGauge.UnitProperty, "Unit", simplified);
                break;
            case HistoryGraph:
                TranslateStringDp(d, HistoryGraph.CaptionProperty, "Caption", simplified);
                break;
            case Oscilloscope:
                TranslateStringDp(d, Oscilloscope.CaptionProperty, "Caption", simplified);
                break;
            case AnalogVoltMeter:
                TranslateStringDp(d, AnalogVoltMeter.CaptionProperty, "Caption", simplified);
                break;
            case DonutChart:
                TranslateStringDp(d, DonutChart.CaptionProperty, "Caption", simplified);
                TranslateStringDp(d, DonutChart.Caption2Property, "Caption2", simplified);
                TranslateStringDp(d, DonutChart.SubTextProperty, "SubText", simplified);
                break;
        }

        if (d is SectionHead sh)
        {
            // SectionHead 的標題 TextBlock 是內部繫結，下面的 TextBlock 分支會因「有繫結」而跳過；
            // 在這裡直接轉外露的 Text 屬性，繫結會把結果帶到內部。
            TranslateStringDp(sh, SectionHead.TextProperty, "Text", simplified);
        }

        if (d is TextBlock tb)
        {
            // 有繫結的 TextBlock 一律不碰。兩個理由：
            // ① 它的文字來自 ViewModel，該由 ChineseConverter／LangFormatConverter 翻譯；
            // ② 更關鍵的是——WPF 會把「繫結來的文字」表示成一個隱式 Run，所以 Inlines.Count > 0
            //    對有繫結的 TextBlock 也成立。舊碼因此走進 Inlines 分支、直接寫 r.Text，
            //    這會把繫結覆蓋掉：文字定在翻譯當下的那一版，之後來源再變（時鐘每一拍、
            //    狀態列、即時讀值）都不會更新。1015 處 Text="{Binding …}" 全中，且繁簡英三模式
            //    都發生（繁是「原文＝譯文」看不出來，簡英最明顯）。2026-10-08 修。
            if (tb.GetBindingExpression(TextBlock.TextProperty) is null)
            {
                if (tb.Inlines.Count > 0)
                {
                    // 先快照成清單再改：改 Run.Text 會觸發 InlineCollection 變更，邊列舉邊改會丟例外。
                    foreach (var r in tb.Inlines.OfType<Run>().ToList())
                        if (r.GetBindingExpression(Run.TextProperty) is null && !string.IsNullOrEmpty(r.Text))
                            r.Text = FromOriginal(r, "Text", r.Text, simplified);
                }
                else if (!string.IsNullOrEmpty(tb.Text))
                {
                    tb.Text = FromOriginal(tb, "Text", tb.Text, simplified);
                }
            }
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

        // 下拉選單／右鍵選單的項目活在 Popup 裡，不是主視窗的視覺子節點——不特別處理就會永遠停在原文。
        // ComboBoxItem 本身就是 ContentControl，直接轉它的 Content 即可（不必展開下拉）。
        if (d is ItemsControl items && items.Items.Count > 0)
        {
            foreach (var item in items.Items)
                if (item is DependencyObject child) ConvertNode(child, simplified);
        }
        if (d is ContextMenu menu)
            foreach (var item in menu.Items)
                if (item is DependencyObject child) ConvertNode(child, simplified);
        // ToolTip 物件形式（非字串）同樣在 Popup 裡
        if (d is FrameworkElement fe2 && fe2.ToolTip is DependencyObject tipObj)
            ConvertNode(tipObj, simplified);
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
