using System.Globalization;

namespace XinSpect;

public interface IPresentFramePacingEngine
{
    Task<PresentFramePacingRun> MeasureAsync(
        PresentFramePacingWorkload workload,
        CancellationToken cancellationToken);
}

public sealed record PresentFramePacingProfile(
    string Mode,
    int SyncInterval,
    int Rounds,
    int FramesPerRound);

public sealed record PresentFramePacingWorkload(IReadOnlyList<PresentFramePacingProfile> Profiles);

public readonly record struct PresentFramePacingSample(
    double IntervalMs,
    double PresentDurationMs);

public sealed record PresentFramePacingRound(
    string Mode,
    int SyncInterval,
    IReadOnlyList<PresentFramePacingSample> Samples);

public sealed record PresentFramePacingRun(
    string AdapterName,
    uint FeatureLevel,
    IReadOnlyList<PresentFramePacingRound> Rounds);

public sealed class PresentFramePacingValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// Present latency / frame pacing：建立小型 D3D11 swap chain 並以不同 Present sync interval
/// 逐幀送出變色影像。計時留在本行程 CPU 觀察：幀間距是連續送出呼叫的時間差，
/// Present 耗時是 ClearRenderTargetView + Present 的 API 觀察時間。
/// </summary>
public sealed class PresentFramePacingService(IPresentFramePacingEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "ux.present-frame-pacing";

    public static string[] Limitations { get; } =
    [
        "計時是本行程 CPU 觀察的 ClearRenderTargetView + Present API 時間；不是驅動內部 GPU timestamp，也不拆解 queue、shader 或 scanout 時間。",
        "vsync-on 會等待 swap chain 的重繪節奏；結果受顯示器更新率、多螢幕混合更新率、DWM、驅動與背景負載影響。",
        "vsync-off 量 unthrottled API 送出節奏，不可當作實際顯示幀率或輸入到 photon 延遲。",
        "測試會短暫建立 96×54 邊框視窗；只送本行程產生的變色影像，不開檔、不連網、不修改顯示設定。",
        "WARP 或軟體渲染如實 Unsupported；不把 Microsoft Basic Render Driver 說成硬體 GPU 實測。",
    ];

    private readonly IPresentFramePacingEngine _engine = engine ?? new D3D11PresentFramePacingEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PresentFramePacingWorkload workload = GetSettings(context.Profile);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.03, "建立 D3D11 present pacing 工作負載"));
            PresentFramePacingRun run = await _engine.MeasureAsync(workload, cancellationToken).ConfigureAwait(false);
            ValidateRun(workload, run);

            IReadOnlyList<DeepBenchMetric> metrics = CreateMetrics(run);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.95, "整理每一幀原始樣本"));
            return new DeepBenchTestResult(
                TestId,
                context.SessionId,
                context.Profile,
                started,
                DateTime.UtcNow,
                $"{run.AdapterName}；Feature Level 0x{run.FeatureLevel:X4}；{run.Rounds.Count} rounds",
                metrics,
                [
                    $"保留 {run.Rounds.Sum(round => round.Samples.Count)} 幀原始樣本；{DescribeProfiles(workload)}。",
                    "interval spike 門檻：max(4 ms, 同輪 interval p50 × 1.5)；門檻由該輪節奏建立，不使用固定 60 Hz 假設。",
                ],
                Limitations,
                DeepBenchFailureKind.None,
                null);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (GpuDeviceRemovedException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.DriverRejected, exception.Message);
        }
        catch (GpuUnsupportedException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unsupported, exception.Message);
        }
        catch (PresentFramePacingValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static PresentFramePacingWorkload GetSettings(DeepBenchRunProfile profile)
    {
        return profile switch
        {
            DeepBenchRunProfile.Quick => new(
            [
                new PresentFramePacingProfile("vsync-on", 1, 1, 180),
                new PresentFramePacingProfile("vsync-off", 0, 1, 180),
            ]),
            DeepBenchRunProfile.Full => new(
            [
                new PresentFramePacingProfile("vsync-on", 1, 2, 360),
                new PresentFramePacingProfile("vsync-off", 0, 2, 360),
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };
    }

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

    internal static int CountIntervalSpikes(IReadOnlyList<double> intervals)
    {
        double[] valid = [.. intervals.Where(value => double.IsFinite(value) && value > 0)];
        if (valid.Length == 0) return 0;
        double threshold = Math.Max(4.0, Percentile(valid, 50) * 1.5);
        return valid.Count(value => value > threshold);
    }

    private static void ValidateRun(PresentFramePacingWorkload workload, PresentFramePacingRun run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(run.AdapterName);
        if (GpuFp32ComputeService.IsWarp(run.AdapterName))
            throw new GpuUnsupportedException("偵測到 WARP（Microsoft Basic Render Driver）；本項不輸出硬體 GPU 結果。");
        if (run.FeatureLevel < 0x0B00)
            throw new GpuUnsupportedException($"D3D11 Feature Level 0x{run.FeatureLevel:X4} 低於必要值 0x0B00。");

        int expectedRounds = workload.Profiles.Sum(profile => profile.Rounds);
        if (run.Rounds.Count != expectedRounds)
            throw new PresentFramePacingValidationException($"Present round 數不符：需要 {expectedRounds}，收到 {run.Rounds.Count}。");

        int runIndex = 0;
        foreach (PresentFramePacingProfile profile in workload.Profiles)
        {
            for (int round = 0; round < profile.Rounds; round++, runIndex++)
            {
                PresentFramePacingRound actual = run.Rounds[runIndex];
                if (!string.Equals(actual.Mode, profile.Mode, StringComparison.Ordinal)
                    || actual.SyncInterval != profile.SyncInterval)
                    throw new PresentFramePacingValidationException($"Present round {runIndex + 1} 的模式或 sync interval 不符。");
                if (actual.Samples.Count != profile.FramesPerRound)
                    throw new PresentFramePacingValidationException($"Present round {runIndex + 1} 幀樣本數不符：需要 {profile.FramesPerRound}，收到 {actual.Samples.Count}。");
                if (!actual.Samples.All(sample =>
                        double.IsFinite(sample.IntervalMs) && sample.IntervalMs >= 0 &&
                        double.IsFinite(sample.PresentDurationMs) && sample.PresentDurationMs > 0))
                    throw new PresentFramePacingValidationException("Present 幀樣本出現非有限、負數或零耗時；整場拒收。");
                if (actual.Samples.Skip(1).Any(sample => sample.IntervalMs <= 0))
                    throw new PresentFramePacingValidationException("第二幀以後的 Present 間距必須為正數；整場拒收。");
            }
        }
    }

    private static IReadOnlyList<DeepBenchMetric> CreateMetrics(PresentFramePacingRun run)
    {
        List<DeepBenchMetricPoint> intervalPoints = [];
        List<DeepBenchMetricPoint> durationPoints = [];
        foreach (PresentFramePacingRound round in run.Rounds)
        {
            for (int index = 0; index < round.Samples.Count; index++)
            {
                var axes = new Dictionary<string, string>
                {
                    ["mode"] = round.Mode,
                    ["round"] = (index + 1).ToString(CultureInfo.InvariantCulture),
                    ["frame"] = (index + 1).ToString(CultureInfo.InvariantCulture),
                };
                intervalPoints.Add(new(round.Samples[index].IntervalMs, axes, [round.Samples[index].IntervalMs]));
                durationPoints.Add(new(round.Samples[index].PresentDurationMs, axes, [round.Samples[index].PresentDurationMs]));
            }
        }

        List<DeepBenchMetric> metrics =
        [
            new(
                "ux.present.interval.ms",
                "Frame-to-frame Present interval",
                "ms",
                false,
                "CPU timestamps around each Present call",
                [.. intervalPoints.Select(point => point.Value)],
                intervalPoints),
            new(
                "ux.present.duration.ms",
                "Clear + Present API duration",
                "ms",
                false,
                "CPU timestamps around ClearRenderTargetView + Present",
                [.. durationPoints.Select(point => point.Value)],
                durationPoints),
        ];

        foreach (string mode in run.Rounds.Select(round => round.Mode).Distinct(StringComparer.Ordinal))
        {
            PresentFramePacingRound[] rounds = [.. run.Rounds.Where(round => round.Mode == mode)];
            double[] intervals = [.. rounds.SelectMany(round => round.Samples.Select(sample => sample.IntervalMs)).Where(value => value > 0)];
            double[] durations = [.. rounds.SelectMany(round => round.Samples.Select(sample => sample.PresentDurationMs))];
            metrics.Add(SummaryMetric($"{mode}.interval.p50.ms", intervals, Percentile(intervals, 50)));
            metrics.Add(SummaryMetric($"{mode}.interval.p95.ms", intervals, Percentile(intervals, 95)));
            metrics.Add(SummaryMetric($"{mode}.interval.p99.ms", intervals, Percentile(intervals, 99)));
            metrics.Add(SummaryMetric($"{mode}.duration.p50.ms", durations, Percentile(durations, 50)));
            metrics.Add(SummaryMetric($"{mode}.duration.p99.ms", durations, Percentile(durations, 99)));
        }

        metrics.Add(new(
            "ux.present.interval-spike.count",
            "Dynamic interval spikes",
            "frames",
            false,
            "threshold = max(4 ms, round interval p50 × 1.5)",
            run.Rounds.Select(round => (double)CountIntervalSpikes(round.Samples.Select(sample => sample.IntervalMs).ToArray())).ToArray(),
            []));
        return metrics;
    }

    private static DeepBenchMetric SummaryMetric(string suffix, double[] samples, double value) => new(
        $"ux.present.{suffix}",
        suffix.Replace('.', ' '),
        suffix.Contains("duration", StringComparison.Ordinal) ? "ms" : "ms",
        false,
        $"{samples.Length} samples",
        [value],
        [new DeepBenchMetricPoint(value, new Dictionary<string, string>(), [value])]);

    private static string DescribeProfiles(PresentFramePacingWorkload workload) =>
        string.Join("; ", workload.Profiles.Select(profile => $"{profile.Mode}={profile.Rounds} rounds × {profile.FramesPerRound} frames"));

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消前不輸出未完成 Present 樣本。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["不推算缺失或無效 Present 幀樣本。"], kind, error);
}
