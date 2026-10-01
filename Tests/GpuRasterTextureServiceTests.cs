using Xunit;

namespace XinSpect.Tests;

public class GpuRasterTextureServiceTests
{
    [Fact]
    public async Task 三情境保留原始樣本並只做明示比率()
    {
        var service = CreateService(CreateMeasurement(gpix: 12.0, ratio8Tap: 24.0));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(
            [
                "gpu.raster.fill.gpix",
                "gpu.raster.texture-single.gpix",
                "gpu.raster.texture-8tap.gpix",
                "gpu.raster.texture-8tap-over-single.ratio",
            ],
            result.Metrics.Select(metric => metric.Id));
        Assert.Equal([12.0, 12.24, 11.76], result.Metrics[0].Samples);
        Assert.Equal(24.0 / 12.0, result.Metrics[3].Samples.Single(), 12);
        string conditions = string.Join('\n', result.Conditions);
        Assert.Contains("checksum", conditions, StringComparison.Ordinal);
        Assert.Contains("CPU 參考", conditions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checksum為零或情境不完整或非正數樣本都整場拒收()
    {
        var zeroChecksum = CreateMeasurement(12.0, 24.0, fillChecksum: 0);
        var incomplete = new GpuRasterMeasurement(
        [
            Scenario("fill", 12.0, 0x11111111u),
            Scenario("texture-single", 12.0, 0x22222222u),
        ]);
        var nonPositive = new GpuRasterMeasurement(
        [
            Scenario("fill", 0, 0x11111111u),
            Scenario("texture-single", 12.0, 0x22222222u),
            Scenario("texture-8tap", 24.0, 0x33333333u),
        ]);
        var wrongOrder = new GpuRasterMeasurement(
        [
            Scenario("texture-single", 12.0, 0x22222222u),
            Scenario("fill", 12.0, 0x11111111u),
            Scenario("texture-8tap", 24.0, 0x33333333u),
        ]);

        foreach (GpuRasterMeasurement measurement in new[] { zeroChecksum, incomplete, nonPositive, wrongOrder })
        {
            DeepBenchTestResult result = await RunAsync(CreateService(measurement), DeepBenchRunProfile.Quick);
            Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
            Assert.Empty(result.Metrics);
        }
    }

    [Fact]
    public async Task WARP或無硬體裝置回Unsupported()
    {
        var service = new GpuRasterTextureService(new ThrowingEngine(new GpuUnsupportedException("偵測到 WARP（Microsoft Basic Render Driver）；本項不輸出硬體 GPU 結果。")));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.NotNull(result.Error);
        Assert.Contains("WARP", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消後不輸出部分場景補值()
    {
        var service = new GpuRasterTextureService(new CancellingEngine());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var context = new DeepBenchRunContext(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());
        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("不輸出部分場景補值", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public void Quick與Full使用不同幀數與樣本數()
    {
        GpuRasterWorkload quick = GpuRasterTextureService.GetWorkload(DeepBenchRunProfile.Quick);
        GpuRasterWorkload full = GpuRasterTextureService.GetWorkload(DeepBenchRunProfile.Full);

        Assert.Equal(1280, quick.Width);
        Assert.Equal(720, quick.Height);
        Assert.Equal(2048, quick.TextureSize);
        Assert.True(full.FramesPerSample > quick.FramesPerSample);
        Assert.True(full.MeasureSamples > quick.MeasureSamples);
        Assert.True(full.WarmupSamples > quick.WarmupSamples);
    }

    [Fact]
    public void 誠實界線明示非遊戲效能與WARP拒收()
    {
        string limitations = string.Join('\n', GpuRasterTextureService.Limitations);

        Assert.Contains("不外推成遊戲效能", limitations, StringComparison.Ordinal);
        Assert.Contains("WARP", limitations, StringComparison.Ordinal);
        Assert.Contains("不是 GPU timestamp", limitations, StringComparison.Ordinal);
        Assert.Contains("不合成單一總分", limitations, StringComparison.Ordinal);
        Assert.Contains("CPU 參考", limitations, StringComparison.Ordinal);
    }

    [Fact]
    public void 四個HLSL進入點都能以真實編譯器編譯()
    {
        foreach ((string entry, string target) in new[]
                 {
                     ("VSMain", "vs_5_0"),
                     ("PSFill", "ps_5_0"),
                     ("PSTextureSingle", "ps_5_0"),
                     ("PSTextureMulti", "ps_5_0"),
                 })
        {
            D3D11ShaderCompilation compilation = D3D11Native.CompileShader(GpuRasterTextureService.HlslSource, entry, target);
            Assert.True(compilation.Succeeded, $"{entry}: {compilation.Error}");
            Assert.NotNull(compilation.Bytecode);
            Assert.True(compilation.Bytecode.Length > 0);
        }
    }

    [Fact]
    public async Task Windows引擎能完成最小光柵實測()
    {
        var workload = new GpuRasterWorkload(320, 240, 256, 3, 0, 1);
        var context = new GpuRasterContext(workload, DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>(), CancellationToken.None);

        GpuRasterMeasurement measurement = await new D3D11RasterEngine().MeasureAsync(context, CancellationToken.None);

        Assert.Equal(3, measurement.Scenarios.Count);
        Assert.All(measurement.Scenarios, scenario =>
        {
            Assert.NotEqual(0u, scenario.Run.ReadbackChecksum);
            Assert.All(scenario.Run.Samples, sample => Assert.True(double.IsFinite(sample.GigapixelsPerSecond) && sample.GigapixelsPerSecond > 0));
        });
    }

    private static GpuRasterTextureService CreateService(GpuRasterMeasurement measurement) =>
        new(new FixedEngine(measurement));

    private static GpuRasterMeasurement CreateMeasurement(double gpix, double ratio8Tap, uint fillChecksum = 0x11111111u) =>
        new(
        [
            Scenario("fill", gpix, fillChecksum),
            Scenario("texture-single", gpix, 0x22222222u),
            Scenario("texture-8tap", ratio8Tap, 0x33333333u),
        ]);

    private static GpuRasterScenarioResult Scenario(string id, double gpix, uint checksum) =>
        new(id, new GpuRasterRun(
            "測試 GPU", 0x0B000, 1280, 720,
            [new GpuRasterSample(gpix, 1.0), new GpuRasterSample(gpix * 1.02, 1.0), new GpuRasterSample(gpix * 0.98, 1.0)],
            checksum));

    private static async Task<DeepBenchTestResult> RunAsync(GpuRasterTextureService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class FixedEngine(GpuRasterMeasurement measurement) : IGpuRasterEngine
    {
        public Task<GpuRasterMeasurement> MeasureAsync(GpuRasterContext context, CancellationToken cancellationToken)
            => Task.FromResult(measurement);
    }

    private sealed class ThrowingEngine(GpuUnsupportedException exception) : IGpuRasterEngine
    {
        public Task<GpuRasterMeasurement> MeasureAsync(GpuRasterContext context, CancellationToken cancellationToken)
            => Task.FromException<GpuRasterMeasurement>(exception);
    }

    private sealed class CancellingEngine : IGpuRasterEngine
    {
        public async Task<GpuRasterMeasurement> MeasureAsync(GpuRasterContext context, CancellationToken cancellationToken)
        {
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new GpuRasterMeasurement([]);
        }
    }
}
