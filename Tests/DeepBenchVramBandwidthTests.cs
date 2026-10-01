using Xunit;

namespace XinSpect.Tests;

public class DeepBenchVramBandwidthTests
{
    private const long NominalBufferBytes = 256L * 1024 * 1024;
    private const uint XorKey = 0x5A5A5A5Au;

    [Fact]
    public void ShaderSource會編譯且包含必要的串流XOR核心()
    {
        D3D11ShaderCompilation compilation = D3D11Native.CompileShader(
            GpuVramBandwidthService.HlslSource, "CSMain", "cs_5_0");

        Assert.True(compilation.Succeeded, compilation.Error);
        Assert.True(compilation.BytecodeLength > 0);
        Assert.Contains("RWStructuredBuffer<uint>", GpuVramBandwidthService.HlslSource, StringComparison.Ordinal);
        Assert.Contains("[numthreads(256,1,1)]", GpuVramBandwidthService.HlslSource, StringComparison.Ordinal);
        Assert.Contains("^ 0x5A5A5A5Au", GpuVramBandwidthService.HlslSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeepBenchRunProfile.Quick, 2, 3)]
    [InlineData(DeepBenchRunProfile.Full, 5, 5)]
    public void Workload設定符合串流頻寬深測介面(DeepBenchRunProfile profile, int samples, int passes)
    {
        var workload = GpuVramBandwidthService.GetWorkload(profile);

        Assert.Equal(samples, workload.Samples);
        Assert.Equal(passes, workload.PassesPerSample);
        Assert.Equal(NominalBufferBytes, workload.BufferBytes);
        Assert.Equal(NominalBufferBytes / 4, workload.ElementCount);
        Assert.Equal(4096, workload.DispatchX);
        Assert.Equal(64, workload.DispatchY);
        Assert.Equal(256, workload.ThreadGroupSize);
    }

    [Fact]
    public async Task 成功量測保留每輪頻寬延遲與驗證視窗()
    {
        var engine = new FakeVramEngine();
        var service = new GpuVramBandwidthService(engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(1, engine.Calls);
        DeepBenchMetric bandwidth = result.Metrics.Single(metric => metric.Id == "gpu.vram.bandwidth-gbps");
        DeepBenchMetric latency = result.Metrics.Single(metric => metric.Id == "gpu.vram.pass-latency-ms");
        Assert.Equal(2, bandwidth.Samples.Count);
        Assert.Equal(2, latency.Samples.Count);
        Assert.True(bandwidth.HigherIsBetter);
        Assert.False(latency.HigherIsBetter);
        Assert.Contains("Fake Hardware", result.Configuration, StringComparison.Ordinal);
        Assert.Contains("cs_5_0", result.Configuration, StringComparison.Ordinal);
        Assert.Contains("256 MiB", result.Configuration, StringComparison.Ordinal);
        Assert.Contains("3 passes", result.Configuration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WARP結果永不會被接受為硬體GPU()
    {
        var service = new GpuVramBandwidthService(
            new FakeVramEngine { AdapterName = "Microsoft Basic Render Driver" });

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Contains("WARP", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Metrics);
    }

    [Fact]
    public async Task 驅動移除會歸類為DriverRejected()
    {
        var service = new GpuVramBandwidthService(
            new FakeVramEngine { Throw = new GpuDeviceRemovedException(0x887A0006) });

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.DriverRejected, result.FailureKind);
        Assert.Contains("0x887A0006", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 非有限或非正數頻寬整場拒收()
    {
        var service = new GpuVramBandwidthService(new FakeVramEngine
        {
            CustomSamples =
            [
                new(double.NaN, 5, 123, GpuVramBandwidthService.ExpectedWindow(0, 3)),
                new(180, 5, 123, GpuVramBandwidthService.ExpectedWindow(1, 3)),
            ],
        });

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("非有限或非正數", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 驗證視窗與CPU參考不符時整場拒收()
    {
        var wrongWindow = Enumerable.Repeat(0xDEADBEEFu, GpuVramBandwidthService.WindowLength).ToArray();
        var service = new GpuVramBandwidthService(new FakeVramEngine
        {
            CustomSamples =
            [
                new(180, 5, 123, wrongWindow),
                new(180, 5, 123, GpuVramBandwidthService.ExpectedWindow(1, 3)),
            ],
        });

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("CPU 參考", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 預期視窗由初始零與XOR次數決定()
    {
        uint[] afterOddPasses = GpuVramBandwidthService.ExpectedWindow(0, 3);
        uint[] afterEvenPasses = GpuVramBandwidthService.ExpectedWindow(1, 3);

        Assert.All(afterOddPasses, value => Assert.Equal(XorKey, value));
        Assert.All(afterEvenPasses, value => Assert.Equal(0u, value));
        Assert.Equal(GpuVramBandwidthService.WindowLength, afterOddPasses.Length);
    }

    [Fact]
    public void 誠實限制不宣稱內部計時器且排除WARP()
    {
        string[] limitations = GpuVramBandwidthService.Limitations;

        Assert.Contains(limitations, text => text.Contains("dispatch", StringComparison.OrdinalIgnoreCase) && text.Contains("同步", StringComparison.Ordinal));
        Assert.DoesNotContain(limitations, text => text.Contains("驅動內部", StringComparison.Ordinal));
        Assert.Contains(limitations, text => text.Contains("WARP 已排除", StringComparison.Ordinal));
        Assert.Contains(limitations, text => text.Contains("共用記憶體", StringComparison.Ordinal));
    }

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private sealed class FakeVramEngine : IGpuVramBandwidthEngine
    {
        public int Calls;
        public string AdapterName { get; set; } = "Fake Hardware";
        public Exception? Throw { get; init; }
        public IReadOnlyList<GpuVramBandwidthSample>? CustomSamples { get; init; }

        public Task<GpuVramBandwidthRun> MeasureAsync(
            GpuVramBandwidthWorkload workload,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            if (CustomSamples is not null)
            {
                return Task.FromResult(new GpuVramBandwidthRun(AdapterName, 0x0B000, workload.BufferBytes, CustomSamples));
            }

            var samples = new List<GpuVramBandwidthSample>();
            for (int index = 0; index < workload.Samples; index++)
            {
                samples.Add(new GpuVramBandwidthSample(
                    180 + index * 5,
                    5 + index * 0.5,
                    123 + (uint)index,
                    GpuVramBandwidthService.ExpectedWindow(index, workload.PassesPerSample)));
            }

            return Task.FromResult(new GpuVramBandwidthRun(AdapterName, 0x0B000, workload.BufferBytes, samples));
        }
    }
}
