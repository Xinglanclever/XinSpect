using Xunit;

namespace XinSpect.Tests;

public class PresentFramePacingServiceTests
{
    [Fact]
    public void Quick與Full使用不同的同步模式和幀數()
    {
        var quick = PresentFramePacingService.GetSettings(DeepBenchRunProfile.Quick);
        var full = PresentFramePacingService.GetSettings(DeepBenchRunProfile.Full);

        Assert.Equal(["vsync-on", "vsync-off"], quick.Profiles.Select(profile => profile.Mode));
        Assert.Equal([1, 0], quick.Profiles.Select(profile => profile.SyncInterval));
        Assert.All(quick.Profiles, profile => Assert.Equal((1, 180), (profile.Rounds, profile.FramesPerRound)));

        Assert.Equal(["vsync-on", "vsync-off"], full.Profiles.Select(profile => profile.Mode));
        Assert.Equal([1, 0], full.Profiles.Select(profile => profile.SyncInterval));
        Assert.All(full.Profiles, profile => Assert.Equal((2, 360), (profile.Rounds, profile.FramesPerRound)));
    }

    [Fact]
    public async Task 結果保留每一幀間距與Present耗時()
    {
        var engine = new CapturingEngine(SampleRun());
        var service = new PresentFramePacingService(engine);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        DeepBenchMetric intervals = result.Metrics.Single(metric => metric.Id == "ux.present.interval.ms");
        DeepBenchMetric durations = result.Metrics.Single(metric => metric.Id == "ux.present.duration.ms");
        Assert.Equal(360, intervals.Samples.Count);
        Assert.Equal(360, durations.Samples.Count);
        Assert.Equal([0, 16.6, 17.4], intervals.Samples.Take(3));
        Assert.Equal([0, 3.1, 2.7], intervals.Samples.Skip(180).Take(3));
        Assert.Equal([0.8, 1.0, 0.9], durations.Samples.Take(3));
        Assert.Equal([0.4, 0.7, 0.5], durations.Samples.Skip(180).Take(3));
        Assert.Contains("ux.present.vsync-on.interval.p50.ms", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("ux.present.vsync-on.interval.p95.ms", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("ux.present.vsync-on.interval.p99.ms", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("ux.present.vsync-on.duration.p50.ms", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("ux.present.vsync-off.duration.p99.ms", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("ux.present.interval-spike.count", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("vsync-on=1 rounds × 180 frames", string.Join('\n', result.Conditions), StringComparison.Ordinal);
        Assert.Contains("不是驅動內部 GPU timestamp", string.Join('\n', result.Limitations), StringComparison.Ordinal);
        Assert.Single(engine.Runs);
    }

    [Fact]
    public async Task WARP或缺少硬體GPU如實Unsupported()
    {
        DeepBenchTestResult warpResult = await RunAsync(
            new PresentFramePacingService(new FailedEngine(new GpuUnsupportedException("WARP"))),
            DeepBenchRunProfile.Quick);
        DeepBenchTestResult missingResult = await RunAsync(
            new PresentFramePacingService(new FailedEngine(new GpuUnsupportedException("沒有硬體配接器"))),
            DeepBenchRunProfile.Full);

        Assert.Equal(DeepBenchFailureKind.Unsupported, warpResult.FailureKind);
        Assert.Equal(DeepBenchFailureKind.Unsupported, missingResult.FailureKind);
        Assert.All(new[] { warpResult, missingResult }, result => Assert.Empty(result.Metrics));
    }

    [Fact]
    public async Task 樣本數不足或出現非有限值整場拒收()
    {
        PresentFramePacingRun missing = RemoveLastSample(SampleRun());
        PresentFramePacingRun invalid = MakeDurationInvalid(SampleRun());

        DeepBenchTestResult missingResult = await RunAsync(
            new PresentFramePacingService(new CapturingEngine(missing)), DeepBenchRunProfile.Quick);
        DeepBenchTestResult invalidResult = await RunAsync(
            new PresentFramePacingService(new CapturingEngine(invalid)), DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, missingResult.FailureKind);
        Assert.Equal(DeepBenchFailureKind.Unstable, invalidResult.FailureKind);
        Assert.Contains("幀樣本", missingResult.Error, StringComparison.Ordinal);
        Assert.Contains("非有限", invalidResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消後不輸出未完成Present樣本()
    {
        using var cts = new CancellationTokenSource(40);
        var service = new PresentFramePacingService(new CancellingEngine());
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), DeepBenchRunProfile.Full, new Progress<DeepBenchProgress>());

        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Equal("已取消", result.Configuration);
    }

    [Fact]
    public void 百分位可重現且尖峰門檻跟著節奏建立()
    {
        double[] intervals = [16.0, 16.5, 16.7, 17.0, 16.8, 16.6, 16.4, 38.0];

        Assert.Equal(16.65, PresentFramePacingService.Percentile(intervals, 50));
        Assert.Equal(30.65, PresentFramePacingService.Percentile(intervals, 95), 2);
        Assert.Equal(36.53, PresentFramePacingService.Percentile(intervals, 99), 2);
        Assert.Equal(1, PresentFramePacingService.CountIntervalSpikes(intervals));
        Assert.Equal(0, PresentFramePacingService.CountIntervalSpikes([2, 3, 4]));
    }

    private static async Task<DeepBenchTestResult> RunAsync(PresentFramePacingService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private static PresentFramePacingRun SampleRun()
    {
        List<PresentFramePacingRound> rounds = [];
        rounds.Add(new PresentFramePacingRound(
            "vsync-on",
            1,
            [
                new PresentFramePacingSample(0, 0.8),
                new PresentFramePacingSample(16.6, 1.0),
                new PresentFramePacingSample(17.4, 0.9),
                .. Enumerable.Repeat(new PresentFramePacingSample(16.7, 0.8), 177),
            ]));
        rounds.Add(new PresentFramePacingRound(
            "vsync-off",
            0,
            [
                new PresentFramePacingSample(0, 0.4),
                new PresentFramePacingSample(3.1, 0.7),
                new PresentFramePacingSample(2.7, 0.5),
                .. Enumerable.Repeat(new PresentFramePacingSample(2.8, 0.4), 177),
            ]));
        return new PresentFramePacingRun("Test Hardware GPU", 0x0B100, rounds);
    }

    private static PresentFramePacingRun RemoveLastSample(PresentFramePacingRun run)
    {
        List<PresentFramePacingRound> rounds = [.. run.Rounds];
        List<PresentFramePacingSample> samples = [.. rounds[0].Samples];
        samples.RemoveAt(179);
        rounds[0] = rounds[0] with { Samples = samples };
        return run with { Rounds = rounds };
    }

    private static PresentFramePacingRun MakeDurationInvalid(PresentFramePacingRun run)
    {
        List<PresentFramePacingRound> rounds = [.. run.Rounds];
        List<PresentFramePacingSample> samples = [.. rounds[1].Samples];
        samples[2] = samples[2] with { PresentDurationMs = double.NaN };
        rounds[1] = rounds[1] with { Samples = samples };
        return run with { Rounds = rounds };
    }

    private sealed class CapturingEngine(PresentFramePacingRun run) : IPresentFramePacingEngine
    {
        public List<PresentFramePacingRun> Runs { get; } = [];

        public Task<PresentFramePacingRun> MeasureAsync(
            PresentFramePacingWorkload workload,
            CancellationToken cancellationToken)
        {
            _ = workload;
            Runs.Add(run);
            return Task.FromResult(run);
        }
    }

    private sealed class FailedEngine(GpuUnsupportedException exception) : IPresentFramePacingEngine
    {
        public Task<PresentFramePacingRun> MeasureAsync(
            PresentFramePacingWorkload workload,
            CancellationToken cancellationToken)
        {
            _ = workload;
            _ = cancellationToken;
            throw exception;
        }
    }

    private sealed class CancellingEngine : IPresentFramePacingEngine
    {
        public async Task<PresentFramePacingRun> MeasureAsync(
            PresentFramePacingWorkload workload,
            CancellationToken cancellationToken)
        {
            _ = workload;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new PresentFramePacingRun("unused", 0x0B100, []);
        }
    }
}
