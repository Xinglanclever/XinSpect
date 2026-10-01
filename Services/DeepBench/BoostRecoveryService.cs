using System.Globalization;

namespace XinSpect;

public enum BoostRecoveryPhase { Idle, Load, Recovery }

public interface IBoostRecoveryEngine
{
    Task<BoostRecoveryRun> MeasureAsync(
        BoostRecoveryWorkload workload,
        CancellationToken cancellationToken);
}

public sealed record BoostRecoveryWorkload(
    int Rounds,
    int IdleMs,
    int LoadMs,
    int RecoveryMs,
    int SamplingIntervalMs);

public sealed record BoostRecoverySample(
    BoostRecoveryPhase Phase,
    double TimeMs,
    double CurrentMhzMin,
    double CurrentMhzMax,
    double MhzLimitMin,
    double MhzLimitMax);

public sealed record BoostRecoveryRound(IReadOnlyList<BoostRecoverySample> Samples);

public sealed record BoostRecoveryRun(
    int LogicalProcessors,
    IReadOnlyList<BoostRecoveryRound> Rounds);

public sealed class BoostRecoveryUnsupportedException(string message) : InvalidOperationException(message);
public sealed class BoostRecoveryValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// Boost ramp / recovery：以全核心 managed pulse 切換 idle → load → recovery，
/// 同窗取樣 Windows 電源 API 的頻率上限換算值與政策限頻，記錄爬升、負載衰退與恢復時間。
/// </summary>
public sealed class BoostRecoveryService(IBoostRecoveryEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "gauntlet.boost-recovery";

    public static string[] Limitations { get; } =
    [
        "PROCESSOR_POWER_INFORMATION.CurrentMhz 是 P-state 上限換算值，不是核心實際有效時脈；也不是 advertised boost clock 或散熱容量證明。",
        "取樣是離散的；爬升與恢復的真實轉換落在兩個快照之間，偵測時間是取樣解析度上界。",
        "門檻由同一輪觀察到的 idle baseline 與 load peak 推導，不是 CPU 規格表、韌體目標或溫度牆時間。",
        "managed 全核心脈衝不調整執行緒優先權、親和性、電源計劃、頻率或電壓；背景排程與保護工作會直接影響樣本。",
        "沒有 MPERF/APERF、溫度、功耗或韌體遙測；不把缺失訊號內插成推算值。",
    ];

    private readonly IBoostRecoveryEngine _engine = engine ?? new WindowsBoostRecoveryEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BoostRecoveryWorkload workload = GetSettings(context.Profile);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.03, "準備全核心脈衝負載"));
            BoostRecoveryRun run = await _engine.MeasureAsync(workload, cancellationToken).ConfigureAwait(false);
            ValidateRun(workload, run);

            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.92, "整理脈衝曲線原始樣本"));
            return new DeepBenchTestResult(
                TestId,
                context.SessionId,
                context.Profile,
                started,
                DateTime.UtcNow,
                Configuration(workload),
                CreateMetrics(workload, run),
                CreateConditions(workload, run),
                Limitations,
                DeepBenchFailureKind.None,
                null);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (BoostRecoveryUnsupportedException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unsupported, exception.Message);
        }
        catch (BoostRecoveryValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static BoostRecoveryWorkload GetSettings(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(3, 200, 400, 300, 50),
        DeepBenchRunProfile.Full => new(6, 400, 800, 600, 25),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    internal static double? DetectionDelay(
        int idleMs,
        int loadMs,
        double idleMhz,
        double peakMhz,
        IReadOnlyList<BoostRecoverySample> samples,
        bool recovery)
    {
        if (!double.IsFinite(idleMhz) || !double.IsFinite(peakMhz) || peakMhz < idleMhz)
            return null;

        double span = peakMhz - idleMhz;
        if (span <= 0)
            return null;

        double threshold = idleMhz + Math.Max(50.0, span * 0.20);
        int phaseStart = idleMs;
        int phaseEnd = idleMs + loadMs;
        Func<BoostRecoverySample, bool> crossed = recovery
            ? sample => sample.CurrentMhzMax <= threshold
            : sample => sample.CurrentMhzMax > threshold;
        BoostRecoveryPhase expectedPhase = recovery ? BoostRecoveryPhase.Recovery : BoostRecoveryPhase.Load;

        return samples
            .Where(sample => sample.Phase == expectedPhase
                && sample.TimeMs >= (recovery ? phaseEnd : phaseStart)
                && sample.TimeMs < (recovery ? phaseEnd + 24 * 3600_000 : phaseEnd)
                && crossed(sample))
            .OrderBy(sample => sample.TimeMs)
            .Select(sample => sample.TimeMs - (recovery ? phaseEnd : phaseStart))
            .Cast<double?>()
            .FirstOrDefault();
    }

    private static void ValidateRun(BoostRecoveryWorkload workload, BoostRecoveryRun run)
    {
        if (run.Rounds.Count != workload.Rounds)
        {
            throw new BoostRecoveryValidationException(
                $"脈衝場次樣本不足：需要 {workload.Rounds} rounds，實得 {run.Rounds.Count}。");
        }

        for (int roundIndex = 0; roundIndex < run.Rounds.Count; roundIndex++)
        {
            IReadOnlyList<BoostRecoverySample> samples = run.Rounds[roundIndex].Samples;
            bool hasIdle = samples.Any(sample => sample.Phase == BoostRecoveryPhase.Idle);
            bool hasLoad = samples.Any(sample => sample.Phase == BoostRecoveryPhase.Load);
            bool hasRecovery = samples.Any(sample => sample.Phase == BoostRecoveryPhase.Recovery);
            if (samples.Count < 3 || !hasIdle || !hasLoad || !hasRecovery)
            {
                throw new BoostRecoveryValidationException(
                    $"脈衝狀態樣本缺失（round {roundIndex + 1}）：需要 idle、load 與 recovery 快照；不內插缺失段。");
            }

            double previousTime = -1;
            foreach (BoostRecoverySample sample in samples)
            {
                if (!double.IsFinite(sample.TimeMs)
                    || sample.TimeMs < previousTime
                    || !double.IsFinite(sample.CurrentMhzMin)
                    || !double.IsFinite(sample.CurrentMhzMax)
                    || !double.IsFinite(sample.MhzLimitMin)
                    || !double.IsFinite(sample.MhzLimitMax)
                    || sample.CurrentMhzMin <= 0
                    || sample.CurrentMhzMin > sample.CurrentMhzMax
                    || sample.MhzLimitMin <= 0
                    || sample.MhzLimitMin > sample.MhzLimitMax)
                {
                    throw new BoostRecoveryValidationException(
                        $"脈衝頻率樣本非有限、亂序或無效（round {roundIndex + 1}）；不輸出可疑曲線。");
                }

                previousTime = sample.TimeMs;
            }
        }
    }

    private static IReadOnlyList<DeepBenchMetric> CreateMetrics(BoostRecoveryWorkload workload, BoostRecoveryRun run)
    {
        var idleMax = new List<double>();
        var loadPeak = new List<double>();
        var loadSteady = new List<double>();
        var recoveryFinal = new List<double>();
        var sag = new List<double>();
        var ramp = new List<double>();
        var recovery = new List<double>();
        var limitChanges = new List<double>();

        for (int roundIndex = 0; roundIndex < run.Rounds.Count; roundIndex++)
        {
            BoostRecoverySample[] samples = [.. run.Rounds[roundIndex].Samples];
            double idle = samples.Where(sample => sample.Phase == BoostRecoveryPhase.Idle).Max(sample => sample.CurrentMhzMax);
            double peak = samples.Where(sample => sample.Phase == BoostRecoveryPhase.Load).Max(sample => sample.CurrentMhzMax);
            double steady = Median(samples
                .Where(sample => sample.Phase == BoostRecoveryPhase.Load)
                .Skip(samples.Count(sample => sample.Phase == BoostRecoveryPhase.Load) / 2)
                .Select(sample => sample.CurrentMhzMax));
            double final = Median(samples
                .Where(sample => sample.Phase == BoostRecoveryPhase.Recovery)
                .Skip(samples.Count(sample => sample.Phase == BoostRecoveryPhase.Recovery) / 2)
                .Select(sample => sample.CurrentMhzMax));

            idleMax.Add(idle);
            loadPeak.Add(peak);
            loadSteady.Add(steady);
            recoveryFinal.Add(final);
            sag.Add(peak > 0 ? Math.Max(0, (peak - steady) / peak * 100) : 0);

            int idleMs = workload.IdleMs;
            int loadMs = workload.LoadMs;
            double? rampDelay = DetectionDelay(idleMs, loadMs, idle, peak, samples, recovery: false);
            double? recoveryDelay = DetectionDelay(idleMs, loadMs, idle, peak, samples, recovery: true);
            if (rampDelay is { } rampValue) ramp.Add(rampValue);
            if (recoveryDelay is { } recoveryValue) recovery.Add(recoveryValue);

            int changes = 0;
            for (int index = 1; index < samples.Length; index++)
            {
                changes += samples[index - 1].CurrentMhzMin != samples[index].CurrentMhzMin
                    || samples[index - 1].CurrentMhzMax != samples[index].CurrentMhzMax
                    || samples[index - 1].MhzLimitMin != samples[index].MhzLimitMin
                    || samples[index - 1].MhzLimitMax != samples[index].MhzLimitMax
                    ? 1 : 0;
            }

            limitChanges.Add(changes);
        }

        string config = $"{run.Rounds.Count} rounds";
        var metrics = new List<DeepBenchMetric>
        {
            Metric("gauntlet.boost.idle-mhz.max", "Idle reported MHz max", "MHz", true, idleMax, config),
            Metric("gauntlet.boost.load-peak-mhz.max", "Load peak reported MHz", "MHz", true, loadPeak, config),
            Metric("gauntlet.boost.load-steady-mhz.median", "Load steady reported MHz median", "MHz", true, loadSteady, config),
            Metric("gauntlet.boost.recovery-final-mhz.median", "Recovery final reported MHz median", "MHz", true, recoveryFinal, config),
            Metric("gauntlet.boost.load-sag-percent", "Load peak-to-steady sag", "%", false, sag, config),
            Metric("gauntlet.boost.limit-change.count", "Frequency snapshot changes", "count", false, limitChanges, config),
        };
        if (ramp.Count > 0)
            metrics.Add(Metric("gauntlet.boost.ramp-detection.ms", "Boost ramp detection interval", "ms", false, ramp, config));
        if (recovery.Count > 0)
            metrics.Add(Metric("gauntlet.boost.recovery-detection.ms", "Recovery detection interval", "ms", false, recovery, config));
        return metrics;
    }

    private static DeepBenchMetric Metric(
        string id,
        string title,
        string unit,
        bool higherIsBetter,
        IReadOnlyList<double> samples,
        string configuration) => new(
        id,
        title,
        unit,
        higherIsBetter,
        configuration,
        samples,
        samples.Select((value, index) => new DeepBenchMetricPoint(
            value,
            new Dictionary<string, string> { ["round"] = (index + 1).ToString(CultureInfo.InvariantCulture) },
            [value])).ToArray());

    private static double Median(IEnumerable<double> samples)
    {
        double[] values = [.. samples.OrderBy(value => value)];
        if (values.Length == 0)
            throw new BoostRecoveryValidationException("聚合樣本為空；不產生中位數。");
        int middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2.0;
    }

    private static string[] CreateConditions(BoostRecoveryWorkload workload, BoostRecoveryRun run)
    {
        int rampRounds = CountDetectedRounds(workload, run, recovery: false);
        int recoveryRounds = CountDetectedRounds(workload, run, recovery: true);
        return
        [
            $"{workload.Rounds} rounds × {workload.IdleMs}/{workload.LoadMs}/{workload.RecoveryMs} ms @ {workload.SamplingIntervalMs} ms；保留 {run.Rounds.Sum(round => round.Samples.Count)} 個電源 API 快照。",
            rampRounds == 0
                ? "沒有觀察到可報告的爬升；不推算 boost ramp 時間。"
                : $"觀察到 {rampRounds}/{workload.Rounds} 輪爬升；時間是取樣間距上界，不是韌體內部轉換時間。",
            recoveryRounds == 0
                ? "沒有觀察到可報告的恢復；不推算 recovery 時間。"
                : $"觀察到 {recoveryRounds}/{workload.Rounds} 輪恢復；時間是取樣間距上界，不是溫度或功耗平衡完成時間。",
        ];
    }

    private static int CountDetectedRounds(BoostRecoveryWorkload workload, BoostRecoveryRun run, bool recovery)
    {
        int count = 0;
        foreach (BoostRecoveryRound round in run.Rounds)
        {
            BoostRecoverySample[] samples = [.. round.Samples];
            BoostRecoverySample[] loadSamples = [.. samples.Where(sample => sample.Phase == BoostRecoveryPhase.Load)];
            BoostRecoverySample[] idleSamples = [.. samples.Where(sample => sample.Phase == BoostRecoveryPhase.Idle)];
            int idleMs = workload.IdleMs;
            int loadMs = workload.LoadMs;
            double idle = idleSamples.Max(sample => sample.CurrentMhzMax);
            double peak = loadSamples.Max(sample => sample.CurrentMhzMax);
            if (DetectionDelay(idleMs, loadMs, idle, peak, samples, recovery).HasValue)
                count++;
        }

        _ = workload;
        return count;
    }

    private static string Configuration(BoostRecoveryWorkload workload) =>
        $"{workload.Rounds} rounds × {workload.IdleMs}/{workload.LoadMs}/{workload.RecoveryMs} ms @ {workload.SamplingIntervalMs} ms；all-core managed pulse";

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消後停止全核心脈衝，不輸出未完成樣本。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["脈衝或電源取樣不完整時不推算缺失曲線。"], kind, error);
}
