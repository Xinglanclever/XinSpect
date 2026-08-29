using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

public enum AppTheme { Dark, Light }

/// <summary>
/// 強調色定義：Main 為主色（漸層頂近似），Dim 為暗端（漸層底），GradTop 為漸層頂亮端。
/// </summary>
public sealed record AccentPreset(string Key, string Name, string Main, string Dim, string GradTop)
{
    public Color MainColor => (Color)ColorConverter.ConvertFromString(Main);
    public Color DimColor => (Color)ColorConverter.ConvertFromString(Dim);
    public Color GradTopColor => (Color)ColorConverter.ConvertFromString(GradTop);

    /// <summary>供設定頁色票預覽用（純色）。</summary>
    public Brush Swatch
    {
        get
        {
            var b = new SolidColorBrush(MainColor);
            b.Freeze();
            return b;
        }
    }
}

/// <summary>
/// 外觀套用引擎：深／淺色主題 ＋ 八色強調色，執行期即時切換並持久化。
/// 核心手法：Theme.xaml 內的 SolidColorBrush / LinearGradientBrush 皆未凍結、以 {StaticResource} 取用
/// （解析為同一筆刷物件）；改寫其 .Color / 漸層停駐點即可讓全部已載入的視覺樹同步換色，
/// 不需 DynamicResource、不需重建視窗。
/// </summary>
public static class ThemeService
{
    /// <summary>主題／強調色變更後觸發（外殼與各頁據此重繪自繪元件）。</summary>
    public static event Action? Changed;

    /// <summary>可選強調色（設定頁色票順序）。宣告於最前，因靜態欄位初始化依文字順序執行。</summary>
    public static IReadOnlyList<AccentPreset> Presets { get; } =
    [
        new("blue",   "曦藍", "#3987e5", "#1c5cab", "#4c96f0"),
        new("teal",   "青碧", "#17a2a2", "#0d6b6b", "#22b8b8"),
        new("green",  "翠綠", "#3ba55d", "#22693a", "#4bbb6e"),
        new("amber",  "琥珀", "#e0932a", "#9c6111", "#f0a83f"),
        new("crimson","絳紅", "#d9455a", "#94212f", "#e85e72"),
        new("violet", "紫霞", "#8a63d2", "#563a8c", "#a17ce8"),
        new("magenta","洋紅", "#cf4aa8", "#8c2470", "#e263bd"),
        new("slate",  "石墨", "#7d8996", "#4b5560", "#93a0ad"),
    ];

    private static AppTheme _theme = AppTheme.Dark;
    /// <summary>目前主題。設定值會立即套用並持久化。</summary>
    public static AppTheme Theme
    {
        get => _theme;
        set
        {
            if (_theme == value) return;
            _theme = value;
            ApplyAll();
            SavePrefs();
            Changed?.Invoke();
        }
    }

    private static AccentPreset _accent = Presets[0];
    /// <summary>目前強調色。TrayService 圖示與標題輝光取用其色值。</summary>
    public static AccentPreset Accent
    {
        get => _accent;
        set
        {
            if (value is null || _accent.Key == value.Key) return;
            _accent = value;
            ApplyAll();
            SavePrefs();
            Changed?.Invoke();
        }
    }

    /// <summary>依鍵值取強調色；找不到回傳預設（曦藍）。</summary>
    public static AccentPreset FindAccent(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
            foreach (var p in Presets)
                if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) return p;
        return Presets[0];
    }

    /// <summary>目前強調色在 <see cref="Presets"/> 中的索引（供設定頁下拉選單使用）。</summary>
    public static int AccentIndex
    {
        get
        {
            for (int i = 0; i < Presets.Count; i++)
                if (Presets[i].Key == _accent.Key) return i;
            return 0;
        }
        set { if (value >= 0 && value < Presets.Count) Accent = Presets[value]; }
    }

    /// <summary>目前主題的索引（0=深色、1=淺色；供設定頁下拉選單使用）。</summary>
    public static int ThemeIndex
    {
        get => _theme == AppTheme.Dark ? 0 : 1;
        set => Theme = value == 1 ? AppTheme.Light : AppTheme.Dark;
    }

    /// <summary>主題名稱（設定頁下拉選單項目）。</summary>
    public static IReadOnlyList<string> ThemeNames { get; } = ["深色", "淺色"];

    /// <summary>在深／淺色間切換（命令面板用）。</summary>
    public static void ToggleTheme() => Theme = _theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;

    // ---- 面板配色 --------------------------------------------------------
    private sealed record Palette(
        string PagePlane, string Surface, string Surface2,
        string PrimaryInk, string SecondaryInk, string MutedInk,
        string Hairline, string Baseline, string HeaderMid);

    private static readonly Palette DarkPalette = new(
        "#0d0d0d", "#1a1a19", "#232322",
        "#ffffff", "#c3c2b7", "#898781",
        "#2c2c2a", "#383835", "#181819");

    // 淺色：以暖白紙面為底，墨色維持與深色相同的三級對比階梯（主/次/弱）
    private static readonly Palette LightPalette = new(
        "#f2f2ef", "#ffffff", "#eceae5",
        "#17171a", "#4a4a45", "#7c7a74",
        "#dedcd6", "#c9c7c0", "#f7f6f3");

    private static Palette Current => _theme == AppTheme.Dark ? DarkPalette : LightPalette;

    // ---- 初始化 ----------------------------------------------------------

    /// <summary>讀取已存偏好後套用（於解析任何 XAML 前呼叫，避免首格閃色）。</summary>
    public static void Initialize()
    {
        LoadPrefs();
        ApplyAll();
    }

    public static void ApplyAll()
    {
        var app = Application.Current;
        if (app is null) return;

        var p = Current;

        Set("PagePlaneBrush", p.PagePlane);
        Set("SurfaceBrush", p.Surface);
        Set("Surface2Brush", p.Surface2);
        Set("PrimaryInkBrush", p.PrimaryInk);
        Set("SecondaryInkBrush", p.SecondaryInk);
        Set("MutedInkBrush", p.MutedInk);
        Set("HairlineBrush", p.Hairline);
        Set("BaselineBrush", p.Baseline);

        SetColor("PagePlaneColor", p.PagePlane);
        SetColor("SurfaceColor", p.Surface);
        SetColor("Surface2Color", p.Surface2);

        // 強調色
        Set("AccentBrush", Accent.Main);
        Set("AccentDimBrush", Accent.Dim);
        SetColor("AccentColor", Accent.Main);

        // 強調漸層：頂=亮端、底=暗端
        SetGradient("AccentGradientBrush", Accent.GradTopColor, Accent.DimColor);

        // 標題列漸層：以強調色調染頁面底色（深色偏暗、淺色偏亮）
        Color plane = Hex(p.PagePlane);
        Color surface = Hex(p.Surface);
        Color tint = Blend(Accent.MainColor, plane, _theme == AppTheme.Dark ? 0.80 : 0.86);
        SetGradientStops("HeaderGradientBrush",
            (0.0, tint),
            (0.45, Hex(p.HeaderMid)),
            (1.0, surface));

        // 狀態色：淺色主題下原深色版飽和度偏亮，壓暗以維持與紙面的對比
        if (_theme == AppTheme.Dark)
        {
            Set("GoodBrush", "#0ca30c");
            Set("WarningBrush", "#fab219");
            Set("SeriousBrush", "#ec835a");
            Set("CriticalBrush", "#d03b3b");
        }
        else
        {
            Set("GoodBrush", "#0a7a0a");
            Set("WarningBrush", "#b57c05");
            Set("SeriousBrush", "#c85a2c");
            Set("CriticalBrush", "#b52626");
        }
    }

    // ---- 偏好持久化（獨立小檔，早於 SettingsService 建立即可讀取）--------

    private static readonly string PrefDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect");
    private static readonly string PrefPath = Path.Combine(PrefDir, "appearance.json");

    private sealed class Prefs
    {
        public string? Theme { get; set; }
        public string? Accent { get; set; }
    }

    private static void LoadPrefs()
    {
        try
        {
            if (!File.Exists(PrefPath)) return;
            var p = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PrefPath));
            if (p is null) return;
            _theme = string.Equals(p.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark;
            _accent = FindAccent(p.Accent);
        }
        catch { /* 偏好毀損則沿用預設（深色 + 曦藍） */ }
    }

    private static void SavePrefs()
    {
        try
        {
            Directory.CreateDirectory(PrefDir);
            var json = JsonSerializer.Serialize(
                new Prefs { Theme = _theme.ToString(), Accent = _accent.Key },
                new JsonSerializerOptions { WriteIndented = true });
            AtomicWrite.AllText(PrefPath, json);
        }
        catch { /* 存檔失敗不影響本次執行期外觀 */ }
    }

    // ---- 資源改寫工具 ----------------------------------------------------

    private static void Set(string key, string hex)
    {
        if (Application.Current.Resources[key] is SolidColorBrush b && !b.IsFrozen)
            b.Color = Hex(hex);
    }

    private static void SetColor(string key, string hex)
    {
        if (Application.Current.Resources.Contains(key))
            Application.Current.Resources[key] = Hex(hex);
    }

    private static void SetGradient(string key, Color top, Color bottom)
        => SetGradientStops(key, (0.0, top), (1.0, bottom));

    private static void SetGradientStops(string key, params (double off, Color col)[] stops)
    {
        if (Application.Current.Resources[key] is not LinearGradientBrush g || g.IsFrozen) return;
        for (int i = 0; i < stops.Length && i < g.GradientStops.Count; i++)
            g.GradientStops[i].Color = stops[i].col;
    }

    private static Color Hex(string h) => (Color)ColorConverter.ConvertFromString(h);

    /// <summary>a、b 依 t 混色（t=0→全 a，t=1→全 b）。</summary>
    private static Color Blend(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }
}
