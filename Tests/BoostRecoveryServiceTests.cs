using Xunit;

namespace XinSpect.Tests;

public class BoostRecoveryServiceTests
{
    [Fact]
    public void Quick與Full使用不同的脈衝與觀察設定()
    {
        var quick = BoostRecoveryService.GetSettings(DeepBenchRunProfile.Quick);
        var full = BoostRecoveryService.GetSettings(DeepBenchRunProfile.Full);

        Assert.Equal((3, 200, 400, 300, 50), (quick.Rounds, quick.IdleMs, quick.LoadMs, quick.RecoveryMs, quick.SamplingIntervalMs));
        Assert.Equal((6, 400, 800, 600, 25), (full.Rounds, full.IdleMs, full.LoadMs, full.RecoveryMs, full.SamplingIntervalMs));
    }

    [Fact]
    public async Task 結果保留脈衝曲線爬升與恢復偵測()
    {
        var engine = new CapturingEngine(SampleRun());
        var service = new BoostRecoveryService(engine);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal([2000, 2000, 2000], result.Metrics.Single(metric => metric.Id == "gauntlet.boost.idle-mhz.max").Samples);
        Assert.Equal([3000, 3000, 3000], result.Metrics.Single(metric => metric.Id == "gauntlet.boost.load-peak-mhz.max").Samples);
        Assert.Equal([3000, 3000, 3000], result.Metrics.Single(metric => metric.Id == "gauntlet.boost.load-steady-mhz.median").Samples);
        Assert.Equal([2000, 2000, 2000], result.Metrics.Single(metric => metric.Id == "gauntlet.boost.recovery-final-mhz.median").Samples);
        Assert.Equal([20, 20, 20], result.Metrics.Single(metric => metric.Id == "gauntlet.boost.ramp-detection.ms").Samples);
        Assert.Equal([10, 10, 10], result.Metrics.Single(metric => metric.Id == "gauntlet.boost.recovery-detection.ms").Samples);
        Assert.Equal([0, 0, 0], result.Metrics.Single(metric => metric.Id == "gauntlet.boost.load-sag-percent").Samples);
        Assert.Contains("3 rounds × 200/400/300 ms", string.Join('\n', result.Conditions), StringComparison.Ordinal);
        Assert.Contains("P-state 上限換算值", string.Join('\n', result.Limitations), StringComparison.Ordinal);
        Assert.Single(engine.Runs);
    }

    [Fact]
    public async Task 沒有可偵測爬升時不推算爬升時間()
    {
        var service = new BoostRecoveryService(new CapturingEngine(MakeNoRamp(SampleRun())));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.DoesNotContain(result.Metrics, metric => metric.Id == "gauntlet.boost.ramp-detection.ms");
        Assert.Contains("沒有觀察到可報告的爬升", string.Join('\n', result.Conditions), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 樣本缺失或頻率無效時整場拒收()
    {
        DeepBenchTestResult missing = await RunAsync(
            new BoostRecoveryService(new CapturingEngine(RemoveRecovery(SampleRun()))),
            DeepBenchRunProfile.Quick);
        DeepBenchTestResult invalid = await RunAsync(
            new BoostRecoveryService(new CapturingEngine(MakeFrequencyInvalid(SampleRun()))),
            DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, missing.FailureKind);
        Assert.Contains("缺失", missing.Error, StringComparison.Ordinal);
        Assert.Equal(DeepBenchFailureKind.Unstable, invalid.FailureKind);
        Assert.Contains("非有限", invalid.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 電源API失敗時如實Unsupported()
    {
        var service = new BoostRecoveryService(
            new FailedEngine(new BoostRecoveryUnsupportedException("CallNtPowerInformation returned 3221225473")));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Full);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("CallNtPowerInformation", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消後不輸出未完成脈衝樣本()
    {
        using var cts = new CancellationTokenSource(40);
        var service = new BoostRecoveryService(new CancellingEngine());
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), DeepBenchRunProfile.Full, new Progress<DeepBenchProgress>());

        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Equal("已取消", result.Configuration);
    }

    [Fact]
    public void 爬升與恢復門檻使用同輪觀察基線()
    {
        BoostRecoverySample[] rampSamples = [.. SampleRun().Rounds[0].Samples];
        BoostRecoverySample[] noRampSamples = [.. MakeNoRamp(SampleRun()).Rounds[0].Samples];
        Assert.Equal(20, BoostRecoveryService.DetectionDelay(200, 400, 2_000, 3_000, rampSamples, recovery: false));
        Assert.Null(BoostRecoveryService.DetectionDelay(200, 400, 2_000, 2_000, noRampSamples, recovery: false));
        Assert.Equal(10, BoostRecoveryService.DetectionDelay(200, 400, 2_000, 3_000, rampSamples, recovery: true));
    }

    private static async Task<DeepBenchTestResult> RunAsync(BoostRecoveryService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private static BoostRecoveryRun SampleRun()
    {
        BoostRecoverySample[] CreateRound() =>
        [
            new(BoostRecoveryPhase.Idle, 0, 2_000, 2_000, 2_600, 2_600),
            new(BoostRecoveryPhase.Idle, 100, 2_000, 2_000, 2_600, 2_600),
            new(BoostRecoveryPhase.Idle, 190, 2_000, 2_000, 2_600, 2_600),
            new(BoostRecoveryPhase.Load, 200, 2_100, 2_100, 2_600, 2_600),
            new(BoostRecoveryPhase.Load, 220, 3_000, 3_000, 2_600, 2_600),
            new(BoostRecoveryPhase.Load, 300, 3_000, 3_000, 2_600, 2_600),
            new(BoostRecoveryPhase.Load, 590, 3_000, 3_000, 2_600, 2_600),
            new(BoostRecoveryPhase.Recovery, 610, 2_200, 2_200, 2_600, 2_600),
            new(BoostRecoveryPhase.Recovery, 620, 2_000, 2_000, 2_600, 2_600),
            new(BoostRecoveryPhase.Recovery, 700, 2_000, 2_000, 2_600, 2_600),
            new(BoostRecoveryPhase.Recovery, 780, 2_000, 2_000, 2_600, 2_600),
        ];
        List<BoostRecoveryRound> rounds = Enumerable
            .Range(0, 3)
            .Select(_ => new BoostRecoveryRound(CreateRound()))
            .ToList();
        return new BoostRecoveryRun(Environment.ProcessorCount, rounds);
    }

    private static BoostRecoveryRun MakeNoRamp(BoostRecoveryRun run)
    {
        List<BoostRecoveryRound> rounds = [.. run.Rounds];
        for (int index = 0; index < rounds.Count; index++)
        {
            rounds[index] = new BoostRecoveryRound(rounds[index].Samples
                .Select(sample => sample.Phase == BoostRecoveryPhase.Load
                    ? sample with { CurrentMhzMin = 2_000, CurrentMhzMax = 2_000 }
                    : sample)
                .ToArray());
        }

        return run with { Rounds = rounds };
    }

    private static BoostRecoveryRun RemoveRecovery(BoostRecoveryRun run) => run with
    {
        Rounds = run.Rounds
            .Select(round => new BoostRecoveryRound(round.Samples.Where(sample => sample.Phase != BoostRecoveryPhase.Recovery).ToArray()))
            .ToArray()
    };

    private static BoostRecoveryRun MakeFrequencyInvalid(BoostRecoveryRun run)
    {
        List<BoostRecoveryRound> rounds = [.. run.Rounds];
        BoostRecoverySample[] samples = [.. rounds[1].Samples];
        samples[2] = samples[2] with { CurrentMhzMin = double.NaN, CurrentMhzMax = double.NaN };
        rounds[1] = rounds[1] with { Samples = samples };
        return run with { Rounds = rounds };
    }

    private sealed class CapturingEngine(BoostRecoveryRun run) : IBoostRecoveryEngine
    {
        public List<BoostRecoveryRun> Runs { get; } = [];

        public Task<BoostRecoveryRun> MeasureAsync(
            BoostRecoveryWorkload workload,
            CancellationToken cancellationToken)
        {
            _ = workload;
            _ = cancellationToken;
            Runs.Add(run);
            return Task.FromResult(run);
        }
    }

    private sealed class FailedEngine(BoostRecoveryUnsupportedException exception) : IBoostRecoveryEngine
    {
        public Task<BoostRecoveryRun> MeasureAsync(
            BoostRecoveryWorkload workload,
            CancellationToken cancellationToken)
        {
            _ = workload;
            _ = cancellationToken;
            throw exception;
        }
    }

    private sealed class CancellingEngine : IBoostRecoveryEngine
    {
        public async Task<BoostRecoveryRun> MeasureAsync(
            BoostRecoveryWorkload workload,
            CancellationToken cancellationToken)
        {
            _ = workload;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new BoostRecoveryRun(Environment.ProcessorCount, []);
        }
    }
}
