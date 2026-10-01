using Xunit;

namespace XinSpect.Tests;

public class TopologyCoreBandwidthServiceTests
{
    [Fact]
    public async Task 非有限或非正數核心頻寬會整場拒收()
    {
        TopologyCoreBandwidthMeasurement measurement = new(
        [
            Point(0, 1, 12.5),
            Point(1, 0, double.NaN),
            Point(0, 2, 0),
            Point(2, 0, double.PositiveInfinity),
        ]);
        var service = CreateService(measurement);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("非有限或非正數", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 量測矩陣保留方向軸與原始樣本()
    {
        TopologyCoreBandwidthMeasurement measurement = new(
        [
            Point(0, 1, 18.25),
            Point(1, 0, 17.5),
        ],
        PinFailureCount: 2);
        var service = CreateService(measurement, 4);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Full);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        DeepBenchMetric metric = Assert.Single(result.Metrics);
        Assert.Equal("topology.core-bandwidth.gbps", metric.Id);
        Assert.Equal("GB/s", metric.Unit);
        Assert.Equal([18.25, 17.5], metric.Samples);
        Assert.Equal(2, metric.Points.Count);
        Assert.Equal("0", metric.Points[0].Axes["fromLp"]);
        Assert.Equal("1", metric.Points[0].Axes["toLp"]);
        Assert.Equal("1", metric.Points[1].Axes["fromLp"]);
        Assert.Equal("0", metric.Points[1].Axes["toLp"]);
        Assert.Contains("2 對", string.Join('\n', result.Conditions), StringComparison.Ordinal);
        Assert.Contains("2 對", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quick與Full使用不同的搬運矩陣設定()
    {
        var capturing = new CapturingEngine(new TopologyCoreBandwidthMeasurement([Point(0, 1, 1)]));
        var service = new TopologyCoreBandwidthService(() => [new(0, 0), new(0, 1)], capturing);

        await RunAsync(service, DeepBenchRunProfile.Quick);
        await RunAsync(service, DeepBenchRunProfile.Full);

        TopologyCoreBandwidthSettings quick = Assert.Single(capturing.Contexts, item => item.Profile == DeepBenchRunProfile.Quick).Settings;
        TopologyCoreBandwidthSettings full = Assert.Single(capturing.Contexts, item => item.Profile == DeepBenchRunProfile.Full).Settings;
        Assert.True(full.ChunkLongs > quick.ChunkLongs);
        Assert.True(full.MeasureRounds > quick.MeasureRounds);
        Assert.True(full.WarmupRounds + full.MeasureRounds > quick.WarmupRounds + quick.MeasureRounds);
    }

    [Fact]
    public async Task 取消後不輸出核心頻寬補值()
    {
        using var cts = new CancellationTokenSource(50);
        var service = new TopologyCoreBandwidthService(
            () => [new(0, 0), new(0, 1)],
            new CancellingEngine());

        var context = new DeepBenchRunContext(
            Guid.NewGuid(),
            DeepBenchRunProfile.Quick,
            new Progress<DeepBenchProgress>());

        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Equal("已取消", result.Configuration);
    }

    [Fact]
    public async Task 少於兩個邏輯處理器時標示不支援()
    {
        var engine = new CapturingEngine(new TopologyCoreBandwidthMeasurement([Point(0, 0, 1)]));
        var service = new TopologyCoreBandwidthService(() => [new(0, 0)], engine);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Empty(engine.Contexts);
        Assert.Empty(result.Metrics);
        Assert.Contains("少於兩個", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 誠實界線明示不是DRAMSTREAM絕對上限()
    {
        Assert.Contains(TopologyCoreBandwidthService.Limitations, item => item.Contains("不是 DRAM STREAM 絕對上限", StringComparison.Ordinal));
        Assert.Contains(TopologyCoreBandwidthService.Limitations, item => item.Contains("不外推", StringComparison.Ordinal));
        Assert.Contains(TopologyCoreBandwidthService.Limitations, item => item.Contains("釘選失敗", StringComparison.Ordinal));
    }

    private static TopologyCoreBandwidthPoint Point(int fromLp, int toLp, double gbPerSecond) =>
        new(new(0, (ushort)fromLp), new(0, (ushort)toLp), gbPerSecond);

    private static TopologyCoreBandwidthService CreateService(
        TopologyCoreBandwidthMeasurement measurement,
        int logicalProcessorCount = 2) =>
        new(
            () => Enumerable.Range(0, logicalProcessorCount).Select(index => new ProcessorRef(0, index)).ToArray(),
            new CapturingEngine(measurement));

    private static async Task<DeepBenchTestResult> RunAsync(TopologyCoreBandwidthService service, DeepBenchRunProfile profile)
    {
        DateTime now = DateTime.UtcNow;
        var context = new DeepBenchRunContext(
            Guid.NewGuid(),
            profile,
            new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class CapturingEngine(TopologyCoreBandwidthMeasurement measurement) : ITopologyCoreBandwidthEngine
    {
        public List<TopologyCoreBandwidthContext> Contexts { get; } = [];

        public Task<TopologyCoreBandwidthMeasurement> MeasureAsync(
            TopologyCoreBandwidthContext context,
            CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            return Task.FromResult(measurement);
        }
    }

    private sealed class CancellingEngine : ITopologyCoreBandwidthEngine
    {
        public async Task<TopologyCoreBandwidthMeasurement> MeasureAsync(
            TopologyCoreBandwidthContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new TopologyCoreBandwidthMeasurement([]);
        }
    }
}
