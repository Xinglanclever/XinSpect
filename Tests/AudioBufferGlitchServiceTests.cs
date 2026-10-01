using Xunit;

namespace XinSpect.Tests;

public class AudioBufferGlitchServiceTests
{
    [Fact]
    public void Quick與Full使用不同延遲檔和回數()
    {
        var quick = AudioBufferGlitchService.GetSettings(DeepBenchRunProfile.Quick);
        var full = AudioBufferGlitchService.GetSettings(DeepBenchRunProfile.Full);

        Assert.Equal([40, 80], quick.LatencyProfiles);
        Assert.Equal([30, 60, 120], full.LatencyProfiles);
        Assert.True(full.RoundsPerProfile > quick.RoundsPerProfile);
        Assert.True(full.DurationMs > quick.DurationMs);
    }

    [Fact]
    public async Task 結果保留各延遲檔回呼間距速率與疑似掉樣()
    {
        var measurement = SampleMeasurement();
        var capturing = new CapturingEngine(measurement);
        var service = new AudioBufferGlitchService(capturing);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Contains("ux.audio.latency40.interval.p50.ms", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("ux.audio.latency40.interval.p95.ms", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("ux.audio.latency40.interval.p99.ms", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("ux.audio.latency40.callback-rate.samples/s", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("ux.audio.latency40.samples-per-call", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("ux.audio.latency80.interval.p99.ms", result.Metrics.Select(metric => metric.Id));
        Assert.Contains("ux.audio.gap.count", result.Metrics.Select(metric => metric.Id));
        Assert.Equal([9.1, 9.5], result.Metrics.Single(metric => metric.Id == "ux.audio.latency40.interval.p50.ms").Samples);
        Assert.Equal([0, 1, 0, 0], result.Metrics.Single(metric => metric.Id == "ux.audio.gap.count").Samples);
        Assert.Contains("40/80 ms", string.Join('\n', result.Conditions), StringComparison.Ordinal);
        Assert.Contains("不是 DAC、喇叭或端點端實際音訊品質", string.Join('\n', result.Limitations), StringComparison.Ordinal);
        Assert.Single(capturing.Contexts);
    }

    [Fact]
    public async Task 沒有音訊裝置時如實Unsupported且不輸出推算值()
    {
        var service = new AudioBufferGlitchService(new UnsupportedEngine());

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("音訊裝置", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 回呼缺樣本或負數整場拒收()
    {
        var missing = new AudioBufferGlitchMeasurement(
            new AudioBufferGlitchProfileSamples(40, [], [10], [11], [90], [100], [0]));
        var invalid = new AudioBufferGlitchMeasurement(
            new AudioBufferGlitchProfileSamples(40, [10], [-1], [11], [90], [100], [0]));

        DeepBenchTestResult missingResult = await RunAsync(
            new AudioBufferGlitchService(new CapturingEngine(missing)), DeepBenchRunProfile.Full);
        DeepBenchTestResult invalidResult = await RunAsync(
            new AudioBufferGlitchService(new CapturingEngine(invalid)), DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, missingResult.FailureKind);
        Assert.Equal(DeepBenchFailureKind.Unstable, invalidResult.FailureKind);
        Assert.Contains("回呼樣本", missingResult.Error, StringComparison.Ordinal);
        Assert.Contains("非有限或負數", invalidResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取消後不輸出部分延遲檔補值()
    {
        using var cts = new CancellationTokenSource(50);
        var service = new AudioBufferGlitchService(new CancellingEngine());
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Equal("已取消", result.Configuration);
    }

    [Fact]
    public void 誠實界線明示使用者模式觀察且不宣稱硬體故障()
    {
        Assert.Contains(AudioBufferGlitchService.Limitations, item => item.Contains("WASAPI render", StringComparison.Ordinal));
        Assert.Contains(AudioBufferGlitchService.Limitations, item => item.Contains("不宣稱韌體 DMA 故障", StringComparison.Ordinal));
        Assert.Contains(AudioBufferGlitchService.Limitations, item => item.Contains("靜音", StringComparison.Ordinal));
    }

    [Fact]
    public void 間距統計使用可重現百分位並套動態掉樣門檻()
    {
        double[] intervals = [8, 9, 9, 9, 10, 10, 11, 25];

        Assert.Equal(9.5, AudioBufferGlitchService.Percentile(intervals, 50));
        Assert.Equal(20.10, AudioBufferGlitchService.Percentile(intervals, 95), 2);
        Assert.Equal(24.02, AudioBufferGlitchService.Percentile(intervals, 99), 2);
        Assert.Equal(1, AudioBufferGlitchService.CountGaps(intervals));
        Assert.Equal(0, AudioBufferGlitchService.CountGaps([9, 10, 11]));
    }

    private static AudioBufferGlitchMeasurement SampleMeasurement() => new(
        new AudioBufferGlitchProfileSamples(40, [9.1, 9.5], [12.0, 13.0], [14.0, 15.0], [88, 92], [108, 112], [0, 1]),
        new AudioBufferGlitchProfileSamples(80, [18.2, 18.6], [22.0, 23.0], [24.0, 25.0], [46, 48], [205, 210], [0, 0]));

    private static async Task<DeepBenchTestResult> RunAsync(AudioBufferGlitchService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class CapturingEngine(AudioBufferGlitchMeasurement measurement) : IAudioBufferGlitchEngine
    {
        public List<AudioBufferGlitchContext> Contexts { get; } = [];

        public Task<AudioBufferGlitchMeasurement> MeasureAsync(
            AudioBufferGlitchContext context,
            CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            return Task.FromResult(measurement);
        }
    }

    private sealed class UnsupportedEngine : IAudioBufferGlitchEngine
    {
        public Task<AudioBufferGlitchMeasurement> MeasureAsync(
            AudioBufferGlitchContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            _ = cancellationToken;
            throw new AudioBufferGlitchUnsupportedException("找不到可用的主動音訊裝置。");
        }
    }

    private sealed class CancellingEngine : IAudioBufferGlitchEngine
    {
        public async Task<AudioBufferGlitchMeasurement> MeasureAsync(
            AudioBufferGlitchContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new AudioBufferGlitchMeasurement();
        }
    }
}
