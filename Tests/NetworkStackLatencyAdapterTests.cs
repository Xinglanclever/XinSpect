using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class NetworkStackLatencyAdapterTests
{
    [Fact]
    public void Quick與Full使用固定探測數與暖機數()
    {
        Assert.Equal((256, 16), NetworkStackLatencyAdapter.GetWorkload(DeepBenchRunProfile.Quick));
        Assert.Equal((1024, 32), NetworkStackLatencyAdapter.GetWorkload(DeepBenchRunProfile.Full));
    }

    [Fact]
    public async Task 成功量測保留本機迴路原始延遲()
    {
        var engine = new FakeNetworkLatencyEngine();
        var service = new NetworkStackLatencyAdapter(engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(256, engine.LastProbes);
        Assert.Equal(16, engine.LastWarmup);
        DeepBenchMetric metric = Assert.Single(result.Metrics);
        Assert.Equal("ux.network-stack.loopback-rtt-us", metric.Id);
        Assert.Equal(engine.Samples, metric.Samples);
        string limitations = string.Join('\n', result.Limitations);
        Assert.Contains("127.0.0.1", limitations, StringComparison.Ordinal);
        Assert.Contains("不是 LAN", limitations, StringComparison.Ordinal);
        Assert.Contains("不是 Internet", limitations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 非有限或非正數延遲整項拒收()
    {
        var engine = new FakeNetworkLatencyEngine
        {
            Samples = [double.NaN, .. Enumerable.Repeat(13d, 255)]
        };
        var service = new NetworkStackLatencyAdapter(engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("非有限或非正數", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 量測引擎失敗時不合成結果()
    {
        var engine = new FakeNetworkLatencyEngine
        {
            Throw = new IOException("listener failed")
        };
        var service = new NetworkStackLatencyAdapter(engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.PlatformError, result.FailureKind);
        Assert.Contains("listener failed", result.Error, StringComparison.Ordinal);
    }

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private sealed class FakeNetworkLatencyEngine : INetworkLatencyEngine
    {
        public int Calls;
        public int LastProbes;
        public int LastWarmup;
        public IOException? Throw;
        public List<double> Samples { get; set; } = Enumerable.Repeat(13d, 256).ToList();

        public Task<NetworkLatencyMeasurement> MeasureAsync(
            int probes,
            int warmup,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastProbes = probes;
            LastWarmup = warmup;
            if (Throw is not null) throw Throw;
            return Task.FromResult(new NetworkLatencyMeasurement(Samples));
        }
    }
}
