using System.Globalization;

namespace XinSpect;

public interface IPowerStateLatencyEngine
{
    Task<PowerStateLatencyRun> ObserveAsync(
        PowerStateLatencyWorkload workload,
        CancellationToken cancellationToken);
}

public sealed record PowerStateLatencyWorkload(
    int Rounds,
    int ObservationMs,
    int SamplingIntervalMs);

/// <summary>一次 CallNtPowerInformation 觀察；六個欄位是逐邏輯處理器回報值的最小／最大聚合。</summary>
public sealed record PowerStateObservation(
    double TimeMs,
    double QueryLatencyUs,
    uint CurrentMhzMin,
    uint CurrentMhzMax,
    uint MhzLimitMin,
    uint MhzLimitMax,
    uint CurrentIdleStateMin,
    uint CurrentIdleStateMax);

public sealed record PowerStateLatencyRound(IReadOnlyList<PowerStateObservation> Observations);

public sealed record PowerStateLatencyRun(
    int LogicalProcessors,
    IReadOnlyList<PowerStateLatencyRound> Rounds);

public sealed class PowerStateLatencyUnsupportedException(string message) : InvalidOperationException(message);
public sealed class PowerStateLatencyValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// Power state latency：連續取樣 Windows 電源 API 的逐核回報值，量 API 查詢延遲，
/// 並記錄 P-state 上限換算值、政策限頻與 C-state 回報的變化偵測間距。
/// </summary>
public sealed class PowerStateLatencyService(IPowerStateLatencyEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "gauntlet.power-state-latency";

    public static string[] Limitations { get; } =
    [
        "量的是 CallNtPowerInformation 查詢延遲與取樣點之間可見的狀態變化；不是韌體內部轉換時間，也不拆解 voltage regulator、microcode 或 package power state。",
        "PROCESSOR_POWER_INFORMATION.CurrentMhz 是 P-state 上限換算值，不是核心實際有效時脈；有效時脈需要 MPERF/APERF 類量測。",
        "不修改電源計劃、不鎖頻、不停放核心、不要求睡眠或休眠；只做使用者模式唯讀查詢。",
        "取樣是離散的；狀態變化的真實轉換落在兩個樣本之間，偵測間距只能作為上界觀察。",
        "API 查詢失敗、回傳空核心或取樣不完整時如實拒收；不用預設值或內插補值。",
    ];

    private readonly IPowerStateLatencyEngine _engine = engine ?? new WindowsPowerStateLatencyEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PowerStateLatencyWorkload workload = GetSettings(context.Profile);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.03, "建立電源狀態觀察工作負載"));
            PowerStateLatencyRun run = await _engine.ObserveAsync(workload, cancellationToken).ConfigureAwait(false);
            ValidateRun(workload, run);

            IReadOnlyList<DeepBenchMetric> metrics = CreateMetrics(run);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.95, "整理電源 API 原始樣本"));
            return new DeepBenchTestResult(
                TestId,
                context.SessionId,
                context.Profile,
                started,
                DateTime.UtcNow,
                $"{run.LogicalProcessors} logical processors；{workload.Rounds} rounds × {workload.ObservationMs} ms @ {workload.SamplingIntervalMs} ms",
                metrics,
                CreateConditions(workload, run),
                Limitations,
                DeepBenchFailureKind.None,
                null);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (PowerStateLatencyUnsupportedException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unsupported, exception.Message);
        }
        catch (PowerStateLatencyValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static PowerStateLatencyWorkload GetSettings(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(3, 1800, 15),
        DeepBenchRunProfile.Full => new(6, 3000, 10),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    internal static bool HasStateChange(PowerStateObservation previous, PowerStateObservation current)
    {
        return previous.CurrentMhzMin != current.CurrentMhzMin
            || previous.CurrentMhzMax != current.CurrentMhzMax
            || previous.MhzLimitMin != current.MhzLimitMin
            || previous.MhzLimitMax != current.MhzLimitMax
            || previous.CurrentIdleStateMin != current.CurrentIdleStateMin
            || previous.CurrentIdleStateMax != current.CurrentIdleStateMax;
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

    private static void ValidateRun(PowerStateLatencyWorkload workload, PowerStateLatencyRun run)
    {
        if (run.LogicalProcessors <= 0)
            throw new PowerStateLatencyValidationException("電源 API 未回報任何邏輯處理器；不輸出空結果。");
        if (run.Rounds.Count != workload.Rounds)
            throw new PowerStateLatencyValidationException($"電源狀態 round 數不符：需要 {workload.Rounds}，收到 {run.Rounds.Count}。");

        for (int roundIndex = 0; roundIndex < run.Rounds.Count; roundIndex++)
        {
            IReadOnlyList<PowerStateObservation> observations = run.Rounds[roundIndex].Observations;
            if (observations.Count < 2)
                throw new PowerStateLatencyValidationException($"電源狀態樣本不足：round {roundIndex + 1} 只有 {observations.Count} 點；至少需要兩個取樣點。");

            double previousTime = double.NegativeInfinity;
            for (int observationIndex = 0; observationIndex < observations.Count; observationIndex++)
            {
                PowerStateObservation sample = observations[observationIndex];
                if (!double.IsFinite(sample.TimeMs)
                    || sample.TimeMs < 0
                    || sample.TimeMs <= previousTime
                    || !double.IsFinite(sample.QueryLatencyUs)
                    || sample.QueryLatencyUs <= 0)
                    throw new PowerStateLatencyValidationException($"電源狀態 round {roundIndex + 1} 樣本 {observationIndex + 1} 時間或查詢延遲非有限、負數或不遞增；整場拒收。");
                if (sample.CurrentMhzMin > sample.CurrentMhzMax
                    || sample.MhzLimitMin > sample.MhzLimitMax
                    || sample.CurrentIdleStateMin > sample.CurrentIdleStateMax)
                    throw new PowerStateLatencyValidationException($"電源狀態 round {roundIndex + 1} 聚合最小值大於最大值；整場拒收。");

                previousTime = sample.TimeMs;
            }
        }
    }

    private static IReadOnlyList<DeepBenchMetric> CreateMetrics(PowerStateLatencyRun run)
    {
        List<PowerStateObservation> observations = [.. run.Rounds.SelectMany(round => round.Observations)];
        List<DeepBenchMetric> metrics =
        [
            new(
                "gauntlet.power.query-latency.us",
                "CallNtPowerInformation query latency",
                "us",
                false,
                "CPU timestamps around each power API query",
                [.. observations.Select(sample => sample.QueryLatencyUs)],
                observations.Select(sample => new DeepBenchMetricPoint(
                    sample.QueryLatencyUs,
                    new Dictionary<string, string>
                    {
                        ["sample"] = sample.TimeMs.ToString("0.###", CultureInfo.InvariantCulture),
                    },
                    [sample.QueryLatencyUs])).ToArray()),
        ];

        AddRoundAggregates(metrics, "gauntlet.power.current-mhz.min", "Minimum reported CurrentMhz", "MHz", run, sample => sample.CurrentMhzMin);
        AddRoundAggregates(metrics, "gauntlet.power.current-mhz.max", "Maximum reported CurrentMhz", "MHz", run, sample => sample.CurrentMhzMax);
        AddRoundAggregates(metrics, "gauntlet.power.mhz-limit.min", "Minimum reported MHz limit", "MHz", run, sample => sample.MhzLimitMin);
        AddRoundAggregates(metrics, "gauntlet.power.mhz-limit.max", "Maximum reported MHz limit", "MHz", run, sample => sample.MhzLimitMax);
        AddRoundAggregates(metrics, "gauntlet.power.idle-state.min", "Minimum reported C-state", "state", run, sample => sample.CurrentIdleStateMin);
        AddRoundAggregates(metrics, "gauntlet.power.idle-state.max", "Maximum reported C-state", "state", run, sample => sample.CurrentIdleStateMax);

        double[] changeCounts = new double[run.Rounds.Count];
        List<double> detectionIntervals = [];
        for (int roundIndex = 0; roundIndex < run.Rounds.Count; roundIndex++)
        {
            IReadOnlyList<PowerStateObservation> roundObservations = run.Rounds[roundIndex].Observations;
            int changes = 0;
            for (int index = 1; index < roundObservations.Count; index++)
            {
                if (HasStateChange(roundObservations[index - 1], roundObservations[index]))
                {
                    changes++;
                    detectionIntervals.Add(roundObservations[index].TimeMs - roundObservations[index - 1].TimeMs);
                }
            }

            changeCounts[roundIndex] = changes;
        }

        metrics.Add(new(
            "gauntlet.power.state-change.count",
            "Observable power-state changes",
            "changes",
            true,
            "changes between discrete API snapshots",
            changeCounts,
            []));
        if (detectionIntervals.Count > 0)
        {
            metrics.Add(new(
                "gauntlet.power.state-change.detection-interval.ms",
                "State-change detection interval",
                "ms",
                false,
                "time between the two API snapshots that bracketed a change",
                [.. detectionIntervals],
                []));
        }

        metrics.Add(SummaryMetric("gauntlet.power.query-latency.p50.us", observations.Select(sample => sample.QueryLatencyUs).ToArray(), Percentile([.. observations.Select(sample => sample.QueryLatencyUs)], 50)));
        metrics.Add(SummaryMetric("gauntlet.power.query-latency.p95.us", observations.Select(sample => sample.QueryLatencyUs).ToArray(), Percentile([.. observations.Select(sample => sample.QueryLatencyUs)], 95)));
        metrics.Add(SummaryMetric("gauntlet.power.query-latency.p99.us", observations.Select(sample => sample.QueryLatencyUs).ToArray(), Percentile([.. observations.Select(sample => sample.QueryLatencyUs)], 99)));
        return metrics;
    }

    private static void AddRoundAggregates(
        List<DeepBenchMetric> metrics,
        string id,
        string title,
        string unit,
        PowerStateLatencyRun run,
        Func<PowerStateObservation, uint> selector)
    {
        double[] samples = run.Rounds
            .Select(round => (double)selector(round.Observations[0]))
            .ToArray();
        for (int roundIndex = 0; roundIndex < run.Rounds.Count; roundIndex++)
            samples[roundIndex] = run.Rounds[roundIndex].Observations.Select(selector).Min();

        if (id.EndsWith(".max", StringComparison.Ordinal))
        {
            for (int roundIndex = 0; roundIndex < run.Rounds.Count; roundIndex++)
                samples[roundIndex] = run.Rounds[roundIndex].Observations.Select(selector).Max();
        }

        metrics.Add(new(id, title, unit, false, "first/aggregate round observation", samples, []));
    }

    private static DeepBenchMetric SummaryMetric(string id, IReadOnlyList<double> samples, double value) => new(
        id,
        id.Replace('.', ' '),
        "us",
        false,
        $"{samples.Count} samples",
        [value],
        [new DeepBenchMetricPoint(value, new Dictionary<string, string>(), [value])]);

    private static string[] CreateConditions(PowerStateLatencyWorkload workload, PowerStateLatencyRun run)
    {
        int changes = run.Rounds.Sum(round =>
        {
            int count = 0;
            for (int index = 1; index < round.Observations.Count; index++)
                count += HasStateChange(round.Observations[index - 1], round.Observations[index]) ? 1 : 0;
            return count;
        });

        return changes == 0
            ?
            [
                $"{workload.Rounds} rounds × {workload.ObservationMs} ms @ {workload.SamplingIntervalMs} ms；保留 {run.Rounds.Sum(round => round.Observations.Count)} 個 API 快照。",
                "沒有觀察到可報告的電源狀態變化；量不到轉換時間，因此不輸出轉換延遲推算。",
            ]
            :
            [
                $"{workload.Rounds} rounds × {workload.ObservationMs} ms @ {workload.SamplingIntervalMs} ms；保留 {run.Rounds.Sum(round => round.Observations.Count)} 個 API 快照。",
                $"觀察到 {changes} 次電源狀態快照變化；偵測間距是兩次取樣 timestamp 差，真實轉換落在兩者之間，不是韌體內部轉換時間。",
            ];
    }

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消前不輸出未完成電源樣本。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["電源 API 查詢不完整時不推算缺失樣本。"], kind, error);
}
