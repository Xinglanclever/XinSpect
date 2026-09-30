using System.Globalization;
using System.Windows;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace XinSpect;

/// <summary>
/// 實用工具 › 螢幕色域：EDID 覆蓋率（自工具箱遷入）＋ 2D CIE 色度圖 ＋ 3D RGB 色彩立方。
/// 資料來源為 <see cref="MainViewModel.Monitors"/>（開機背景解析一次，報告與 AI 共用）。
/// </summary>
public partial class ScreenGamutView : UserControl
{
    public ScreenGamutView() => InitializeComponent();
}

/// <summary>
/// CIE 1931 xy 色度圖（2D）：畫光譜軌跡（馬蹄形）、sRGB 參考三角形與本機各螢幕的色域三角形。
/// 光譜軌跡資料為 CIE 1931 2° 標準觀察者，380–700 nm 每 10 nm 一點。
/// </summary>
public sealed class GamutChromaticityDiagram : FrameworkElement
{
    public static readonly DependencyProperty MonitorsProperty = DependencyProperty.Register(
        nameof(Monitors), typeof(System.Collections.Generic.IReadOnlyList<MonitorGamutInfo>),
        typeof(GamutChromaticityDiagram), new PropertyMetadata(null, (_, _) => _sharedVisual?.InvalidateVisual()));

    public System.Collections.Generic.IReadOnlyList<MonitorGamutInfo>? Monitors
    {
        get => (System.Collections.Generic.IReadOnlyList<MonitorGamutInfo>?)GetValue(MonitorsProperty);
        set => SetValue(MonitorsProperty, value);
    }

    // 靜態共享：Monitors 變更時讓所有實例（含未進入視覺樹的）重畫
    private static GamutChromaticityDiagram? _sharedVisual;

    /// <summary>CIE 1931 2° 標準觀察者光譜軌跡（波長 nm → (x, y)）。</summary>
    internal static readonly (int Wl, double X, double Y)[] Locus =
    [
        (380, 0.1741, 0.0050), (390, 0.1738, 0.0049), (400, 0.1733, 0.0048),
        (410, 0.1726, 0.0048), (420, 0.1714, 0.0051), (430, 0.1689, 0.0069),
        (440, 0.1644, 0.0109), (450, 0.1566, 0.0177), (460, 0.1440, 0.0297),
        (470, 0.1241, 0.0578), (480, 0.0913, 0.1327), (490, 0.0454, 0.2950),
        (500, 0.0082, 0.5384), (510, 0.0139, 0.7502), (520, 0.0743, 0.8338),
        (530, 0.1547, 0.8059), (540, 0.2296, 0.7543), (550, 0.3016, 0.6923),
        (560, 0.3731, 0.6245), (570, 0.4441, 0.5547), (580, 0.5125, 0.4866),
        (590, 0.5752, 0.4242), (600, 0.6270, 0.3725), (610, 0.6658, 0.3340),
        (620, 0.6915, 0.3083), (630, 0.7079, 0.2920), (640, 0.7190, 0.2809),
        (650, 0.7260, 0.2740), (660, 0.7300, 0.2700), (670, 0.7320, 0.2680),
        (680, 0.7334, 0.2666), (690, 0.7344, 0.2656), (700, 0.7347, 0.2653),
    ];

    private static readonly (double X, double Y)[] Srgb =
        { (0.640, 0.330), (0.300, 0.600), (0.150, 0.060) };

    public GamutChromaticityDiagram() => _sharedVisual = this;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        // 底板
        var bg = VizPalette.Of("SurfaceBrush", "#1a1a19");
        dc.DrawRoundedRectangle(bg, new Pen(VizPalette.Of("HairlineBrush", "#2c2c2a"), 1),
            new Rect(0, 0, w, h), 8, 8);

        // 繪圖區：x ∈ [−0.03, 0.78]、y ∈ [−0.03, 0.88]（留邊給標籤）
        const double xmin = -0.03, xmax = 0.78, ymin = -0.03, ymax = 0.88;
        double pad = 14;
        double iw = w - pad * 2, ih = h - pad * 2;
        Point Map(double x, double y) => new(
            pad + (x - xmin) / (xmax - xmin) * iw,
            h - pad - (y - ymin) / (ymax - ymin) * ih);

        // 光譜軌跡內部：深色淡染（不用飽和色填滿，避免蓋過螢幕三角形）
        var locusGeo = new StreamGeometry();
        using (var g = locusGeo.Open())
        {
            g.BeginFigure(Map(Locus[0].X, Locus[0].Y), true, true);
            for (int i = 1; i < Locus.Length; i++) g.LineTo(Map(Locus[i].X, Locus[i].Y), true, false);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(26, 120, 120, 120)), null, locusGeo);

        // 光譜軌跡邊線：每段以該波段近似色描繪（馬蹄形的彩虹感）
        for (int i = 1; i < Locus.Length; i++)
        {
            var a = Map(Locus[i - 1].X, Locus[i - 1].Y);
            var b = Map(Locus[i].X, Locus[i].Y);
            dc.DrawLine(new Pen(SpectralBrush(Locus[i].Wl), 2.2), a, b);
        }
        // 紫線（380–700 連線）
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(120, 120, 60, 160)), 1.5),
            Map(Locus[^1].X, Locus[^1].Y), Map(Locus[0].X, Locus[0].Y));

        // sRGB 參考三角形：虛線
        var dashed = new Pen(VizPalette.Of("MutedInkBrush", "#898781"), 1.4) { DashStyle = DashStyles.Dash };
        dc.DrawLine(dashed, Map(Srgb[0].X, Srgb[0].Y), Map(Srgb[1].X, Srgb[1].Y));
        dc.DrawLine(dashed, Map(Srgb[1].X, Srgb[1].Y), Map(Srgb[2].X, Srgb[2].Y));
        dc.DrawLine(dashed, Map(Srgb[2].X, Srgb[2].Y), Map(Srgb[0].X, Srgb[0].Y));
        DrawLabel(dc, "sRGB", Map(0.36, 0.335), VizPalette.Of("MutedInkBrush", "#898781"), 11);

        // 本機各螢幕三角形（多螢幕全畫，半透明疊加）
        var monitors = Monitors;
        if (monitors is not null)
        {
            foreach (var m in monitors)
            {
                if (!m.Valid) continue;
                var accent = VizPalette.Accent;
                var fill = new SolidColorBrush(((SolidColorBrush)accent).Color) { Opacity = 0.28 };
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(Map(m.Rx, m.Ry), true, true);
                    g.LineTo(Map(m.Gx, m.Gy), true, false);
                    g.LineTo(Map(m.Bx, m.By), true, false);
                }
                        dc.DrawGeometry(fill, new Pen(accent, 2), geo);

                DrawLabel(dc, "R", Map(m.Rx, m.Ry), VizPalette.Of("CriticalBrush", "#d03b3b"));
                DrawLabel(dc, "G", Map(m.Gx, m.Gy), VizPalette.Of("GoodBrush", "#0ca30c"));
                DrawLabel(dc, "B", Map(m.Bx, m.By), AccentInk());
                DrawLabel(dc, "W", Map(m.Wx, m.Wy), VizPalette.Of("PrimaryInkBrush", "#ffffff"), 10);
            }
        }

        DrawLabel(dc, "波長（nm）沿光譜軌跡：380 → 700", Map(0.10, -0.015),
            VizPalette.Of("MutedInkBrush", "#898781"), 10.5);
    }

    private static Brush AccentInk() => VizPalette.Of("AccentInkBrush", "#7db4ff");

    private static void DrawLabel(DrawingContext dc, string text, Point at, Brush brush, double size = 12)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, 1.25);
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
    }

    /// <summary>波長 → 近似可見色（標譜段對映；畫光譜軌跡邊線用，非色彩學精確值）。</summary>
    internal static Brush SpectralBrush(int nm) => new SolidColorBrush(WavelengthColor(nm));

    internal static Color WavelengthColor(int nm) => nm switch
    {
        < 400 => Color.FromRgb(0x60, 0x00, 0xCA),      // 紫外緣：深紫
        < 440 => Lerp(Color.FromRgb(0x60, 0x00, 0xCA), Color.FromRgb(0x1E, 0x32, 0xFF), (nm - 380) / 60.0),
        < 490 => Lerp(Color.FromRgb(0x1E, 0x32, 0xFF), Color.FromRgb(0x00, 0xC0, 0xFF), (nm - 440) / 50.0),
        < 510 => Lerp(Color.FromRgb(0x00, 0xC0, 0xFF), Color.FromRgb(0x00, 0xE8, 0x80), (nm - 490) / 20.0),
        < 580 => Lerp(Color.FromRgb(0x00, 0xE8, 0x80), Color.FromRgb(0xC8, 0xE8, 0x00), (nm - 510) / 70.0),
        < 645 => Lerp(Color.FromRgb(0xC8, 0xE8, 0x00), Color.FromRgb(0xFF, 0x30, 0x00), (nm - 580) / 65.0),
        _ => Color.FromRgb(0xB0, 0x00, 0x00),          // 深紅端
    };

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}

/// <summary>
/// RGB 色彩立方（3D）：以 R／G／B 為軸的單位立方，六個面各自帶該面的連續色彩紋理，
/// 可按住拖曳旋轉。用 WPF 內建 3D 管線，不引入新相依。
/// </summary>
public sealed class RgbCube3D : FrameworkElement
{
    private readonly Viewport3D _viewport = new();
    private double _yaw = -28, _pitch = 22;   // 初始視角：微微俯視的等角感
    private Point? _drag;

    public RgbCube3D()
    {
        var camera = new PerspectiveCamera
        {
            Position = new Point3D(2.1, 1.7, 2.6),
            LookDirection = new Vector3D(-2.1, -1.7, -2.6),
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = 45,
        };

        var model = new ModelVisual3D { Content = BuildCube() };
        var light = new ModelVisual3D
        {
            Content = new Model3DGroup
            {
                Children =
                [
                    new AmbientLight(Colors.White) ,   // 全環境光：立方體要自體發色，不能被光影吃掉
                ],
            },
        };

        _viewport.Camera = camera;
        _viewport.Children.Add(light);
        _viewport.Children.Add(model);
        _viewport.ClipToBounds = true;
        AddVisualChild(_viewport);

        MouseLeftButtonDown += (_, e) => { _drag = e.GetPosition(this); CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (_drag is not { } p) return;
            var now = e.GetPosition(this);
            _yaw += (now.X - p.X) * 0.5;
            _pitch = Math.Clamp(_pitch + (now.Y - p.Y) * 0.5, -88, 88);
            _drag = now;
            ApplyRotation();
        };
        MouseLeftButtonUp += (_, _) => { _drag = null; ReleaseMouseCapture(); };
        MouseWheel += (_, e) =>
        {
            var cam = (PerspectiveCamera)_viewport.Camera;
            double k = e.Delta > 0 ? 0.9 : 1.1;
            var pos = cam.Position;
            var scaled = new Point3D(pos.X * k, pos.Y * k, pos.Z * k);
            // 拉距下限避免穿進立方體
            if (scaled.X + scaled.Y + scaled.Z > 0.9) cam.Position = scaled;
        };

        Loaded += (_, _) => ApplyRotation();
    }

    protected override int VisualChildrenCount => 1;
    protected override System.Windows.Media.Visual GetVisualChild(int index) => _viewport;

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private static Model3D BuildCube()
    {
        var group = new Model3DGroup();
        // 六面：法向 ±X（R 軸）、±Y（G 軸）、±Z（B 軸）
        foreach (var (u, v, n) in new[]
        {
            (Vector3D(0, 0, 1), Vector3D(0, 1, 0), new Vector3D(1, 0, 0)),   // +X
            (Vector3D(0, 0, -1), Vector3D(0, 1, 0), new Vector3D(-1, 0, 0)), // -X
            (Vector3D(1, 0, 0), Vector3D(0, 0, -1), new Vector3D(0, 1, 0)),  // +Y
            (Vector3D(-1, 0, 0), Vector3D(0, 0, -1), new Vector3D(0, -1, 0)),// -Y
            (Vector3D(1, 0, 0), Vector3D(0, 1, 0), new Vector3D(0, 0, 1)),   // +Z
            (Vector3D(-1, 0, 0), Vector3D(0, 1, 0), new Vector3D(0, 0, -1)), // -Z
        })
        {
            group.Children.Add(BuildFace(n, u, v));
        }
        return group;
    }

    private static Vector3D Vector3D(double x, double y, double z) => new(x, y, z);

    /// <summary>單面：以法向決定面的原點顏色，UV 平面採 64×64 像素紋理呈現連續漸層。</summary>
    private static GeometryModel3D BuildFace(Vector3D normal, Vector3D uAxis, Vector3D vAxis)
    {
        // 面中心＝0.5·normal；面上點 = center + (u-0.5)·uAxis + (v-0.5)·vAxis
        var center = new Point3D(normal.X * 0.5, normal.Y * 0.5, normal.Z * 0.5);
        const int N = 8;   // 每邊細分（配合紋理已夠平滑，幾何不用太密）

        var mesh = new MeshGeometry3D();
        var corner0 = new Point3D(
            center.X - 0.5 * uAxis.X - 0.5 * vAxis.X,
            center.Y - 0.5 * uAxis.Y - 0.5 * vAxis.Y,
            center.Z - 0.5 * uAxis.Z - 0.5 * vAxis.Z);

        for (int j = 0; j <= N; j++)
        {
            for (int i = 0; i <= N; i++)
            {
                double du = (double)i / N, dv = (double)j / N;
                mesh.Positions.Add(new Point3D(
                    corner0.X + du * uAxis.X + dv * vAxis.X,
                    corner0.Y + du * uAxis.Y + dv * vAxis.Y,
                    corner0.Z + du * uAxis.Z + dv * vAxis.Z));
                mesh.TextureCoordinates.Add(new Point(du, dv));
            }
        }
        for (int j = 0; j < N; j++)
        {
            for (int i = 0; i < N; i++)
            {
                int a = j * (N + 1) + i, b = a + 1, c = a + N + 1, d = c + 1;
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(d);
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(d); mesh.TriangleIndices.Add(c);
            }
        }

        // 紋理：面上任一點的「立方體座標」就是它的顏色
        var tex = FaceTexture(normal, uAxis, vAxis);
        return new GeometryModel3D(mesh, new DiffuseMaterial { Brush = new ImageBrush(tex) })
        {
            BackMaterial = new DiffuseMaterial { Brush = new ImageBrush(tex) },
        };
    }

    private static WriteableBitmap FaceTexture(Vector3D normal, Vector3D uAxis, Vector3D vAxis)
    {
        const int S = 64;
        var bmp = new WriteableBitmap(S, S, 96, 96, PixelFormats.Bgra32, null);
        var px = new byte[S * S * 4];
        var origin = new Vector3D(
            (1 - normal.X) / 2 - uAxis.X / 2 - vAxis.X / 2,
            (1 - normal.Y) / 2 - uAxis.Y / 2 - vAxis.Y / 2,
            (1 - normal.Z) / 2 - uAxis.Z / 2 - vAxis.Z / 2);
        for (int y = 0; y < S; y++)
        {
            for (int x = 0; x < S; x++)
            {
                double u = (double)x / (S - 1), v = 1.0 - (double)y / (S - 1);
                var p = origin + uAxis * u + vAxis * v;
                int o = (y * S + x) * 4;
                px[o] = ToByte(p.X); px[o + 1] = ToByte(p.Y); px[o + 2] = ToByte(p.Z); px[o + 3] = 0xFF;
            }
        }
        bmp.WritePixels(new Int32Rect(0, 0, S, S), px, S * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(v * 255, 0, 255);

    private void ApplyRotation()
    {
        var axis = new Vector3D(0, 1, 0);
        var yaw = new RotateTransform3D(new AxisAngleRotation3D(axis, _yaw));
        var pitch = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), _pitch));
        var group = new Transform3DGroup();
        group.Children.Add(yaw);
        group.Children.Add(pitch);

        foreach (var child in _viewport.Children)
            if (child is ModelVisual3D { Content: GeometryModel3D gm })
                gm.Transform = group;
    }
}
