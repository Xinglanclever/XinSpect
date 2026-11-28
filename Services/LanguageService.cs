using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OpenccNetLib;

namespace XinSpect;

/// <summary>
/// 全域語言服務：繁體↔簡體中文。桌面與 CLI 共用 OpenCC 詞組轉換及產品術語覆寫。
/// </summary>
/// <remarks>
/// <para>
/// 轉換策略分三層：
/// ① 硬編碼在 XAML 裡的文字 → 頁面載入時由 <see cref="ConvertVisualTree"/> 遍歷轉換。
/// ② Binding 與稍後變動的顯示文字 → WpfLocalizationBridge 保留原繫結並監看顯示屬性。
/// ③ 程式碼裡產生的文字（HelpCatalog、Changelog、狀態列）→ 在產出點呼叫 <see cref="T"/>。
/// </para>
/// <para>
/// 呼叫 <see cref="SetLanguage"/> 會立即轉換整棵視覺樹、觸發 <see cref="Changed"/> 事件
/// 並持久化到 SettingsService。
/// </para>
/// </remarks>
public static class LanguageService
{
    /// <summary>明確標記產品顯示文案；唯讀 TextBox 須選用此標記，原始紀錄／使用者內容維持預設停用。</summary>
    public static readonly DependencyProperty IsUiTextProperty = DependencyProperty.RegisterAttached(
        "IsUiText", typeof(bool), typeof(LanguageService), new FrameworkPropertyMetadata(false));
    public static void SetIsUiText(DependencyObject target, bool value) => target.SetValue(IsUiTextProperty, value);
    public static bool GetIsUiText(DependencyObject target) => (bool)target.GetValue(IsUiTextProperty);

    /// <summary>原始證據、硬體識別或已逐段組合的字串；此標記連同後代一律不做整串翻譯。</summary>
    public static readonly DependencyProperty IsRawTextProperty = DependencyProperty.RegisterAttached(
        "IsRawText", typeof(bool), typeof(LanguageService),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static void SetIsRawText(DependencyObject target, bool value) => target.SetValue(IsRawTextProperty, value);
    public static bool GetIsRawText(DependencyObject target) => (bool)target.GetValue(IsRawTextProperty);

    private static readonly Lazy<Opencc> TraditionalToSimplified = new(
        () => new Opencc(OpenccConfig.Tw2Sp, isFrozen: true),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Opencc> SimplifiedToTraditional = new(
        () => new Opencc(OpenccConfig.S2Twp, isFrozen: true),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Encoding> Big5Encoding = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static bool _simplified;
    private static bool _isEnglish;
    private static bool _catLanguageSelected;

    /// <summary>目前是否為簡體模式。</summary>
    public static bool IsSimplified => _simplified;

    /// <summary>目前是否為英語模式。</summary>
    public static bool IsEnglish => _isEnglish;

    /// <summary>貓貓語喵已選取，與外觀主題無關。</summary>
    public static bool IsCatLanguage => _catLanguageSelected;

    /// <summary>目前語言。</summary>
    public static AppLanguage Language => IsCatLanguage ? AppLanguage.Cat
        : _isEnglish ? AppLanguage.English
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
    internal static void SetEnglishForTests(bool english)
    {
        _isEnglish = english;
        _catLanguageSelected = false;
    }

    /// <summary>測試用：直接切簡體旗標（同上；英語優先，故同時把英語關掉）。</summary>
    internal static void SetSimplifiedForTests(bool simplified)
    {
        _simplified = simplified;
        _catLanguageSelected = false;
        if (simplified) _isEnglish = false;
    }

    public static void Initialize(SettingsService settings)
        => Initialize(settings.SimplifiedChinese, settings.IsEnglish, settings.CatLanguage);

    /// <summary>啟動前只載入語言旗標；不建構設定服務、不寫設定、不初始化硬體後端。</summary>
    internal static void Initialize(bool simplifiedChinese, bool isEnglish, bool catLanguage)
    {
        _simplified = simplifiedChinese;
        _isEnglish = isEnglish;
        _catLanguageSelected = catLanguage;
        WpfLocalizationBridge.Initialize();
    }

    /// <summary>啟用或停用貓貓語喵時保留既有繁／簡／英語偏好。</summary>
    public static void ApplyCatLanguage(bool enabled, SettingsService settings)
    {
        _catLanguageSelected = enabled;
        settings.CatLanguage = enabled;
        RefreshPresentation();
        Changed?.Invoke();
        NotifyLanguageDependentLists?.Invoke();
    }

    /// <summary>切換語言（三語版）。英文字串未收錄者回退繁中原文。</summary>
    public static void SetLanguage(AppLanguage lang, SettingsService settings)
    {
        if (lang == AppLanguage.Cat)
        {
            _catLanguageSelected = true;
            settings.CatLanguage = true;
        }
        else
        {
            _catLanguageSelected = false;
            settings.CatLanguage = false;
            _isEnglish = lang == AppLanguage.English;
            _simplified = lang == AppLanguage.Simplified && !_isEnglish;
        }
        settings.SimplifiedChinese = _simplified;
        settings.IsEnglish = _isEnglish;
        RefreshPresentation();
        Changed?.Invoke();
    }

    /// <summary>
    /// 切換語言並立即套用到整棵視覺樹。
    /// </summary>
    public static void SetLanguage(bool simplified, SettingsService settings)
        => SetLanguage(simplified ? AppLanguage.Simplified : AppLanguage.Traditional, settings);

    private static void RefreshPresentation()
    {
        WpfLocalizationBridge.RefreshAll();
        if (Shell.Main is { } main && main.Dispatcher.CheckAccess()) main.RebuildNavIfNeeded();
    }

    /// <summary>
    /// 核心轉換函式：把一段文字轉為目前語言。
    /// 全專案統一透過此函式轉換，不直接呼叫 Windows API。
    /// </summary>
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return Catify(text, TranslateBase(text));
    }

    /// <summary>翻譯短標籤但不加貓貓語句尾，供主題名稱等產品識別文字使用。</summary>
    internal static string TLabel(string? text) => TranslateBase(text);

    private static string TranslateBase(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (_isEnglish) return UiTextTranslation.ToEnglish(text);
        // 若輸入是英語模式的產物，先反查繁中再轉，切換語系可往返。
        if (EnglishStrings.Reverse(text) is { } zhOriginal) text = zhOriginal;
        else if (UiTextTranslation.OriginalFromEnglishTemplate(text) is { } templateOriginal) text = templateOriginal;
        return UiTextTranslation.TranslateTemplate(text, _simplified) ?? (_simplified ? ToSimplified(text) : text);
    }

    internal static string Catify(string source, string localized)
    {
        if (!IsCatLanguage || !HasChineseProse(source)) return localized;

        // 逐段加語氣詞；純數字、單位與 ASCII 識別值不含中文 UI 原文，因此保持原樣。
        string[] sourceLines = source.Split('\n');
        string[] localizedLines = localized.Split('\n');
        for (int i = 0; i < localizedLines.Length; i++)
        {
            string sourceLine = sourceLines[Math.Min(i, sourceLines.Length - 1)];
            if (!HasChineseProse(sourceLine) || !HasDisplayWords(localizedLines[i])) continue;
            string line = localizedLines[i];
            int end = line.Length;
            while (end > 0 && char.IsWhiteSpace(line[end - 1])) end--;
            if (end == 0 || line[..end].EndsWith("喵", StringComparison.Ordinal)) continue;
            localizedLines[i] = line.Insert(end, "喵");
        }
        return string.Join("\n", localizedLines);
    }

    private static bool HasDisplayWords(string text) => text.Any(char.IsLetter);

    private static bool HasChineseProse(string source)
        => source.Any(c => c is >= '㐀' and <= '鿿' or >= '豈' and <= '﫿')
           || EnglishStrings.Reverse(source) is not null;

    private static string RemoveCatEnding(string text)
    {
        if (text is "貓貓語喵" or "猫猫语喵") return text;
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int end = lines[i].Length;
            while (end > 0 && char.IsWhiteSpace(lines[i][end - 1])) end--;
            if (end > 0 && lines[i][..end].EndsWith("喵", StringComparison.Ordinal))
                lines[i] = lines[i].Remove(end - 1, 1);
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 複合字串的翻譯：狀態列是「就緒 ・ 每秒更新中 ・ 深度規格已讀取 ・ 啟動耗時 2.6 秒」
    /// 這樣拼出來的，整串查表一定查不到。這裡按「 ・ 」切開逐段查，段尾帶數字時
    /// （「啟動耗時 2.6 秒」）取最長前綴查表、數字與單位原樣接回。
    /// 簡體模式不需要這套——共用轉換服務可以直接處理整串。
    /// </summary>
    public static string TComposite(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!_isEnglish) return Catify(text, TranslateBase(text));
        if (UiTextTranslation.ToEnglish(text) is { } translated && translated != text) return Catify(text, translated);
        if (EnglishStrings.Lookup(text) is { } whole) return Catify(text, whole);

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
        return Catify(text, string.Join(" ・ ", parts));
    }

    /// <summary>繁體→簡體：先套用產品硬體詞，再以 OpenCC 台灣繁體詞組配置轉換。</summary>
    public static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        text = ZhTerms.ApplyToSimp(text);
        return TraditionalToSimplified.Value.Convert(text);
    }

    /// <summary>只在原始位元組邊界嚴格解碼 Big5；無效序列會丟例外，不靜默變成亂碼。</summary>
    public static string DecodeBig5(ReadOnlySpan<byte> bytes) => Big5Encoding.Value.GetString(bytes);

    /// <summary>Big5 位元組 → Unicode → OpenCC／產品詞庫 → 簡體 Unicode。</summary>
    public static string Big5ToSimplified(ReadOnlySpan<byte> bytes) => ToSimplified(DecodeBig5(bytes));

    /// <summary>簡體→繁體：先套用產品詞組，再用 OpenCC 簡體至台灣繁體配置。</summary>
    public static string ToTraditional(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        text = ZhTerms.ApplyToTrad(text);
        return SimplifiedToTraditional.Value.Convert(text);
    }

    private sealed class TextOrigin(string original)
    {
        public string Original = original;
        public string LastRendered = original;
    }

    // 原文與最近一次輸出分開保存；程式稍後重新指定文字時，不能還原成第一次載入的舊值。
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, TextOrigin>> Origins = new();

    internal static string FromOriginal(DependencyObject target, string slot, string current, bool simplified,
        bool allowEnglishReverse = true, bool knownOnly = false)
    {
        var map = Origins.GetOrCreateValue(target);
        if (!map.TryGetValue(slot, out var origin))
            map[slot] = origin = new TextOrigin(knownOnly ? current : Normalize(current, allowEnglishReverse));
        else if (current != origin.LastRendered && current != origin.Original)
            origin.Original = knownOnly ? current : Normalize(current, allowEnglishReverse);

        string original = origin.Original;
        string localized = knownOnly ? UiTextTranslation.KnownDisplayText(original, _isEnglish, simplified)
            : _isEnglish ? UiTextTranslation.ToEnglish(original)
            : UiTextTranslation.TranslateTemplate(original, simplified)
              ?? (simplified ? ToSimplified(original) : original);
        return origin.LastRendered = knownOnly ? localized : Catify(original, localized);
    }

    private static string Normalize(string text, bool allowEnglishReverse)
    {
        text = RemoveCatEnding(text);
        if (allowEnglishReverse && EnglishStrings.Reverse(text) is { } original) return original;
        if (allowEnglishReverse && UiTextTranslation.OriginalFromEnglishTemplate(text) is { } templateOriginal)
            return templateOriginal;
        if (UiTextTranslation.ContainsChinese(text) && EnglishStrings.Lookup(text) is null)
        {
            string traditional = ToTraditional(text);
            if (EnglishStrings.Lookup(traditional) is not null) return traditional;
        }
        return text;
    }

    /// <summary>轉換顯示樹並持續追蹤後續變動；保留 Binding、converter、StringFormat 與行內格式。</summary>
    public static void ConvertVisualTree(DependencyObject root, bool simplified)
        => WpfLocalizationBridge.ConvertTree(root, simplified);

    /// <summary>快取頁面及延後載入頁面共用同一條語言生命週期。</summary>
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
        // 英文裝置名稱（例如 Memory）、路徑不是繁中 UI 原文，不能用 Reverse 當成翻譯來源。
        => value is string s && UiTextTranslation.ContainsChinese(s) && !WpfLocalizationBridge.LooksLikePath(s)
            ? LanguageService.T(s) : value ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
