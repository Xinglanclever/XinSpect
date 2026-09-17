using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
namespace XinSpect;

public sealed class GpuRenderTestService : ObservableObject
{
    // ── observable state ──────────────────────────────────────
    bool   _isRunning;
    string _phase = "";
    double _progressFraction;
    string _statusLine = "";
    double? _compositeScore;

    public bool   IsRunning        { get => _isRunning;        private set { SetProperty(ref _isRunning, value); OnPropertyChanged(nameof(CanStart)); } }
    public bool   CanStart         => !IsRunning;
    public string Phase            { get => _phase;            private set => SetProperty(ref _phase, value); }
    public double ProgressFraction { get => _progressFraction; private set { SetProperty(ref _progressFraction, value); OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent  => ProgressFraction * 100.0;
    public string StatusLine       { get => _statusLine;       private set => SetProperty(ref _statusLine, value); }
    public double? CompositeScore  { get => _compositeScore;   private set { SetProperty(ref _compositeScore, value); OnPropertyChanged(nameof(CompositeText)); } }
    public string CompositeText    => CompositeScore is double s ? $"{s:0}" : "—";

    public ObservableCollection<RenderTestResult> Results { get; } = new();

    // ── lifecycle ─────────────────────────────────────────────
    CancellationTokenSource? _cts;

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    // ── main pipeline ─────────────────────────────────────────
    async Task RunAsync(CancellationToken ct)
    {
        IsRunning = true;
        Results.Clear();
        CompositeScore = null;
        StatusLine = "";

        try
        {
            // Phase 1 ── Fill Rate
            Phase = "填充率測試 (1/3)";
            double fillFps = await RunFillTestAsync(ct, p => ProgressFraction = p * 0.333);
            Results.Add(new RenderTestResult { Name = "填充率", AverageFps = fillFps, Score = fillFps * 0.4 });
            StatusLine = $"填充率: {fillFps:0.0} FPS";

            // Phase 2 ── 3D Geometry
            Phase = "3D 幾何測試 (2/3)";
            double geoFps = await RunGeometryTestAsync(ct, p => ProgressFraction = 0.333 + p * 0.333);
            Results.Add(new RenderTestResult { Name = "3D 幾何", AverageFps = geoFps, Score = geoFps * 0.35 });
            StatusLine = $"3D 幾何: {geoFps:0.0} FPS";

            // Phase 3 ── Text Rendering
            Phase = "文字渲染測試 (3/3)";
            double txtFps = await RunTextTestAsync(ct, p => ProgressFraction = 0.666 + p * 0.334);
            Results.Add(new RenderTestResult { Name = "文字渲染", AverageFps = txtFps, Score = txtFps * 0.25 });
            StatusLine = $"文字渲染: {txtFps:0.0} FPS";

            // Composite
            double composite = Math.Round(fillFps * 0.4 + geoFps * 0.35 + txtFps * 0.25);
            CompositeScore = composite;
            ProgressFraction = 1.0;
            Phase = "完成";
            StatusLine = $"綜合分數: {composite:0}";
        }
        catch (OperationCanceledException)
        {
            Phase = "已取消";
            StatusLine = "使用者取消了測試。";
        }
        catch (Exception ex)
        {
            Phase = "錯誤";
            StatusLine = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    // ── Test 1: Fill Rate ─────────────────────────────────────
    // RenderTargetBitmap.Render() 必須在 UI 執行緒上跑，搬到背景執行緒會拋例外。
    // 但不能用一個 5 秒的 while 迴圈把 Dispatcher 整個佔住——取消按鈕按不到、進度條不會動。
    // 所以每隔幾幀就 Yield 一次，讓 Dispatcher 處理輸入與重繪。
    static async Task<double> RunFillTestAsync(CancellationToken ct, Action<double> report)
    {
        const int RectCount = 500;
        const double Seconds = 5.0;
        int w = 800, h = 600;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var rng = new Random(42);
        var sw  = Stopwatch.StartNew();
        int frames = 0;

        while (sw.Elapsed.TotalSeconds < Seconds)
        {
            ct.ThrowIfCancellationRequested();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                for (int i = 0; i < RectCount; i++)
                {
                    var brush = new SolidColorBrush(
                        Color.FromArgb(128,
                            (byte)rng.Next(256),
                            (byte)rng.Next(256),
                            (byte)rng.Next(256)));
                    brush.Freeze();
                    dc.DrawRectangle(brush, null,
                        new Rect(rng.Next(w), rng.Next(h),
                                 rng.Next(50, 200), rng.Next(50, 200)));
                }
            }
            rtb.Render(dv);
            frames++;
            if (frames % 5 == 0)
            {
                report(sw.Elapsed.TotalSeconds / Seconds);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        return Math.Round(frames / Math.Max(0.001, sw.Elapsed.TotalSeconds), 1);
    }

    // ── Test 2: 3D Geometry ───────────────────────────────────
    static async Task<double> RunGeometryTestAsync(CancellationToken ct, Action<double> report)
    {
        const double Seconds = 5.0;
        int w = 800, h = 600;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);

        var mesh = BuildSphereMesh(3);
        var material = new DiffuseMaterial(Brushes.CornflowerBlue);
        material.Freeze();
        var model = new GeometryModel3D(mesh, material);
        var group = new Model3DGroup();
        group.Children.Add(model);
        group.Children.Add(new AmbientLight(Colors.DarkGray));
        group.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -1, -1)));

        var visual = new ModelVisual3D { Content = group };
        var camera = new PerspectiveCamera(
            new Point3D(0, 0, 3), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 60);

        var viewport = new Viewport3D
        {
            Camera = camera,
            Width = w,
            Height = h
        };
        viewport.Children.Add(visual);
        viewport.Measure(new Size(w, h));
        viewport.Arrange(new Rect(0, 0, w, h));

        var sw = Stopwatch.StartNew();
        int frames = 0;
        double angle = 0;

        while (sw.Elapsed.TotalSeconds < Seconds)
        {
            ct.ThrowIfCancellationRequested();
            angle += 2.0;
            model.Transform = new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(0, 1, 0), angle));
            viewport.UpdateLayout();
            rtb.Render(viewport);
            frames++;
            if (frames % 5 == 0)
            {
                report(sw.Elapsed.TotalSeconds / Seconds);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        return Math.Round(frames / Math.Max(0.001, sw.Elapsed.TotalSeconds), 1);
    }

    // ── Test 3: Text Rendering ────────────────────────────────
    static async Task<double> RunTextTestAsync(CancellationToken ct, Action<double> report)
    {
        const int TextCount = 200;
        const double Seconds = 5.0;
        int w = 800, h = 600;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var rng = new Random(123);
        var typeface = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;
        if (dpi <= 0) dpi = 1.0;

        var sw = Stopwatch.StartNew();
        int frames = 0;

        while (sw.Elapsed.TotalSeconds < Seconds)
        {
            ct.ThrowIfCancellationRequested();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                for (int i = 0; i < TextCount; i++)
                {
                    double em = 8 + rng.Next(29);
                    var ft = new FormattedText(
                        $"XinSpect 繪圖測試 {i}",
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        typeface, em, Brushes.Black, dpi);
                    dc.DrawText(ft, new Point(rng.Next(w), rng.Next(h)));
                }
            }
            rtb.Render(dv);
            frames++;
            if (frames % 5 == 0)
            {
                report(sw.Elapsed.TotalSeconds / Seconds);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        return Math.Round(frames / Math.Max(0.001, sw.Elapsed.TotalSeconds), 1);
    }

    // ── Sphere mesh builder ───────────────────────────────────
    static MeshGeometry3D BuildSphereMesh(int subdivisions)
    {
        // Start with an octahedron and subdivide
        var pts = new System.Collections.Generic.List<Point3D>
        {
            new( 0,  1,  0),  // 0  top
            new( 1,  0,  0),  // 1
            new( 0,  0,  1),  // 2
            new(-1,  0,  0),  // 3
            new( 0,  0, -1),  // 4
            new( 0, -1,  0),  // 5  bottom
        };
        var tris = new System.Collections.Generic.List<(int, int, int)>
        {
            (0,1,2),(0,2,3),(0,3,4),(0,4,1),
            (5,2,1),(5,3,2),(5,4,3),(5,1,4),
        };

        var midCache = new System.Collections.Generic.Dictionary<long, int>();

        int GetMid(int a, int b)
        {
            long key = Math.Min(a, b) * 100000L + Math.Max(a, b);
            if (midCache.TryGetValue(key, out int idx)) return idx;
            var p = new Point3D(
                (pts[a].X + pts[b].X) / 2,
                (pts[a].Y + pts[b].Y) / 2,
                (pts[a].Z + pts[b].Z) / 2);
            double len = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            pts.Add(new Point3D(p.X / len, p.Y / len, p.Z / len));
            idx = pts.Count - 1;
            midCache[key] = idx;
            return idx;
        }

        for (int s = 0; s < subdivisions; s++)
        {
            var next = new System.Collections.Generic.List<(int, int, int)>();
            midCache.Clear();
            foreach (var (i0, i1, i2) in tris)
            {
                int a = GetMid(i0, i1), b = GetMid(i1, i2), c = GetMid(i2, i0);
                next.Add((i0, a, c));
                next.Add((a, i1, b));
                next.Add((c, b, i2));
                next.Add((a, b, c));
            }
            tris = next;
        }

        var geo = new MeshGeometry3D();
        foreach (var p in pts) { geo.Positions.Add(p); geo.Normals.Add(new Vector3D(p.X, p.Y, p.Z)); }
        foreach (var (i0, i1, i2) in tris) { geo.TriangleIndices.Add(i0); geo.TriangleIndices.Add(i1); geo.TriangleIndices.Add(i2); }
        geo.Freeze();
        return geo;
    }
}
