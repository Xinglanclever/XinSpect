using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 自繪圖表與轉換器的取色點：一律回傳<b>同一顆</b>會跟著主題走的筆刷物件。
/// </summary>
/// <remarks>
/// 佈景色有兩條路：XAML 以 <c>{DynamicResource}</c> 取用，換色時整支資源被換掉；
/// 而程式碼拿到的是筆刷「物件」，換掉資源的通知傳不到它身上。所以這裡回的不是資源字典裡那一支
/// （放進字典的筆刷會被 WPF 凍結、改不動），而是 <see cref="ThemeService"/> 另外持有的一份
/// 未凍結筆刷——換主題時就地改它的 <c>.Color</c>，已經畫上去的內容與已求值的繫結都會自己跟著變。
/// <para>反過來說，回傳的筆刷是全域共用的：呼叫端<b>絕對不可以</b>改它的 <c>Color</c> 或
/// <c>Opacity</c>（要淡化請設在圖形本身的 <c>Opacity</c> 上），否則會連帶改掉整個應用程式的配色。</para>
/// <para>只有<b>複製色值</b>（如 <see cref="AccentColor"/>、<see cref="Blend"/>）的呼叫端仍需自行
/// 重畫，因為那是當下的一份快照；請於建構時呼叫 <c>this.RepaintOnThemeChange()</c>。</para>
/// <para>取不到時（設計時期預覽、單元測試沒有 <see cref="Application"/>）回傳凍結的後備色，
/// 讓繪圖照樣畫得出來而不是直接丟例外。後備色與 Theme.xaml 的深色預設一致。</para>
/// </remarks>
public static class VizPalette
{
    // 後備筆刷依色碼共用一份：格線每次重畫都會取色，不該每次配置新物件。
    private static readonly ConcurrentDictionary<string, SolidColorBrush> Fallbacks = new();

    /// <summary>取共用筆刷；取不到（或不在建立它的執行緒上）時回傳後備色。</summary>
    public static SolidColorBrush Of(string key, string fallbackHex)
    {
        try
        {
            if (ThemeService.LiveBrush(key) is { } live && live.CheckAccess()) return live;
            if (Application.Current?.Resources[key] is SolidColorBrush b) return b;
        }
        catch { /* 非 UI 執行緒或資源字典尚未就緒：退回後備色 */ }

        return Fallbacks.GetOrAdd(fallbackHex, static hex =>
        {
            var f = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            f.Freeze();
            return f;
        });
    }

    /// <summary>取資源筆刷的當下色值（需要 <see cref="Color"/> 而非筆刷時，例如漸層停駐點與陰影色）。</summary>
    public static Color ColorOf(string key, string fallbackHex) => Of(key, fallbackHex).Color;

    // ── 圖表語彙：哪個角色對應哪個資源鍵，只在這裡決定一次 ────────────────────

    /// <summary>格線與座標基線。<c>BaselineBrush</c> 本來就是為此而設，深淺主題各有一階。</summary>
    public static SolidColorBrush Grid => Of("BaselineBrush", "#383835");

    /// <summary>細分隔線、卡片描邊。</summary>
    public static SolidColorBrush Hairline => Of("HairlineBrush", "#2c2c2a");

    /// <summary>刻度文字、次要說明。</summary>
    public static SolidColorBrush Muted => Of("MutedInkBrush", "#898781");

    /// <summary>讀值等主要文字。</summary>
    public static SolidColorBrush Ink => Of("PrimaryInkBrush", "#ffffff");

    /// <summary>浮出卡片（十字準線讀值卡）的底色。</summary>
    public static SolidColorBrush Card => Of("Surface2Brush", "#232322");

    /// <summary>目前強調色。</summary>
    public static SolidColorBrush Accent => Of("AccentBrush", "#3987e5");

    /// <summary>目前強調色的色值。</summary>
    public static Color AccentColor => Accent.Color;

    /// <summary><paramref name="a"/>、<paramref name="b"/> 依 <paramref name="t"/> 混色（t=0→全 a，t=1→全 b）。</summary>
    public static Color Blend(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }
}
