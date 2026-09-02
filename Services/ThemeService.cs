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
/// <para>
/// 手法：Theme.xaml 宣告全部佈景筆刷的初值，XAML 一律以 <c>{DynamicResource}</c> 取用；
/// 換色時把資源項目<b>整支換掉</b>，WPF 便通知所有動態引用重新解析，
/// 已經在畫面上的元素同步換色，不必重建視窗。
/// </para>
/// <para>
/// ⚠ 不要改回「就地改寫筆刷的 .Color ＋ {StaticResource}」：放進資源字典的 <see cref="Freezable"/>
/// 會被 WPF 自動凍結（Application 的資源可跨執行緒查找），凍結後改不動，整套換色會<b>靜靜落空</b>
/// ——不拋例外、不留繫結錯誤。1.8.0 之前正是如此，淺色主題與另外七個強調色從加進來的那天起
/// 就沒有生效過（深色＋曦藍剛好等於 Theme.xaml 的字面值，所以看起來像是正常的）。
/// 守這件事的是 <c>Tests/ThemeSwitchTests.cs</c>：它會實際重繪並比對像素。
/// </para>
/// <para>
/// 程式碼繪製的圖表另有一條路：<see cref="VizPalette"/> 每次取色都重新查資源，
/// 但已經畫上去的內容不會自己更新，故自繪控制項須訂閱 <see cref="Changed"/> 重畫
/// （見 <c>Controls/ThemeAware.cs</c>）。
/// </para>
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

        // 強調色
        Set("AccentBrush", Accent.Main);
        Set("AccentDimBrush", Accent.Dim);

        // 按鈕底色與其上的文字色：白字要在整道漸層上都達到 WCAG AA 的 4.5:1，
        // 做法是把按鈕自己的底色整組壓暗（只動亮度、不動色相），而不是換掉字色——
        // 換字色只會把問題從漸層的一端搬到另一端。見 Services/AccentInk.cs 與 AccentInkTests。
        var btn = AccentInk.ButtonColors(Accent.GradTopColor, Accent.DimColor, Accent.MainColor);
        SetBrush("AccentInkBrush", btn.Ink);
        SetBrush("AccentButtonBrush", btn.Solid);

        // 強調漸層（按鈕底）：頂=亮端、底=暗端，兩端都已壓到白字達標
        SetGradient("AccentGradientBrush", btn.Top, btn.Bottom);

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
    //
    // 手法是「換掉資源項目」，不是「就地改筆刷的色」。原因見類別註解：放進資源字典的
    // Freezable 一律被 WPF 凍結（Application 的資源可跨執行緒查找），改不動。
    // 換掉項目會通知所有 {DynamicResource} 重新解析，已在畫面上的元素因此同步換色。

    /// <summary>換掉一支純色筆刷資源，並同步供程式碼取用的「活的」同名筆刷。</summary>
    private static void Set(string key, string hex)
    {
        var c = Hex(hex);

        var b = new SolidColorBrush(c);
        b.Freeze();   // 凍結的筆刷可跨執行緒共用，繪製也少一層變更通知
        Application.Current.Resources[key] = b;

        SetLive(key, c);
    }

    // ---- 供程式碼繪製與轉換器取用的「活的」筆刷 ---------------------------
    //
    // 自繪元件與 IValueConverter 拿到的是筆刷「物件」，不是動態資源引用：換掉資源項目的通知
    // 傳不到它們身上（已經畫進視覺內容的筆刷、已經求值完的繫結都不會再問一次）。
    // 但凍結只發生在「放進資源字典」這一步——存在靜態欄位裡的筆刷不會被凍結，
    // 於是就地改它的 .Color 就能讓已經畫上去的內容與已求值的繫結一起換色。
    // 兩條路並用：XAML 走 DynamicResource，程式碼走這裡（VizPalette）。

    private static readonly Dictionary<string, SolidColorBrush> LiveBrushes = [];

    /// <summary>取供程式碼使用的共用筆刷（<see cref="VizPalette"/> 的來源）；未建立則回 null。</summary>
    public static SolidColorBrush? LiveBrush(string key)
        => LiveBrushes.TryGetValue(key, out var b) ? b : null;

    private static void SetLive(string key, Color c)
    {
        if (LiveBrushes.TryGetValue(key, out var b))
        {
            // 建立它的執行緒之外改不動（測試可能在另一條 STA 執行緒重跑）；改不動就換一支新的
            try { b.Color = c; return; }
            catch (InvalidOperationException) { }
        }
        LiveBrushes[key] = new SolidColorBrush(c);
    }

    /// <summary>換掉一支純色筆刷資源（已有 <see cref="Color"/> 時用這個，不必再繞一趟字串）。</summary>
    private static void SetBrush(string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        Application.Current.Resources[key] = b;
        SetLive(key, c);
    }

    private static void SetGradient(string key, Color top, Color bottom)
        => SetGradientStops(key, (0.0, top), (1.0, bottom));

    /// <summary>
    /// 換掉一支漸層筆刷資源。以現有筆刷的複本為底改色，方向（StartPoint／EndPoint）與停駐點位置
    /// 因此仍由 Theme.xaml 決定，不在這裡重複一份。
    /// </summary>
    private static void SetGradientStops(string key, params (double off, Color col)[] stops)
    {
        if (Application.Current.Resources[key] is not LinearGradientBrush cur)
        {
            Diag.Swallow($"ThemeService.SetGradientStops({key})", null,
                $"佈景資源 {key} 不是 LinearGradientBrush：該處顏色不會跟著主題走。");
            return;
        }

        var g = cur.CloneCurrentValue();
        for (int i = 0; i < stops.Length && i < g.GradientStops.Count; i++)
            g.GradientStops[i].Color = stops[i].col;
        g.Freeze();
        Application.Current.Resources[key] = g;
    }

    private static Color Hex(string h) => (Color)ColorConverter.ConvertFromString(h);

    /// <summary>a、b 依 t 混色（t=0→全 a，t=1→全 b）。</summary>
    private static Color Blend(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }
}
