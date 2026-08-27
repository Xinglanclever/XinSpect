using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XinSpect;

/// <summary>
/// 系統匣圖示與選單。圖示以目前強調色即時繪製（放大鏡＝「覽 / 檢視」）。
/// 透過事件通知主視窗處理顯示主視窗 / 切換迷你視窗 / 結束。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private IntPtr _hIcon;

    public event Action? ShowMainRequested;
    public event Action? ToggleMiniRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _icon = new NotifyIcon
        {
            Visible = true,
            Text = "曦覽 XinSpect ・ 硬體資訊總覽",
            Icon = BuildIcon(),
        };
        _icon.DoubleClick += (_, _) => ShowMainRequested?.Invoke();

        var menu = new ContextMenuStrip { Font = MenuFont() };
        menu.Items.Add("顯示主視窗", null, (_, _) => ShowMainRequested?.Invoke());
        menu.Items.Add("迷你浮動監視器", null, (_, _) => ToggleMiniRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("結束 曦覽", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
    }

    /// <summary>顯示系統匣氣泡通知（供硬體警示使用）。</summary>
    public void ShowBalloon(string title, string text)
    {
        try { _icon.ShowBalloonTip(6000, title, text, ToolTipIcon.Warning); }
        catch { /* 氣泡通知為附加，失敗略過 */ }
    }

    private static Font MenuFont()
    {
        try { return new Font("Microsoft JhengHei UI", 9f); }
        catch { return SystemFonts.MenuFont ?? new Font(FontFamily.GenericSansSerif, 9f); }
    }

    private Icon BuildIcon()
    {
        var top = ThemeService.Accent.GradTopColor;   // System.Windows.Media.Color（以 var 取用，避免與 GDI 型別衝突）
        var dim = ThemeService.Accent.DimColor;
        var cTop = Color.FromArgb(255, top.R, top.G, top.B);
        var cDim = Color.FromArgb(255, dim.R, dim.G, dim.B);

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var rect = new Rectangle(2, 2, 27, 27);
            using (var path = RoundedRect(rect, 7))
            using (var br = new LinearGradientBrush(rect, cTop, cDim, 45f))
                g.FillPath(br, path);

            using var pen = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawEllipse(pen, 9, 8, 10, 10);   // 放大鏡鏡框
            g.DrawLine(pen, 18, 17, 24, 23);    // 放大鏡手把
        }

        _hIcon = bmp.GetHicon();
        return Icon.FromHandle(_hIcon);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
    }
}
