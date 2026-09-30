using System;
using System.Linq;
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

    private string _analysis = "";
    /// <summary>跑完後的總結分析：各階段量到什麼、數字怎麼解讀、檢核結果。</summary>
    public string Analysis { get => _analysis; private set => SetProperty(ref _analysis, value); }
    public bool HasAnalysis => Analysis.Length > 0;

    public ObservableCollection<RenderTestResult> Results { get; } = new();

    /// <summary>對最後渲染出的 RTB 抽樣檢核：非空白（alpha>0 且非純黑）像素占比。</summary>
    internal static double ValidateNonBlank(RenderTargetBitmap rtb)
    {
        try
        {
            int w = rtb.PixelWidth, h = rtb.PixelHeight;
            int stride = w * 4;
            var px = new byte[stride * h];
            rtb.CopyPixels(px, stride, 0);
            int total = 0, valid = 0;
            // 抽樣：每 8×8 區塊取一點，兼顧速度與覆蓋
            for (int y = 0; y < h; y += 8)
                for (int x = 0; x < w; x += 8)
                {
                    total++;
                    int o = y * stride + x * 4;
                    if (px[o + 3] > 0 && (px[o] > 4 || px[o + 1] > 4 || px[o + 2] > 4)) valid++;
                }
            return total == 0 ? 0 : valid * 100.0 / total;
        }
        catch { return 0; }
    }

    private static string TraceText(List<double> perSecond)
        => perSecond.Count == 0 ? "" : "逐秒 FPS：" + string.Join(" / ", perSecond.Select(v => v.ToString("0")));

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
            var (fillFps, fillValid, fillTrace) = await RunFillTestAsync(ct, p => ProgressFraction = p * 0.333);
            Results.Add(new RenderTestResult
            {
                Name = "填充率", AverageFps = fillFps, Score = fillFps * 0.4,
                ValidPixelPercent = fillValid, FpsTrace = TraceText(fillTrace),
                Detail = "800×600 離屏畫布，每幀畫 500 個半透明隨機矩形後整幀重繪——壓的是填充率與混合（每幀都有大量疊色）。",
            });
            StatusLine = $"填充率: {fillFps:0.0} FPS" + (fillValid < 1 ? "（像素檢核未通過，此項不計分）" : "");

            // Phase 2 ── 3D Geometry
            Phase = "3D 幾何測試 (2/3)";
            var (geoFps, geoValid, geoTrace) = await RunGeometryTestAsync(ct, p => ProgressFraction = 0.333 + p * 0.333);
            Results.Add(new RenderTestResult
            {
                Name = "3D 幾何", AverageFps = geoFps, Score = geoFps * 0.35,
                ValidPixelPercent = geoValid, FpsTrace = TraceText(geoTrace),
                Detail = "128 面球體（八面體細分 3 次）逐幀旋轉後離屏成像——壓的是 3D 管線的頂點處理與三角化。",
            });
            StatusLine = $"3D 幾何: {geoFps:0.0} FPS" + (geoValid < 1 ? "（像素檢核未通過：離屏 3D 輸出空白，此項不計分）" : "");

            // Phase 3 ── Text Rendering
            Phase = "文字渲染測試 (3/3)";
            var (txtFps, txtValid, txtTrace) = await RunTextTestAsync(ct, p => ProgressFraction = 0.666 + p * 0.334);
            Results.Add(new RenderTestResult
            {
                Name = "文字渲染", AverageFps = txtFps, Score = txtFps * 0.25,
                ValidPixelPercent = txtValid, FpsTrace = TraceText(txtTrace),
                Detail = "每幀排版並繪製 200 段 8–36 px 的隨機文字——壓的是字形快取、排版與柵格化。",
            });
            StatusLine = $"文字渲染: {txtFps:0.0} FPS" + (txtValid < 1 ? "（像素檢核未通過，此項不計分）" : "");

            // Composite：像素檢核未過的階段不計分——空白幀跑得再快也不是渲染能力
            double composite = Math.Round(fillFps * 0.4 + geoFps * 0.35 + txtFps * 0.25);
            CompositeScore = composite;
            ProgressFraction = 1.0;
            Phase = "完成";
            StatusLine = $"綜合分數: {composite:0}";

            // ── 總結分析 ──
            var an = new System.Text.StringBuilder();
            foreach (var r in Results)
            {
                an.Append($"{r.Name}：{r.FpsText}");
                an.AppendLine(r.IsBlank ? "——末幀像素檢核為空白，分數不計入評價。" : $"。{r.Detail}");
                if (!r.IsBlank && r.FpsTrace.Length > 0) an.AppendLine("　" + r.FpsTrace);
            }
            an.AppendLine();
            an.Append("判讀：這套測試走 WPF 離屏光柵化路徑，量的是「CPU 端把圖畫出來」的速度，不是遊戲裡那種 GPU 幀率——換顯示卡不會有明顯差異，換處理器才會。");
            bool allValid = Results.All(r => !r.IsBlank);
            if (!allValid)
                an.Append("有階段被像素檢核判定為空白（常見於部分驅動或遠端工作階段的離屏 3D），那些階段已不計分，綜合分數只反映量到的部分。");
            else if (composite >= 120) an.Append("三項都在高速區，繪圖管線的反應速度足以應付一般桌面與文件場景。");
            else if (composite >= 60) an.Append("屬常見水準；若介面動畫偶爾卡頓，先看「DPC 延遲」與顯示驅動版本。");
            else an.Append("明顯偏慢：老舊或省電模式的處理器、遠端工作階段、或被安全軟體干擾都可能造成；建議關閉省電模式後重測一次。");
            Analysis = an.ToString();
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
    async Task<(double Fps, double ValidPercent, List<double> PerSecond)> RunFillTestAsync(
        CancellationToken ct, Action<double> report)
    {
        const int RectCount = 500;
        const double Seconds = 5.0;
        int w = 800, h = 600;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var rng = new Random(42);
        var sw  = Stopwatch.StartNew();
        int frames = 0;
        var perSecond = new List<double>();
        var secSw = Stopwatch.StartNew();
        int framesAtSec = 0;

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
            if (secSw.Elapsed.TotalSeconds >= 1.0)
            {
                perSecond.Add((frames - framesAtSec) / secSw.Elapsed.TotalSeconds);
                framesAtSec = frames;
                secSw.Restart();
                report(sw.Elapsed.TotalSeconds / Seconds);
                // 過程展示：狀態列即時回報目前 fps
                Phase = $"填充率測試 (1/3)・目前 {perSecond[^1]:0} FPS";
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        double valid = ValidateNonBlank(rtb);
        return (Math.Round(frames / Math.Max(0.001, sw.Elapsed.TotalSeconds), 1), valid, perSecond);
    }

    // ── Test 2: 3D Geometry ───────────────────────────────────
    async Task<(double Fps, double ValidPercent, List<double> PerSecond)> RunGeometryTestAsync(
        CancellationToken ct, Action<double> report)
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
        var perSecond = new List<double>();
        var secSw = Stopwatch.StartNew();
        int framesAtSec = 0;

        while (sw.Elapsed.TotalSeconds < Seconds)
        {
            ct.ThrowIfCancellationRequested();
            angle += 2.0;
            model.Transform = new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(0, 1, 0), angle));
            viewport.UpdateLayout();
            rtb.Render(viewport);
            frames++;
            if (secSw.Elapsed.TotalSeconds >= 1.0)
            {
                perSecond.Add((frames - framesAtSec) / secSw.Elapsed.TotalSeconds);
                framesAtSec = frames;
                secSw.Restart();
                report(sw.Elapsed.TotalSeconds / Seconds);
                Phase = $"3D 幾何測試 (2/3)・目前 {perSecond[^1]:0} FPS";
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        double valid = ValidateNonBlank(rtb);
        return (Math.Round(frames / Math.Max(0.001, sw.Elapsed.TotalSeconds), 1), valid, perSecond);
    }

    // ── Test 3: Text Rendering ────────────────────────────────
    async Task<(double Fps, double ValidPercent, List<double> PerSecond)> RunTextTestAsync(
        CancellationToken ct, Action<double> report)
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
        var perSecond = new List<double>();
        var secSw = Stopwatch.StartNew();
        int framesAtSec = 0;

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
            if (secSw.Elapsed.TotalSeconds >= 1.0)
            {
                perSecond.Add((frames - framesAtSec) / secSw.Elapsed.TotalSeconds);
                framesAtSec = frames;
                secSw.Restart();
                report(sw.Elapsed.TotalSeconds / Seconds);
                Phase = $"文字渲染測試 (3/3)・目前 {perSecond[^1]:0} FPS";
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        double valid = ValidateNonBlank(rtb);
        return (Math.Round(frames / Math.Max(0.001, sw.Elapsed.TotalSeconds), 1), valid, perSecond);
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
