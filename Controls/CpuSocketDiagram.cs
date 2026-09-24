using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// CPU 腳座示意圖：畫出腳座（socket）的頂視示意——腳位柵格、第 1 腳三角標記與方向缺口。
/// </summary>
/// <remarks>
/// <para>
/// LGA（Intel）腳位在主機板腳座上、CPU 是平面接點；PGA（AMD AM4 等）腳位在 CPU 上。
/// 兩者的第 1 腳角落都有金色三角標記，插反了會對不上——這張圖就是用來認方向的。
/// </para>
/// <para>
/// <b>示意圖，不是工程圖。</b>柵格點只表示腳位很密，密度不等於實際腳位數；缺口與三角
/// 標記表示方向。實際腳位數見上方規格。
/// </para>
/// </remarks>
public sealed class CpuSocketDiagram : FrameworkElement
{
    /// <summary>封裝方式字串（含 "LGA" 或 "PGA"）。</summary>
    public static readonly DependencyProperty MountProperty = DependencyProperty.Register(
        nameof(Mount), typeof(string), typeof(CpuSocketDiagram),
        new FrameworkPropertyMetadata("LGA", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Mount { get => (string)GetValue(MountProperty); set => SetValue(MountProperty, value); }

    /// <summary>腳座名稱（顯示在圖中央）。</summary>
    public static readonly DependencyProperty SocketProperty = DependencyProperty.Register(
        nameof(Socket), typeof(string), typeof(CpuSocketDiagram),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Socket { get => (string)GetValue(SocketProperty); set => SetValue(SocketProperty, value); }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 360 : available.Width;
        return new Size(w, 300);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth > 0 ? ActualWidth : 360;
        double h = ActualHeight > 0 ? ActualHeight : 300;

        var muted = ThemeBrush("MutedBrush", Color.FromRgb(0x9a, 0x9a, 0x9a));
        var text = ThemeBrush("TextBrush", Colors.White);
        var accent = ThemeBrush("AccentBrush", Color.FromRgb(0x4C, 0x8D, 0xFF));
        var frame = ThemeBrush("Surface2Brush", Color.FromRgb(0x2a, 0x2a, 0x2e));
        var gold = new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27));
        var tf = new Typeface("Segoe UI");

        bool pga = Mount?.Contains("PGA", StringComparison.OrdinalIgnoreCase) == true;

        // 正方形腳座置中
        double side = Math.Min(w, h) - 40;
        double x0 = (w - side) / 2, y0 = (h - side) / 2;

        // 腳座外框（圓角）
        var body = new Rect(x0, y0, side, side);
        dc.DrawRoundedRectangle(frame, new Pen(muted, 1.5), body, 8, 8);

        // 方向缺口：Intel LGA 兩個對邊各一個半圓凹口；AMD PGA 用一角削平
        if (!pga)
        {
            double ny = y0 + side / 2;
            dc.DrawEllipse(frame, new Pen(muted, 1.5), new Point(x0, ny), 7, 12);
            dc.DrawEllipse(frame, new Pen(muted, 1.5), new Point(x0 + side, ny), 7, 12);
        }

        // 腳位柵格（示意）：畫 N×N 的小點，留出中央文字區
        int n = 20;
        double pad = 18;
        double gx = (side - pad * 2) / (n - 1);
        double dotR = pga ? 1.8 : 1.4;
        var dotBrush = pga ? gold : muted;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                // 中央 6×3 區塊留白給文字
                if (r >= n / 2 - 2 && r <= n / 2 + 1 && c >= n / 2 - 5 && c <= n / 2 + 4) continue;
                double px = x0 + pad + c * gx;
                double py = y0 + pad + r * gx;
                dc.DrawEllipse(dotBrush, null, new Point(px, py), dotR, dotR);
            }

        // 第 1 腳三角標記（左下角，金色）
        var tri = new StreamGeometry();
        using (var ctx = tri.Open())
        {
            double tx = x0 + 8, ty = y0 + side - 8;
            ctx.BeginFigure(new Point(tx, ty), true, true);
            ctx.LineTo(new Point(tx + 14, ty), true, false);
            ctx.LineTo(new Point(tx, ty - 14), true, false);
        }
        tri.Freeze();
        dc.DrawGeometry(gold, null, tri);
        var pin1 = new FormattedText("Pin 1", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            tf, 9, gold, 1.25);
        dc.DrawText(pin1, new Point(x0 + 6, y0 + side - 30));

        // 中央文字：腳座名稱 + LGA/PGA
        if (!string.IsNullOrEmpty(Socket))
        {
            var name = new FormattedText(Socket, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                tf, 17, text, 1.25);
            dc.DrawText(name, new Point(x0 + side / 2 - name.Width / 2, y0 + side / 2 - name.Height));
        }
        var mount = new FormattedText(pga ? "PGA · 腳位在 CPU 上" : "LGA · 腳位在腳座上",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11, accent, 1.25);
        dc.DrawText(mount, new Point(x0 + side / 2 - mount.Width / 2, y0 + side / 2 + 4));
    }

    private static SolidColorBrush ThemeBrush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b;
        return new SolidColorBrush(fallback);
    }
}
