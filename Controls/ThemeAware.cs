using System.Windows;

namespace XinSpect;

/// <summary>
/// 讓自繪元件在換主題／強調色之後重畫一次。
/// </summary>
/// <remarks>
/// <see cref="VizPalette"/> 每次取色都重新查資源，所以「下一次重畫」一定用到新配色；
/// 但已經畫進視覺內容裡的筆刷是<b>凍結</b>的複本，不會自己跟著變。動畫中的元件每格都在重畫，
/// 看起來自己就好了；不動的（空的插槽圖、停轉的風扇、靜態讀值）則會一直停在舊配色——
/// 在淺色主題下就是白底上的白字。故凡是在 <c>OnRender</c> 裡取 <see cref="VizPalette"/> 的元件，
/// 都要在建構時呼叫 <see cref="RepaintOnThemeChange"/>。
/// <para>訂閱掛在 <c>Loaded</c>／<c>Unloaded</c>：換頁時自動退訂，不會留下讓元件無法回收的參考；
/// 重複 <c>Loaded</c>（換回同一頁）也不會重複訂閱。</para>
/// </remarks>
public static class ThemeAware
{
    /// <summary>訂閱外觀變更，變更後對此元件呼叫 <see cref="UIElement.InvalidateVisual"/>。</summary>
    public static void RepaintOnThemeChange(this FrameworkElement el) => el.OnThemeChange(el.InvalidateVisual);

    /// <summary>訂閱外觀變更並執行指定動作（自繪內容不是走 <c>OnRender</c> 的元件用這個）。</summary>
    public static void OnThemeChange(this FrameworkElement el, Action redraw)
    {
        void Handler() => redraw();
        el.Loaded += (_, _) =>
        {
            ThemeService.Changed -= Handler;   // 重複 Loaded 不重覆訂閱
            ThemeService.Changed += Handler;
        };
        el.Unloaded += (_, _) => ThemeService.Changed -= Handler;
    }
}
