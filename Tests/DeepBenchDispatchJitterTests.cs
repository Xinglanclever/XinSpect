using Xunit;

namespace XinSpect.Tests;

public class DeepBenchDispatchJitterTests
{
    [Theory]
    [InlineData(DeepBenchRunProfile.Quick, 32)]
    [InlineData(DeepBenchRunProfile.Full, 128)]
    public void Workload設定符合DispatchJitter深測介面(DeepBenchRunProfile profile, int samples)
    {
        var workload = GpuDispatchJitterService.GetWorkload(profile);

        Assert.Equal(samples, workload.Samples);
        Assert.Equal(16, workload.DispatchesPerSample);
    }

    [Fact]
    public void 百分位使用最近排名而不是數學平均()
    {
        double[] values = [10, 20, 30, 40];

        Assert.Equal(10, GpuDispatchJitterService.Percentile(values, 0));
        Assert.Equal(20, GpuDispatchJitterService.Percentile(values, 50));
        Assert.Equal(40, GpuDispatchJitterService.Percentile(values, 95));
        Assert.Equal(40, GpuDispatchJitterService.Percentile(values, 100));
    }

    [Fact]
    public async Task 成功量測保留原始樣本並導出百分位與抖動()
    {
        var engine = new FakeDispatchEngine();
        var service = new GpuDispatchJitterService(engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(1, engine.Calls);
        DeepBenchMetric raw = result.Metrics.Single(metric => metric.Id == "gpu.dispatch.sync-latency-us");
        Assert.Equal(32, raw.Samples.Count);
        Assert.Equal(20, result.Metrics.Single(metric => metric.Id == "gpu.dispatch.latency-p50-us").Samples.Single());
        Assert.Equal(31, result.Metrics.Single(metric => metric.Id == "gpu.dispatch.latency-p95-us").Samples.Single());
        Assert.Equal(11, result.Metrics.Single(metric => metric.Id == "gpu.dispatch.jitter-p95-minus-p50-us").Samples.Single());
        Assert.Contains("Fake Hardware", result.Configuration, StringComparison.Ordinal);
        Assert.Contains("16 dispatch", result.Configuration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WARP或FeatureLevel不足整場拒收()
    {
        var warp = new GpuDispatchJitterService(new FakeDispatchEngine { AdapterName = "Microsoft Basic Render Driver" });
        DeepBenchTestResult warpResult = await warp.RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.Unsupported, warpResult.FailureKind);

        var old = new GpuDispatchJitterService(new FakeDispatchEngine { FeatureLevel = 0x0A01 });
        DeepBenchTestResult oldResult = await old.RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.Unsupported, oldResult.FailureKind);
    }

    [Fact]
    public async Task 樣本數不符或非正數延遲整場拒收()
    {
        var count = new GpuDispatchJitterService(new FakeDispatchEngine { SampleCount = 1 });
        DeepBenchTestResult countResult = await count.RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.Unstable, countResult.FailureKind);
        Assert.Contains("樣本數不符", countResult.Error, StringComparison.Ordinal);

        var latency = new GpuDispatchJitterService(new FakeDispatchEngine { LatenciesOverride = [10, -1] });
        DeepBenchTestResult latencyResult = await latency.RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.Unstable, latencyResult.FailureKind);
        Assert.Contains("非有限或非正數", latencyResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sentinel或Checksum錯誤整場拒收()
    {
        var sentinel = new GpuDispatchJitterService(new FakeDispatchEngine { Sentinel = 0 });
        DeepBenchTestResult sentinelResult = await sentinel.RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.Unstable, sentinelResult.FailureKind);

        var checksum = new GpuDispatchJitterService(new FakeDispatchEngine { Checksum = 0xDEADBEEFu });
        DeepBenchTestResult checksumResult = await checksum.RunAsync(CreateContext(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.Unstable, checksumResult.FailureKind);
    }

    [Fact]
    public void 誠實限制明示API計時暖機與驗證界線()
    {
        string[] limitations = GpuDispatchJitterService.Limitations;

        Assert.Contains(limitations, text => text.Contains("Flush", StringComparison.Ordinal) && text.Contains("驅動內部", StringComparison.Ordinal));
        Assert.Contains(limitations, text => text.Contains("8 次未列入", StringComparison.Ordinal));
        Assert.Contains(limitations, text => text.Contains("sentinel", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(limitations, text => text.Contains("frame time", StringComparison.OrdinalIgnoreCase));
    }

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private sealed class FakeDispatchEngine : IGpuDispatchJitterEngine
    {
        public int Calls;
        public string AdapterName { get; init; } = "Fake Hardware";
        public uint FeatureLevel { get; init; } = 0x0B000;
        public int SampleCount { get; init; } = 32;
        public uint Sentinel { get; init; } = GpuDispatchJitterService.Sentinel;
        public uint Checksum { get; init; } = GpuDispatchJitterService.ExpectedChecksum();
        public double[]? LatenciesOverride { get; init; }

        public Task<GpuDispatchJitterRun> MeasureAsync(
            GpuDispatchJitterWorkload workload,
            CancellationToken cancellationToken)
        {
            Calls++;
            double[] latencies = LatenciesOverride ?? [20, 31];
            var samples = Enumerable.Range(0, SampleCount)
                .Select(index => new GpuDispatchJitterSample(latencies[index % latencies.Length], Checksum, Sentinel))
                .ToArray();
            return Task.FromResult(new GpuDispatchJitterRun(
                AdapterName,
                FeatureLevel,
                workload.DispatchesPerSample,
                unchecked((nuint)(8L * 1024 * 1024 * 1024)),
                samples));
        }
    }
}
