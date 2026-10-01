using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace XinSpect;

public interface IAudioBufferGlitchEngine
{
    Task<AudioBufferGlitchMeasurement> MeasureAsync(
        AudioBufferGlitchContext context,
        CancellationToken cancellationToken);
}

public sealed record AudioBufferGlitchSettings(
    IReadOnlyList<int> LatencyProfiles,
    int RoundsPerProfile,
    int DurationMs);

public sealed record AudioBufferGlitchContext(
    DeepBenchRunProfile Profile,
    AudioBufferGlitchSettings Settings,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public sealed record AudioBufferGlitchProfileSamples(
    int RequestedLatencyMs,
    IReadOnlyList<double> IntervalP50Ms,
    IReadOnlyList<double> IntervalP95Ms,
    IReadOnlyList<double> IntervalP99Ms,
    IReadOnlyList<double> CallbackRates,
    IReadOnlyList<double> SamplesPerCallbacks,
    IReadOnlyList<double> GapCounts);

public sealed record AudioBufferGlitchMeasurement(IReadOnlyList<AudioBufferGlitchProfileSamples> Profiles)
{
    public AudioBufferGlitchMeasurement(params AudioBufferGlitchProfileSamples[] profiles)
        : this(profiles as IReadOnlyList<AudioBufferGlitchProfileSamples>)
    {
    }
}

public sealed class AudioBufferGlitchUnsupportedException(string message) : InvalidOperationException(message);
public sealed class AudioBufferGlitchValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// Audio buffer / glitch：以共用模式 WASAPI render 送出靜音供樣，量本程式供樣回呼的
/// 可觀察間距、供樣速率與疑似掉樣。不讀驅動私有計數器，也不宣稱量到 DAC 端音質。
/// </summary>
public sealed class AudioBufferGlitchService(IAudioBufferGlitchEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "ux.audio-buffer-glitch";

    public static string[] Limitations { get; } =
    [
        "量的是共用模式 WASAPI render 的靜音供樣回呼觀察；不是 DAC、喇叭或端點端實際音訊品質。",
        "回呼間距超過動態門檻只標為疑似 glitch/dropout；不宣稱韌體 DMA 故障。",
        "間距包含 audio engine 排程、緩衝策略、CPU 排程、藍牙緩衝與背景負載；不可直接外推無線延遲。",
        "會短暫占用預設輸出裝置並混合靜音；不修改系統音量、預設裝置或裝置格式。",
        "沒有主動輸出裝置時如實 Unsupported；不使用假裝置或合成結果補值。",
    ];

    private readonly IAudioBufferGlitchEngine _engine = engine ?? new WindowsAudioBufferGlitchEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "檢查音訊輸出裝置"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            AudioBufferGlitchSettings settings = GetSettings(context.Profile);
            var engineContext = new AudioBufferGlitchContext(
                context.Profile,
                settings,
                context.Progress,
                cancellationToken);
            AudioBufferGlitchMeasurement measurement = await _engine
                .MeasureAsync(engineContext, cancellationToken)
                .ConfigureAwait(false);
            Validate(measurement);
            return CreateResult(context, started, settings, measurement);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (AudioBufferGlitchUnsupportedException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unsupported, exception.Message);
        }
        catch (AudioBufferGlitchValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static AudioBufferGlitchSettings GetSettings(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new([40, 80], 2, 900),
        DeepBenchRunProfile.Full => new([30, 60, 120], 3, 1500),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    internal static double Percentile(IReadOnlyList<double> samples, double percentile)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percentile, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 100);
        if (samples.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(samples), samples, "百分位需要至少一個樣本。");
        double[] sorted = [.. samples.OrderBy(value => value)];
        double position = (sorted.Length - 1) * percentile / 100.0;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        double fraction = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    internal static int CountGaps(IReadOnlyList<double> intervals)
    {
        if (intervals.Count == 0) return 0;
        double threshold = Math.Max(20.0, Percentile(intervals, 50) * 1.4);
        return intervals.Count(value => value > threshold);
    }

    private static void Validate(AudioBufferGlitchMeasurement measurement)
    {
        if (measurement.Profiles.Count == 0)
            throw new AudioBufferGlitchValidationException("沒有任何 WASAPI 延遲檔回呼樣本；不輸出空結果。");
        foreach (var profile in measurement.Profiles)
        {
            if (profile.IntervalP50Ms.Count == 0
                || profile.IntervalP95Ms.Count != profile.IntervalP50Ms.Count
                || profile.IntervalP99Ms.Count != profile.IntervalP50Ms.Count
                || profile.CallbackRates.Count != profile.IntervalP50Ms.Count
                || profile.SamplesPerCallbacks.Count != profile.IntervalP50Ms.Count
                || profile.GapCounts.Count != profile.IntervalP50Ms.Count)
                throw new AudioBufferGlitchValidationException("WASAPI 延遲檔回呼樣本數不一致；不推算缺失樣本。");
            if (!profile.IntervalP50Ms.All(value => double.IsFinite(value) && value >= 0)
                || !profile.IntervalP95Ms.All(value => double.IsFinite(value) && value >= 0)
                || !profile.IntervalP99Ms.All(value => double.IsFinite(value) && value >= 0)
                || !profile.CallbackRates.All(value => double.IsFinite(value) && value > 0)
                || !profile.SamplesPerCallbacks.All(value => double.IsFinite(value) && value > 0)
                || !profile.GapCounts.All(value => double.IsFinite(value) && value >= 0))
                throw new AudioBufferGlitchValidationException("WASAPI 回呼指標出現非有限或負數樣本；整場拒收。");
        }
    }

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        AudioBufferGlitchSettings settings,
        AudioBufferGlitchMeasurement measurement)
    {
        List<DeepBenchMetric> metrics = [];
        foreach (var profile in measurement.Profiles.OrderBy(item => item.RequestedLatencyMs))
        {
            string prefix = $"ux.audio.latency{profile.RequestedLatencyMs}";
            metrics.Add(Metric($"{prefix}.interval.p50.ms", $"{profile.RequestedLatencyMs} ms callback p50", "ms", false, profile.IntervalP50Ms));
            metrics.Add(Metric($"{prefix}.interval.p95.ms", $"{profile.RequestedLatencyMs} ms callback p95", "ms", false, profile.IntervalP95Ms));
            metrics.Add(Metric($"{prefix}.interval.p99.ms", $"{profile.RequestedLatencyMs} ms callback p99", "ms", false, profile.IntervalP99Ms));
            metrics.Add(Metric($"{prefix}.callback-rate.samples/s", $"{profile.RequestedLatencyMs} ms supply callback rate", "samples/s", true, profile.CallbackRates));
            metrics.Add(Metric($"{prefix}.samples-per-call", $"{profile.RequestedLatencyMs} ms samples per callback", "samples", true, profile.SamplesPerCallbacks));
        }

        metrics.Add(Metric(
            "ux.audio.gap.count",
            "Suspected dropout callbacks",
            "callbacks",
            false,
            [.. measurement.Profiles.OrderBy(item => item.RequestedLatencyMs).SelectMany(item => item.GapCounts)]));

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"{settings.LatencyProfiles.Count} latency profiles（{string.Join('/', settings.LatencyProfiles)} ms）；{settings.RoundsPerProfile} rounds/profile；{settings.DurationMs} ms/round",
            metrics,
            [
                $"共用模式靜音供樣；延遲檔 {string.Join('/', settings.LatencyProfiles)} ms，每檔實測 {settings.RoundsPerProfile} 回。",
                "疑似掉樣門檻：max(20 ms, callback p50 × 1.4)；門檻本身由同輪間距建立。",
                "結果保留每個延遲檔的原始樣本；不同裝置、驅動與背景負載不可混比。",
            ],
            Limitations,
            DeepBenchFailureKind.None,
            null);
    }

    private static DeepBenchMetric Metric(
        string id,
        string title,
        string unit,
        bool higherIsBetter,
        IReadOnlyList<double> samples) =>
        new(id, title, unit, higherIsBetter, "WASAPI silent render", samples, []);

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消後不輸出部分延遲檔補值。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["量測不完整時不推算缺失回呼指標。"], kind, error);
}

/// <summary>Windows 使用者模式 WASAPI 實作；共用模式、靜音、零驅動、零特權。</summary>
public sealed class WindowsAudioBufferGlitchEngine : IAudioBufferGlitchEngine
{
    public async Task<AudioBufferGlitchMeasurement> MeasureAsync(
        AudioBufferGlitchContext context,
        CancellationToken cancellationToken)
    {
        MMDevice device;
        try
        {
            device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw new AudioBufferGlitchUnsupportedException($"找不到可用的主動音訊裝置：{exception.Message}");
        }

        var profiles = new List<AudioBufferGlitchProfileSamples>();
        int totalRounds = context.Settings.LatencyProfiles.Count * context.Settings.RoundsPerProfile;
        int completed = 0;
        foreach (int latencyMs in context.Settings.LatencyProfiles)
        {
            var p50 = new List<double>();
            var p95 = new List<double>();
            var p99 = new List<double>();
            var rates = new List<double>();
            var samplesPerCallback = new List<double>();
            var gaps = new List<double>();
            for (int round = 0; round < context.Settings.RoundsPerProfile; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.Progress.Report(new DeepBenchProgress(
                    AudioBufferGlitchService.TestId,
                    completed + 1,
                    totalRounds,
                    0.05 + 0.90 * completed / totalRounds,
                    $"WASAPI {latencyMs} ms／round {round + 1}/{context.Settings.RoundsPerProfile}"));

                var roundSamples = await MeasureRoundAsync(device, latencyMs, context.Settings.DurationMs, cancellationToken)
                    .ConfigureAwait(false);
                if (roundSamples.CallbackCount == 0)
                    throw new AudioBufferGlitchValidationException($"WASAPI {latencyMs} ms 檔沒有任何回呼；不輸出推算值。");

                p50.Add(AudioBufferGlitchService.Percentile(roundSamples.IntervalsMs, 50));
                p95.Add(AudioBufferGlitchService.Percentile(roundSamples.IntervalsMs, 95));
                p99.Add(AudioBufferGlitchService.Percentile(roundSamples.IntervalsMs, 99));
                rates.Add(roundSamples.SampleCount / roundSamples.Elapsed.TotalSeconds);
                samplesPerCallback.Add(roundSamples.SampleCount / roundSamples.CallbackCount);
                gaps.Add(AudioBufferGlitchService.CountGaps(roundSamples.IntervalsMs));
                completed++;
            }

            profiles.Add(new AudioBufferGlitchProfileSamples(latencyMs, p50, p95, p99, rates, samplesPerCallback, gaps));
        }

        return new AudioBufferGlitchMeasurement([.. profiles]);
    }

    private static async Task<RenderRoundSamples> MeasureRoundAsync(
        MMDevice device,
        int latencyMs,
        int durationMs,
        CancellationToken cancellationToken)
    {
        var provider = new SilentRenderProvider();
        long startedTimestamp = Stopwatch.GetTimestamp();
        Exception? playbackException = null;
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var player = new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
        player.PlaybackStopped += (_, arguments) =>
        {
            playbackException = arguments.Exception;
            stopped.TrySetResult();
        };
        player.Init(provider);
        player.Play();
        try
        {
            await Task.Delay(durationMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            player.Stop();
            try
            {
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Stop 已執行；沒有 Stopped 事件時不偽造樣本。
            }
        }

        if (playbackException is not null)
            throw new AudioBufferGlitchValidationException($"WASAPI render 提前停止：{playbackException.Message}");
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
        if (elapsed <= TimeSpan.Zero || provider.SampleCount <= 0)
            throw new AudioBufferGlitchValidationException("WASAPI render 沒有可驗證的靜音樣本。");

        double[] intervals = provider.GetIntervalsMs();
        if (intervals.Length == 0)
            throw new AudioBufferGlitchValidationException("WASAPI render 回呼少於兩次；不足以建立間距樣本。");
        return new(
            provider.CallbackCount,
            provider.SampleCount,
            elapsed,
            intervals);
    }

    private sealed record RenderRoundSamples(
        int CallbackCount,
        double SampleCount,
        TimeSpan Elapsed,
        double[] IntervalsMs);

    private sealed class SilentRenderProvider : IWaveProvider
    {
        private readonly object _gate = new();
        private readonly Queue<long> _timestamps = new();
        private long _sampleCountBytes;
        private int _callbackCount;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public int CallbackCount => _callbackCount;

        public double SampleCount
        {
            get
            {
                lock (_gate) return _sampleCountBytes / 4.0;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            long timestamp = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                _timestamps.Enqueue(timestamp);
                _callbackCount++;
                _sampleCountBytes += count;
            }

            Array.Clear(buffer, offset, count);
            return count;
        }

        public double[] GetIntervalsMs()
        {
            long[] timestamps;
            lock (_gate) timestamps = [.. _timestamps];
            if (timestamps.Length <= 1) return [];
            return [.. timestamps.Skip(1).Select((timestamp, index) =>
                (timestamp - timestamps[index]) / (double)Stopwatch.Frequency * 1000.0)];
        }
    }
}
