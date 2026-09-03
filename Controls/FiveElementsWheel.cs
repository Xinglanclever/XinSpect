using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 「五族共和」五行輪：外圈一個圓、內圈一顆五角星，五個節點上各放一個元素字，
/// 圈外對應寫上協力的模型名，圓心寫「五族共和」。
/// </summary>
/// <remarks>
/// <para>
/// 資料與幾何在 <see cref="FiveElements"/>（純函式，可測）；這裡只負責畫。沒有動畫：
/// 這是靜態圖，動起來只會多花功耗、不多給資訊。
/// </para>
/// <para>
/// <b>版面尺寸由文字實測寬度算出來，不是寫死的。</b>第一版把每個名字放進固定寬度的框裡、
/// 沿半徑置中，結果右上與左上那兩個名字（最長的「Claude Opus 5 Thinking」）整段壓在節點圓上——
/// 固定框在圓的兩側各吃掉一半寬度，圓就被蓋住了。現在改成量出每段文字的實際寬度，
/// 再依節點在圓周上的位置把它靠到圓的外側（上方置中／右側左靠／下方置中／左側右靠），
/// 元件的 DesiredSize 也跟著算出來——換字型或換系統縮放都不會裁到字。
/// </para>
/// <para>
/// 所有顏色走 <see cref="VizPalette"/>（每次重畫都重新查資源），並在建構時掛
/// <see cref="ThemeAware.RepaintOnThemeChange"/>：不然換主題之後這張圖會停在舊配色，
/// 在淺色主題下變成白底白線。
/// </para>
/// </remarks>
public sealed class FiveElementsWheel : FrameworkElement
{
    private const double RingR = 128;    // 五個節點所在的圓半徑
    private const double NodeR = 26;     // 節點圓半徑
    private const double Gap = 12;       // 節點圓與名字之間
    private const double Pad = 4;        // 四周留白

    /// <summary>名字要靠到節點的哪一側。由節點在圓周上的角度決定。</summary>
    private enum Side { Top, Right, Bottom, Left }

    public FiveElementsWheel()
    {
        this.RepaintOnThemeChange();
        SnapsToDevicePixels = true;
        // 自繪的圖對螢幕閱讀器是一片空白：整張圖的內容在這裡用一句話交代，
        // 這樣版面上就不必再放一行意思一樣的文字。
        System.Windows.Automation.AutomationProperties.SetName(this, Describe());
        ToolTip = Describe();
    }

    /// <summary>整張圖的文字版本（無障礙名稱與滑鼠提示共用）。</summary>
    public static string Describe()
        => FiveElements.Caption + "：" + string.Join("、",
            FiveElements.Nodes.Select(n => $"{n.Element} {n.Label}"));

    private static Side SideOf(double angleDeg) => angleDeg switch
    {
        < 45 or > 315 => Side.Top,
        <= 135 => Side.Right,
        <= 225 => Side.Bottom,
        _ => Side.Left,
    };

    /// <summary>整張圖的框：尺寸與圓心。左右上下各自取「該側最遠的字」算出來。</summary>
    private (Size Size, Point Centre) Frame()
    {
        double l = RingR + NodeR, r = RingR + NodeR, t = RingR + NodeR, b = RingR + NodeR;

        foreach (var n in FiveElements.Nodes)
        {
            var ft = Label(n.Label, Brushes.Black);
            var (ux, uy) = n.Unit;
            double nx = ux * RingR, ny = uy * RingR;      // 節點相對圓心的位置

            switch (SideOf(n.AngleDeg))
            {
                case Side.Top:
                    t = Math.Max(t, -ny + NodeR + Gap + ft.Height);
                    l = Math.Max(l, -nx + ft.Width / 2);
                    r = Math.Max(r, nx + ft.Width / 2);
                    break;
                case Side.Bottom:
                    b = Math.Max(b, ny + NodeR + Gap + ft.Height);
                    l = Math.Max(l, -nx + ft.Width / 2);
                    r = Math.Max(r, nx + ft.Width / 2);
                    break;
                case Side.Right:
                    r = Math.Max(r, nx + NodeR + Gap + ft.Width);
                    break;
                default:
                    l = Math.Max(l, -nx + NodeR + Gap + ft.Width);
                    break;
            }
        }

        return (new Size(l + r + Pad * 2, t + b + Pad * 2), new Point(l + Pad, t + Pad));
    }

    protected override Size MeasureOverride(Size availableSize) => Frame().Size;

    protected override void OnRender(DrawingContext dc)
    {
        var (_, c) = Frame();
        var accent = VizPalette.Accent;
        var ink = VizPalette.Ink;
        var name = VizPalette.Of("SecondaryInkBrush", "#c3c2b7");

        // 外圈：五個節點都坐在這條線上，和參考圖一樣
        dc.DrawEllipse(null, new Pen(VizPalette.Grid, 2), c, RingR, RingR);

        // 內星：跨兩格連線並閉合（跨一格會畫成五邊形，見 FiveElements.StarOrder）
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var order = FiveElements.StarOrder;
            ctx.BeginFigure(NodeAt(c, FiveElements.Nodes[order[0]]), isFilled: false, isClosed: true);
            for (int i = 1; i < order.Count; i++)
                ctx.LineTo(NodeAt(c, FiveElements.Nodes[order[i]]), isStroked: true, isSmoothJoin: false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(VizPalette.Hairline, 1.4), geo);

        foreach (var n in FiveElements.Nodes)
        {
            var p = NodeAt(c, n);
            dc.DrawEllipse(VizPalette.Card, new Pen(accent, 1.6), p, NodeR, NodeR);

            var ch = Element(n.Element, ink);
            dc.DrawText(ch, new Point(p.X - ch.Width / 2, p.Y - ch.Height / 2));

            var ft = Label(n.Label, name);
            dc.DrawText(ft, SideOf(n.AngleDeg) switch
            {
                Side.Top => new Point(p.X - ft.Width / 2, p.Y - NodeR - Gap - ft.Height),
                Side.Bottom => new Point(p.X - ft.Width / 2, p.Y + NodeR + Gap),
                Side.Right => new Point(p.X + NodeR + Gap, p.Y - ft.Height / 2),
                _ => new Point(p.X - NodeR - Gap - ft.Width, p.Y - ft.Height / 2),
            });
        }

        // 圓心那四個字。五角星的內五邊形是空的（內切半徑約 39.5），這行字的角落落在半徑 36 以內，
        // 所以不會壓到任何一條線，也就不需要拿底色去遮。
        var cap = Format(FiveElements.Caption, 17, accent, bold: true);
        dc.DrawText(cap, new Point(c.X - cap.Width / 2, c.Y - cap.Height / 2));
    }

    private static Point NodeAt(Point c, FiveElementNode n)
    {
        var (x, y) = n.At(c.X, c.Y, RingR);
        return new Point(x, y);
    }

    private FormattedText Element(string s, Brush b) => Format(s, 21, b, bold: true);
    private FormattedText Label(string s, Brush b) => Format(s, 11.5, b, bold: false);

    private FormattedText Format(string text, double size, Brush brush, bool bold)
    {
        var face = new Typeface(new FontFamily("Microsoft JhengHei UI"), FontStyles.Normal,
                                bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                 face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxLineCount = 1 };
    }
}
