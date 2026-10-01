using Xunit;

namespace XinSpect.Tests;

public class PowerStateLatencyServiceTests
{
    [Fact]
    public void Quick與Full使用不同的觀察期與取樣間距()
    {
        var quick = PowerStateLatencyService.GetSettings(DeepBenchRunProfile.Quick);
        var full = PowerStateLatencyService.GetSettings(DeepBenchRunProfile.Full);

        Assert.Equal((3, 1800, 15), (quick.Rounds, quick.ObservationMs, quick.SamplingIntervalMs));
        Assert.Equal((6, 3000, 10), (full.Rounds, full.ObservationMs, full.SamplingIntervalMs));
    }

    [Fact]
    public async Task 結果保留查詢延遲狀態範圍與狀態變化偵測間距()
    {
        var engine = new CapturingEngine(SampleRun());
        var service = new PowerStateLatencyService(engine);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(15, result.Metrics.Single(metric => metric.Id == "gauntlet.power.query-latency.us").Samples.Count);
        Assert.Equal([1800, 1800, 1800], result.Metrics.Single(metric => metric.Id == "gauntlet.power.current-mhz.min").Samples);
        Assert.Equal([2600, 1800, 1800], result.Metrics.Single(metric => metric.Id == "gauntlet.power.current-mhz.max").Samples);
        Assert.Equal([0, 1, 1], result.Metrics.Single(metric => metric.Id == "gauntlet.power.idle-state.min").Samples);
        Assert.Equal([1, 1, 1], result.Metrics.Single(metric => metric.Id == "gauntlet.power.idle-state.max").Samples);
        Assert.Equal([2, 0, 0], result.Metrics.Single(metric => metric.Id == "gauntlet.power.state-change.count").Samples);
        Assert.Equal([15, 16], result.Metrics.Single(metric => metric.Id == "gauntlet.power.state-change.detection-interval.ms").Samples);
        Assert.Contains("gauntlet.power.query-latency.p95.us", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("3 rounds × 1800 ms", string.Join('\n', result.Conditions), StringComparison.Ordinal);
        Assert.Contains("不是韌體內部轉換時間", string.Join('\n', result.Limitations), StringComparison.Ordinal);
        Assert.Single(engine.Runs);
    }

    [Fact]
    public async Task 沒有狀態變化時如實標示且不推算轉換延遲()
    {
        PowerStateLatencyRun run = MakeStable(SampleRun());
        var service = new PowerStateLatencyService(new CapturingEngine(run));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal([0, 0, 0], result.Metrics.Single(metric => metric.Id == "gauntlet.power.state-change.count").Samples);
        Assert.DoesNotContain(result.Metrics, metric => metric.Id == "gauntlet.power.state-change.detection-interval.ms");
        Assert.Contains("沒有觀察到可報告的電源狀態變化", string.Join('\n', result.Conditions), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 電源API失敗時如實Unsupported()
    {
        var service = new PowerStateLatencyService(
            new FailedEngine(new PowerStateLatencyUnsupportedException("CallNtPowerInformation returned 3221225473")));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Full);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("CallNtPowerInformation", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 樣本不足或非有限延遲整場拒收()
    {
        DeepBenchTestResult missing = await RunAsync(
            new PowerStateLatencyService(new CapturingEngine(RemoveLastSnapshot(SampleRun()))),
            DeepBenchRunProfile.Quick);
        DeepBenchTestResult invalid = await RunAsync(
            new PowerStateLatencyService(new CapturingEngine(MakeQueryLatencyInvalid(SampleRun()))),
            DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, missing.FailureKind);
        Assert.Equal(DeepBenchFailureKind.Unstable, invalid.FailureKind);
        Assert.Contains("電源狀態樣本", missing.Error, StringComparison.Ordinal);
        Assert.Contains("非有限", invalid.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消後不輸出未完成電源樣本()
    {
        using var cts = new CancellationTokenSource(40);
        var service = new PowerStateLatencyService(new CancellingEngine());
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), DeepBenchRunProfile.Full, new Progress<DeepBenchProgress>());

        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Equal("已取消", result.Configuration);
    }

    [Fact]
    public void 狀態變化偵測使用完整聚合指紋()
    {
        var previous = new PowerStateObservation(0, 5, 2600, 2600, 2600, 2600, 0, 0);
        var mhzChanged = previous with { CurrentMhzMin = 1800, CurrentMhzMax = 2600 };
        var limitChanged = previous with { MhzLimitMax = 1800 };
        var idleChanged = previous with { CurrentIdleStateMax = 1 };
        var unchanged = previous;

        Assert.True(PowerStateLatencyService.HasStateChange(previous, mhzChanged));
        Assert.True(PowerStateLatencyService.HasStateChange(previous, limitChanged));
        Assert.True(PowerStateLatencyService.HasStateChange(previous, idleChanged));
        Assert.False(PowerStateLatencyService.HasStateChange(previous, unchanged));
    }

    private static async Task<DeepBenchTestResult> RunAsync(PowerStateLatencyService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private static PowerStateLatencyRun SampleRun()
    {
        List<PowerStateLatencyRound> rounds = [];
        List<PowerStateObservation> first =
        [
            new(0, 25, 2600, 2600, 2600, 2600, 1, 1),
            new(15, 22, 1800, 2600, 2600, 2600, 0, 1),
            new(31, 27, 2600, 2600, 2600, 2600, 1, 1),
            new(46, 24, 2600, 2600, 2600, 2600, 1, 1),
            new(61, 26, 2600, 2600, 2600, 2600, 1, 1),
        ];
        List<PowerStateObservation> second =
        [
            new(0, 21, 1800, 1800, 2600, 2600, 1, 1),
            new(15, 23, 1800, 1800, 2600, 2600, 1, 1),
            new(31, 22, 1800, 1800, 2600, 2600, 1, 1),
            new(47, 24, 1800, 1800, 2600, 2600, 1, 1),
            new(62, 22, 1800, 1800, 2600, 2600, 1, 1),
        ];
        rounds.Add(new PowerStateLatencyRound(first));
        rounds.Add(new PowerStateLatencyRound(second));
        rounds.Add(new PowerStateLatencyRound([.. second]));
        return new PowerStateLatencyRun(Environment.ProcessorCount, rounds);
    }

    private static PowerStateLatencyRun MakeStable(PowerStateLatencyRun run)
    {
        List<PowerStateObservation> stable =
        [
            new(0, 22, 2600, 2600, 2600, 2600, 1, 1),
            new(15, 23, 2600, 2600, 2600, 2600, 1, 1),
            new(31, 22, 2600, 2600, 2600, 2600, 1, 1),
            new(46, 23, 2600, 2600, 2600, 2600, 1, 1),
            new(61, 22, 2600, 2600, 2600, 2600, 1, 1),
        ];
        return run with { Rounds = [new PowerStateLatencyRound(stable), .. run.Rounds.Skip(1)] };
    }

    private static PowerStateLatencyRun RemoveLastSnapshot(PowerStateLatencyRun run)
    {
        List<PowerStateLatencyRound> rounds = [.. run.Rounds];
        rounds[^1] = rounds[^1] with { Observations = [.. rounds[^1].Observations.Take(1)] };
        return run with { Rounds = rounds };
    }

    private static PowerStateLatencyRun MakeQueryLatencyInvalid(PowerStateLatencyRun run)
    {
        List<PowerStateLatencyRound> rounds = [.. run.Rounds];
        List<PowerStateObservation> observations = [.. rounds[1].Observations];
        observations[2] = observations[2] with { QueryLatencyUs = double.NaN };
        rounds[1] = rounds[1] with { Observations = observations };
        return run with { Rounds = rounds };
    }

    private sealed class CapturingEngine(PowerStateLatencyRun run) : IPowerStateLatencyEngine
    {
        public List<PowerStateLatencyRun> Runs { get; } = [];

        public Task<PowerStateLatencyRun> ObserveAsync(
            PowerStateLatencyWorkload workload,
            CancellationToken cancellationToken)
        {
            _ = workload;
            _ = cancellationToken;
            Runs.Add(run);
            return Task.FromResult(run);
        }
    }

    private sealed class FailedEngine(PowerStateLatencyUnsupportedException exception) : IPowerStateLatencyEngine
    {
        public Task<PowerStateLatencyRun> ObserveAsync(
            PowerStateLatencyWorkload workload,
            CancellationToken cancellationToken)
        {
            _ = workload;
            _ = cancellationToken;
            throw exception;
        }
    }

    private sealed class CancellingEngine : IPowerStateLatencyEngine
    {
        public async Task<PowerStateLatencyRun> ObserveAsync(
            PowerStateLatencyWorkload workload,
            CancellationToken cancellationToken)
        {
            _ = workload;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new PowerStateLatencyRun(Environment.ProcessorCount, []);
        }
    }
}
