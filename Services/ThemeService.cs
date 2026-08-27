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
}

/// <summary>
/// 外觀套用引擎（固定深色主題 + 固定藍色強調，不提供執行期調色）。
/// 核心手法：Theme.xaml 內的 SolidColorBrush / LinearGradientBrush 皆未凍結、以 {StaticResource} 取用
/// （解析為同一筆刷物件）；於啟動時直接改寫其 .Color / 漸層停駐點，即可統一套用配色。
/// </summary>
public static class ThemeService
{
    /// <summary>固定主題（深色）。</summary>
    public static AppTheme Theme => AppTheme.Dark;

    /// <summary>固定強調色（藍）。TrayService 圖示與標題輝光取用其色值。</summary>
    public static AccentPreset Accent { get; } = new("blue", "藍", "#3987e5", "#1c5cab", "#4c96f0");

    // ---- 面板配色（深色） ------------------------------------------------
    private sealed record Palette(
        string PagePlane, string Surface, string Surface2,
        string PrimaryInk, string SecondaryInk, string MutedInk,
        string Hairline, string Baseline);

    private static readonly Palette Dark = new(
        "#0d0d0d", "#1a1a19", "#232322",
        "#ffffff", "#c3c2b7", "#898781",
        "#2c2c2a", "#383835");

    // ---- 初始化 ----------------------------------------------------------

    public static void Initialize() => ApplyAll();

    public static void ApplyAll()
    {
        var app = Application.Current;
        if (app is null) return;

        var p = Dark;

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

        // 標題列漸層：以強調色調染，偏暗（深色底）
        Color plane = Hex(p.PagePlane);
        Color surface = Hex(p.Surface);
        Color tint = Blend(Accent.MainColor, plane, 0.80);
        SetGradientStops("HeaderGradientBrush",
            (0.0, tint),
            (0.45, Hex("#181819")),
            (1.0, surface));
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
