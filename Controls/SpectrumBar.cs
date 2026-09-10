using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 音訊 FFT 頻譜長條圖：32 個頻段各一根垂直長條，高度對應 dB 值，
/// 底部有微弱的鏡像反射效果。長條有平滑下降動畫（每幀頂多下降 2 dB）以防閃爍。
/// </summary>
/// <remarks>
/// <para>繫結 <see cref="Data"/>（float[32]）即可；配色走 <see cref="VizPalette.Accent"/>，
/// 換主題時自動重畫（<see cref="ThemeAware.RepaintOnThemeChange"/>）。</para>
/// <para>反射效果以 0.18 透明度畫出高度 30% 的倒影，不另外分配 RenderTarget。</para>
/// </remarks>
public sealed class SpectrumBar : FrameworkElement
{
    /// <summary>每幀最大下降速度（dB）。上升不限速，下降限速以避免閃爍。</summary>
    private const float FalloffPerFrame = 2f;

    /// <summary>反射區佔長條高度的比例。</summary>
    private const double ReflectionRatio = 0.30;

    /// <summary>反射區的最大不透明度。</summary>
    private const double ReflectionOpacity = 0.18;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(float[]), typeof(SpectrumBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));
    /// <summary>32 個頻段的 dB 值（-100 到 0）。</summary>
    public float[]? Data { get => (float[])GetValue(DataProperty); set => SetValue(DataProperty, value); }

    private readonly float[] _display = new float[AudioSpectrumService.BandCount];

    public SpectrumBar()
    {
        this.RepaintOnThemeChange();
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        MinHeight = 80;

        // 初始值為靜音
        Array.Fill(_display, -100f);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (SpectrumBar)d;
        self.ApplySmoothing();
    }

    /// <summary>將新資料套用平滑下降：上升立即、下降限速。</summary>
    private void ApplySmoothing()
    {
        var raw = Data;
        if (raw is null || raw.Length < AudioSpectrumService.BandCount) return;

        for (int i = 0; i < AudioSpectrumService.BandCount; i++)
        {
            float target = Math.Clamp(raw[i], -100f, 0f);
            if (target >= _display[i])
            {
                // 上升：立即跟上
                _display[i] = target;
            }
            else
            {
                // 下降：每幀最多降 FalloffPerFrame dB
                _display[i] = Math.Max(target, _display[i] - FalloffPerFrame);
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;

        // 背景
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        int bands = AudioSpectrumService.BandCount;
        double gap = Math.Max(1, w * 0.01);          // 長條間距
        double barW = Math.Max(2, (w - gap * (bands - 1)) / bands);
        double totalW = barW * bands + gap * (bands - 1);
        double offsetX = (w - totalW) / 2;             // 置中

        // 主長條區與反射區的高度分配
        double reflectH = h * 0.12;                    // 反射區高度
        double mainH = h - reflectH - 2;               // 主長條區（留 2px 間距）

        var accentColor = VizPalette.AccentColor;

        for (int i = 0; i < bands; i++)
        {
            float db = _display[i];
            // 將 -100..0 dB 映射為 0..1 高度比
            double normalized = Math.Clamp((db + 100.0) / 100.0, 0, 1);
            double barH = normalized * mainH;
            if (barH < 0.5) continue;   // 靜音不畫

            double x = offsetX + i * (barW + gap);
            double y = mainH - barH;    // 長條頂部

            // 顏色：低頻段偏暖、高頻段偏冷，以強調色為基底做漸層
            double t = (double)i / (bands - 1);
            byte r = (byte)Math.Clamp(accentColor.R + (int)((1.0 - t) * 30 - t * 20), 0, 255);
            byte g = (byte)Math.Clamp(accentColor.G + (int)(t * 15), 0, 255);
            byte b = (byte)Math.Clamp(accentColor.B + (int)(t * 30), 0, 255);
            var barColor = Color.FromRgb(r, g, b);

            // 主長條：從頂端到底端的垂直漸層
            var barBrush = new LinearGradientBrush(
                Color.FromArgb(255, barColor.R, barColor.G, barColor.B),
                Color.FromArgb(180, barColor.R, barColor.G, barColor.B),
                new Point(0, 0), new Point(0, 1));
            barBrush.Freeze();
            dc.DrawRoundedRectangle(barBrush, null,
                new Rect(x, y, barW, barH), 1.5, 1.5);

            // 反射效果：在主長條區下方畫一條較矮、漸層淡出的倒影
            double refH = barH * ReflectionRatio;
            if (refH >= 1)
            {
                double refY = mainH + 2;   // 反射起始位置（間距 2px）
                var refBrush = new LinearGradientBrush(
                    Color.FromArgb((byte)(255 * ReflectionOpacity), barColor.R, barColor.G, barColor.B),
                    Color.FromArgb(0, barColor.R, barColor.G, barColor.B),
                    new Point(0, 0), new Point(0, 1));
                refBrush.Freeze();
                dc.DrawRoundedRectangle(refBrush, null,
                    new Rect(x, refY, barW, refH), 1.5, 1.5);
            }
        }
    }
}
