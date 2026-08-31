using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 插槽配置圖：一列一個通道，一個圓角長條一個實體插槽。裝了模組的畫成實心並寫上容量，
/// 空的畫成虛線外框寫「空」——一眼看得出「哪裡還能插」與「有沒有插對通道」。
/// </summary>
/// <remarks>
/// 資料來自 <see cref="DimmLayout"/>（純函式，只讀 SMBIOS Type 17）。這裡不做任何判斷，
/// 只負責畫；通道推不出來時 <see cref="DimmLayout"/> 會給單一「插槽」群組，圖就退成一列。
/// 沒有動畫：這是靜態配置，動起來只會多花功耗、不多給資訊。
/// </remarks>
public sealed class DimmMap : FrameworkElement
{
    private const double RowGap = 10;      // 通道之間
    private const double SlotGap = 8;      // 插槽之間
    private const double LabelW = 62;      // 左側通道名寬度
    private const double SlotH = 52;
    private const double MinSlotW = 22;    // 再窄就只剩邊框，但仍然不許畫到隔壁去

    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(DimmLayoutView), typeof(DimmMap),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public DimmLayoutView? Layout { get => (DimmLayoutView?)GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }

    public DimmMap()
    {
        SnapsToDevicePixels = true;
        // 伺服器板一列可能有 12 個插槽；擠不下時寧可裁掉，也不要把字畫到隔壁通道上
        ClipToBounds = true;
        ToolTipService.SetInitialShowDelay(this, 250);
    }

    /// <summary>本次繪製的插槽落點，供滑鼠停留時查出是哪一槽。</summary>
    private readonly List<(Rect Box, DimmSlotView Slot)> _hit = [];

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        foreach (var (box, slot) in _hit)
        {
            if (!box.Contains(p)) continue;
            if (ToolTip as string != slot.Tip) ToolTip = slot.Tip;
            return;
        }
        ToolTip = null;
    }

    protected override Size MeasureOverride(Size available)
    {
        int rows = Math.Max(1, Layout?.Channels.Count ?? 1);
        double h = rows * SlotH + (rows - 1) * RowGap;
        double w = double.IsInfinity(available.Width) ? 480 : available.Width;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var view = Layout;
        var ink = VizPalette.Ink;
        var faint = VizPalette.Muted;
        _hit.Clear();

        if (view is null || !view.HasData)
        {
            Text(dc, "沒有插槽資料可畫。", 12.5, faint, 2, 4);
            return;
        }

        double y = 0;
        foreach (var chan in view.Channels)
        {
            // 通道名（推不出通道時 DimmLayout 只給一組，名字是「插槽」）
            Text(dc, chan.Name, 12.5, faint, 0, y + SlotH / 2 - 9);

            double x = LabelW;
            int n = Math.Max(1, chan.Slots.Count);
            // 寬度不夠時所有插槽一起變窄（下限 MinSlotW），不讓最後幾格溢出到通道名或畫面外
            double avail = Math.Max(MinSlotW * n, ActualWidth - LabelW - (n - 1) * SlotGap);
            double slotW = Math.Max(MinSlotW, avail / n);

            foreach (var s in chan.Slots)
            {
                var box = new Rect(x, y, slotW, SlotH);
                DrawSlot(dc, box, s, ink, faint);
                _hit.Add((box, s));
                x += slotW + SlotGap;
            }
            y += SlotH + RowGap;
        }
    }

    private void DrawSlot(DrawingContext dc, Rect r, DimmSlotView s, Brush ink, Brush faint)
    {
        var round = new Rect(r.X, r.Y, Math.Max(1, r.Width), r.Height);

        if (s.Occupied)
        {
            // 實心＝有模組。用強調色的淡底加實線邊，避免整條純色蓋掉上面的文字
            dc.PushOpacity(0.18);
            dc.DrawRoundedRectangle(VizPalette.Accent, null, round, 6, 6);
            dc.Pop();
            dc.DrawRoundedRectangle(null, new Pen(VizPalette.Accent, 1.4), round, 6, 6);
        }
        else
        {
            // 虛線＝空槽。刻意不填色，才不會讓人以為「有東西只是灰的」；
            // 但要填透明底，否則框內是空的、滑鼠停在中央查不到這一槽
            var pen = new Pen(faint, 1.2) { DashStyle = new DashStyle([3, 3], 0) };
            dc.DrawRoundedRectangle(Brushes.Transparent, pen, round, 6, 6);
        }

        // 標籤（A1／DIMM0）＋第二行（容量・速率 或 空）
        var label = Format(s.Label, 12.5, s.Occupied ? ink : faint, r.Width - 12);
        var detail = Format(s.Detail, 11, faint, r.Width - 12);
        double th = label.Height + detail.Height;
        double ty = r.Y + (r.Height - th) / 2;
        dc.DrawText(label, new Point(r.X + 8, ty));
        dc.DrawText(detail, new Point(r.X + 8, ty + label.Height));
    }

    private FormattedText Format(string text, double size, Brush brush, double maxWidth)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft JhengHei UI"),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        if (maxWidth > 8) ft.MaxTextWidth = maxWidth;
        return ft;
    }

    private void Text(DrawingContext dc, string text, double size, Brush brush, double x, double y)
        => dc.DrawText(Format(text, size, brush, ActualWidth - x - 4), new Point(x, y));
}
