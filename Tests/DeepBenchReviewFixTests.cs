using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Phase 1 終審（C1/C2/C3/I1/I2）修復的回歸測試。</summary>
public class DeepBenchReviewFixTests
{
    // ── C1：檢視必須把 DataContext 指到 DeepBench 子模型，否則整頁繫結全空 ──

    [Fact]
    public void 深測頁根節點必須繫結DeepBench子模型()
    {
        Assert.True(XinSpect.DeepBenchViewProbe.BindsToDeepBenchContext(),
            "DeepBenchView 根節點缺少 DataContext={Binding DeepBench}；頁面會整頁空白。");
    }

    [Fact]
    public void 深測頁載入後以MainViewModel為DataContext可解析出NoScoreNotice()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // 主程式的樣式（Card/SectionHead 等）定義在 App.xaml 合併的 Theme.xaml；
                // 測試環境沒有 Application，先掛上來，XAML 才解析得動。
                // XamlReader.Load 不會解析 xmlns:local 的型別，改用 Application.LoadComponent
                // 走完整的 pack URI 載入（主題裡有 local:SeverityToBrushConverter）。
                if (Application.Current is null)
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var theme = new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/XinSpect;component/Themes/Theme.xaml", UriKind.Absolute),
                    };
                    app.Resources.MergedDictionaries.Add(theme);
                }

                var view = new DeepBenchView();
                var main = new MainViewModel();

                // UserControl 的 DataContext 繫結只在「有父項目」時才連接（InheritanceContext）；
                // 直接設 view.DataContext 不會觸發 Root 上的 {Binding DeepBench}，
                // 因此照 MainWindow 的方式：放進 ContentControl 當內容再給 DataContext。
                var host = new ContentControl { Content = view };
                host.DataContext = main;
                host.ApplyTemplate();
                view.ApplyTemplate();
                host.Measure(new Size(1200, 800));
                host.Arrange(new Rect(0, 0, 1200, 800));
                host.UpdateLayout();

                // 繫結走 dispatcher；推到 ApplicationIdle 確保資料流完成
                var idleFrame = new System.Windows.Threading.DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                    (Action)(() => idleFrame.Continue = false));
                Dispatcher.PushFrame(idleFrame);
                host.UpdateLayout();

                var text = FindTextBlock(host, "NoScoreNoticeText");
                Assert.NotNull(text);
                var expression = BindingOperations.GetBindingExpression(text, TextBlock.TextProperty);
                Assert.NotNull(expression);
                // DataContext 已由 Root 的 {Binding DeepBench} 換成子模型，值才拿得到
                Assert.IsType<DeepBenchViewModel>(text.DataContext);
                Assert.Equal(DeepBenchViewModel.NoScoreNotice, text.Text);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    // ── C2：CSSetShader（slot 69）簽名必須是 4 參數，不是 CSSetUAV 的 6 參數形狀 ──

    [Fact]
    public void D3D11CSSetShader委派簽名必須是4參數()
    {
        Assert.True(XinSpect.D3D11NativeProbe.HasCorrectCssSetShaderSignature(),
            "D3D11Native 的 CSSetShader 委派簽名錯誤（多傳了 2 個參數），會把 shader 指標傳進 NumClassInstances 造成 native crash。");
    }

    // ── C3：全零輸出必須以「值」判斷，FNV-1a checksum 對全零資料永遠非零 ──

    [Fact]
    public async Task 全零輸出以值判定而不是checksum()
    {
        // 2048 個零經 FNV-1a 雜湊永遠不為零——靠 checksum==0 擋全零永遠擋不到；
        // 值必須真的全是零，但 checksum 是「看起來健康」的非零值。
        float[] zeros = new float[2048];
        var engine = new FakeGpuEngineWithValueSamples([
            new GpuFp32Sample(3000, 0.2, 2166136261u, zeros),
            new GpuFp32Sample(3000, 0.2, 2166136261u, zeros)]);
        var service = new GpuFp32ComputeService(engine);

        var result = await service.RunAsync(
            new DeepBenchRunContext(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>()),
            CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("全零", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 非零且符合CPU期望值的輸出被接受()
    {
        // 和 shader 等價的 CPU 參考計算：gid=0..7 的期望值陣列
        float[] expected = Enumerable.Range(0, 8).Select(g => ComputeExpected((uint)g)).ToArray();
        var engine = new FakeGpuEngineWithValueSamples([
            new GpuFp32Sample(3000, 0.2, 123u, expected),
            new GpuFp32Sample(3100, 0.19, 124u, expected)]);
        var service = new GpuFp32ComputeService(engine);

        var result = await service.RunAsync(
            new DeepBenchRunContext(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>()),
            CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
    }

    // ── I1：真實測項取消是「回傳 Cancelled 結果」不是丟例外；orchestrator 要據此停止並記 Cancelled ──

    [Fact]
    public async Task 測項回傳取消結果時Orchestrator停止並記為Cancelled()
    {
        var first = new FakeDeepBenchTest("first", (context, _) => Task.FromResult(SuccessResult("first", context.SessionId)));
        var returnsCancelled = new FakeDeepBenchTest("second", (context, _) => Task.FromResult(new DeepBenchTestResult(
            "second", context.SessionId, context.Profile, DateTime.UtcNow, DateTime.UtcNow, "已取消", [], [],
            ["取消前不輸出未完成樣本。"], DeepBenchFailureKind.Cancelled, "使用者取消。")));
        var never = new FakeDeepBenchTest("never", (context, _) => Task.FromResult(SuccessResult("never", context.SessionId)));

        var record = await new DeepBenchOrchestrator([first, returnsCancelled, never]).RunAsync(
            DeepBenchRunProfile.Quick, ["first", "second", "never"]);

        Assert.Equal(DeepBenchRunState.Cancelled, record.State);
        Assert.Equal(2, record.Results.Count);
        Assert.Equal(0, never.Calls);
    }

    // ── I2：儲存擴展性比例必須同軸同單位（同 metric id、同 blockBytes），不得混 IOPS/MiB/s/µs ──

    [Fact]
    public void 儲存擴展性比例只用同指標同區塊大小的樣本()
    {
        Guid session = Guid.NewGuid();
        var storage = new DeepBenchTestResult(
            "storage.qd-ladder", session, DeepBenchRunProfile.Quick,
            DateTime.UtcNow, DateTime.UtcNow, "test",
            [
                new DeepBenchMetric(
                    "storage.qd.read.iops", "隨機讀 IOPS", "IOPS", true, "test",
                    [],
                    [
                        new DeepBenchMetricPoint(1000, new Dictionary<string, string> { ["blockBytes"] = "4096", ["queueDepth"] = "1" }, [1000]),
                        new DeepBenchMetricPoint(90000, new Dictionary<string, string> { ["blockBytes"] = "4096", ["queueDepth"] = "16" }, [90000]),
                        // 不同單位的指標（MiB/s）與不同區塊（128K）不得混入同一比例
                        new DeepBenchMetricPoint(700, new Dictionary<string, string> { ["blockBytes"] = "4096", ["queueDepth"] = "1" }, [700]),
                        new DeepBenchMetricPoint(95000, new Dictionary<string, string> { ["blockBytes"] = "131072", ["queueDepth"] = "1" }, [95000]),
                    ]),
            ],
            [], [], DeepBenchFailureKind.None, null);
        var record = new DeepBenchRunRecord(
            session, DeepBenchRunProfile.Quick, DateTime.UtcNow, DateTime.UtcNow,
            DeepBenchRunState.Completed, [storage], []);

        IReadOnlyList<DeepBenchInsight> insights = DeepBenchCrossDomainSynthesis.Summarize(session, [storage]);

        DeepBenchInsight? scaling = insights.FirstOrDefault(i => i.Title.Contains("佇列擴展", StringComparison.Ordinal));
        Assert.NotNull(scaling);
        Assert.Contains("隨機讀 IOPS", scaling.Text, StringComparison.Ordinal);
        Assert.Contains("4 KiB", scaling.Text, StringComparison.Ordinal);
        Assert.Contains("QD1 到 QD16", scaling.Text, StringComparison.Ordinal);
        // 4K 同軸：QD1 平均 (1000+700)/2=850，最佳 QD16=90000 → 105.88×；
        // 128K 樣本（95000）若混入會變成 95000/850≈111.76×
        Assert.Contains("105.88", scaling.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("111", scaling.Text, StringComparison.Ordinal);
    }

    // ── 共用 ──

    private static DeepBenchTestResult SuccessResult(string testId, Guid session) => new(
        testId, session, DeepBenchRunProfile.Quick, DateTime.UtcNow, DateTime.UtcNow, "test",
        [new DeepBenchMetric("m", "m", "unit", true, "test", [1, 2], [])],
        [], [], DeepBenchFailureKind.None, null);

    private static float ComputeExpected(uint gid)
    {
        float value = gid * 0.00048828125f + 1.0f;
        for (int i = 0; i < 4096; i++) value = value * 1.0000001f + 0.0000001f;
        return value;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static TextBlock? FindTextBlock(DependencyObject root, string bindingPath)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb)
            {
                var expr = BindingOperations.GetBindingExpression(tb, TextBlock.TextProperty);
                if (expr?.ParentBinding.Path.Path == bindingPath) return tb;
            }
            var deeper = FindTextBlock(child, bindingPath);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    private static void WaitForBindings()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Render,
            (Action)(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>以值樣本為主的假 engine；expectedValue 非 null 時代表 readback 值符合 CPU 參考計算。</summary>
    private sealed class FakeGpuEngineWithValueSamples(GpuFp32Sample[] samples) : IGpuFp32ComputeEngine
    {
        public Task<GpuFp32Run> MeasureAsync(GpuFp32Workload workload, CancellationToken cancellationToken)
            => Task.FromResult(new GpuFp32Run("Fake Hardware", 0x0B00, samples));
    }

    private sealed class FakeDeepBenchTest(string id, Func<DeepBenchRunContext, CancellationToken, Task<DeepBenchTestResult>> run) : IDeepBenchTest
    {
        public string Id { get; } = id;
        public int Calls { get; private set; }
        public Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return run(context, cancellationToken);
        }
    }
}
