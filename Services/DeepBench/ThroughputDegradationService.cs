using System.Globalization;

namespace XinSpect;

public interface IThroughputDegradationEngine
{
    Task<ThroughputDegradationRun> MeasureAsync(
        ThroughputDegradationWorkload workload,
        CancellationToken cancellationToken);
}

public sealed record ThroughputDegradationWorkload(
    int WarmupMs,
    int Windows,
    int WindowMs,
    int CooldownMs);

public sealed record ThroughputWindow(
    int Index,
    double DurationMs,
    double Operations,
    double OperationsPerSecond,
    double CurrentMhzMin,
    double CurrentMhzMax,
    double MhzLimitMin,
    double MhzLimitMax);

public sealed record ThroughputDegradationRun(
    int LogicalProcessors,
    IReadOnlyList<ThroughputWindow> Windows);

public sealed class ThroughputDegradationUnsupportedException(string message) : InvalidOperationException(message);
public sealed class ThroughputDegradationValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// Throughput degradation：以 managed 全核心分窗長跑實測 operations，
/// 並同窗取樣 Windows 電源 API 的 P-state 上限換算值，輸出 early/late 保留率與相鄰窗最大下降。
/// </summary>
public sealed class ThroughputDegradationService(IThroughputDegradationEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "gauntlet.throughput-degradation";

    public static string[] Limitations { get; } =
    [
        "吞吐是 managed operations 實測；受 CLR、JIT、排程、CPU 親和性、背景工作與電源政策影響，不是 ISA 峰值或廠商 TDP 承諾。",
        "PROCESSOR_POWER_INFORMATION.CurrentMhz 是 P-state 上限換算值，不是核心實際有效時脈；不把頻率當作吞吐或用頻率內插 operations。",
        "分窗吞吐是整窗平均值；窗內微秒級抖動、SMT 鄰居干擾與單次背景排程會被平均值稀釋。",
        "early/late 使用前後各 25% 分窗中位數；不做加權總分、不外推長時間老化、不宣稱溫度或功耗成因。",
        "電源 API 失敗、回報值不完整或分窗樣本無效時整場拒收；不輸出內插或預設值。",
    ];

    private readonly IThroughputDegradationEngine _engine = engine ?? new WindowsThroughputDegradationEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThroughputDegradationWorkload workload = GetSettings(context.Profile);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.03, "準備全核心分窗吞吐負載"));
            ThroughputDegradationRun run = await _engine.MeasureAsync(workload, cancellationToken).ConfigureAwait(false);
            ValidateRun(workload, run);

            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.94, "整理分窗吞吐與衰退指標"));
            return new DeepBenchTestResult(
                TestId,
                context.SessionId,
                context.Profile,
                started,
                DateTime.UtcNow,
                Configuration(workload, run),
                CreateMetrics(run),
                CreateConditions(workload, run),
                Limitations,
                DeepBenchFailureKind.None,
                null);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (ThroughputDegradationUnsupportedException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unsupported, exception.Message);
        }
        catch (ThroughputDegradationValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static ThroughputDegradationWorkload GetSettings(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(300, 4, 800, 150),
        DeepBenchRunProfile.Full => new(500, 8, 1600, 300),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    private static void ValidateRun(ThroughputDegradationWorkload workload, ThroughputDegradationRun run)
    {
        if (run.LogicalProcessors <= 0)
            throw new ThroughputDegradationValidationException("未回報任何邏輯處理器；不輸出空吞吐結果。");
        if (run.Windows.Count != workload.Windows)
        {
            throw new ThroughputDegradationValidationException(
                $"分窗樣本窗數不足：需要 {workload.Windows} 窗，實得 {run.Windows.Count} 窗；不補窗、不內插。");
        }

        for (int index = 0; index < run.Windows.Count; index++)
        {
            ThroughputWindow window = run.Windows[index];
            if (window.Index != index
                || !double.IsFinite(window.DurationMs) || window.DurationMs <= 0
                || !double.IsFinite(window.Operations) || window.Operations <= 0
                || !double.IsFinite(window.OperationsPerSecond) || window.OperationsPerSecond <= 0
                || !double.IsFinite(window.CurrentMhzMin) || window.CurrentMhzMin <= 0
                || !double.IsFinite(window.CurrentMhzMax) || window.CurrentMhzMax < window.CurrentMhzMin
                || !double.IsFinite(window.MhzLimitMin) || window.MhzLimitMin <= 0
                || !double.IsFinite(window.MhzLimitMax) || window.MhzLimitMax < window.MhzLimitMin)
            {
                throw new ThroughputDegradationValidationException(
                    $"吞吐窗 {index + 1} 樣本非有限、無效或序號不符；整場拒收。");
            }
        }
    }

    private static IReadOnlyList<DeepBenchMetric> CreateMetrics(ThroughputDegradationRun run)
    {
        double[] windowMops = [.. run.Windows.Select(window => window.OperationsPerSecond / 1_000_000.0)];
        int edgeCount = Math.Max(1, run.Windows.Count / 4);
        double early = Median(windowMops.Take(edgeCount));
        double late = Median(windowMops.TakeLast(edgeCount));
        double retention = RoundPercent(early > 0 ? late / early * 100 : 0);
        double degradation = RoundPercent(Math.Max(0, 100 - retention));
        double maxDrop = RoundPercent(MaxAdjacentDrop(windowMops));

        string configuration = $"{run.Windows.Count} windows";
        var metrics = new List<DeepBenchMetric>
        {
            Metric("gauntlet.throughput.window.mops", "Managed throughput per window", "MOPS", true, windowMops, configuration),
            Metric("gauntlet.throughput.early.mops", "Early-window throughput median", "MOPS", true, [early], configuration),
            Metric("gauntlet.throughput.late.mops", "Late-window throughput median", "MOPS", true, [late], configuration),
            Metric("gauntlet.throughput.retention.percent", "Late/early throughput retention", "%", true, [retention], configuration),
            Metric("gauntlet.throughput.degradation.percent", "Early-to-late throughput degradation", "%", false, [degradation], configuration),
            Metric("gauntlet.throughput.max-window-drop.percent", "Maximum adjacent-window drop", "%", false, [maxDrop], configuration),
            Metric(
                "gauntlet.throughput.current-mhz.max",
                "Maximum reported CurrentMhz per window",
                "MHz",
                true,
                [.. run.Windows.Select(window => window.CurrentMhzMax)],
                configuration),
        };
        return metrics;
    }

    private static double Median(IEnumerable<double> samples)
    {
        double[] values = [.. samples.OrderBy(value => value)];
        if (values.Length == 0)
            throw new ThroughputDegradationValidationException("early/late 聚合樣本為空；不產生中位數。");

        int middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2.0;
    }

    private static double MaxAdjacentDrop(double[] values)
    {
        double max = 0;
        for (int index = 1; index < values.Length; index++)
        {
            if (values[index] < values[index - 1])
                max = Math.Max(max, (values[index - 1] - values[index]) / values[index - 1] * 100);
        }

        return max;
    }

    private static double RoundPercent(double value) => Math.Round(value, 3);

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
            new Dictionary<string, string> { ["window"] = (index + 1).ToString(CultureInfo.InvariantCulture) },
            [value])).ToArray());

    private static string[] CreateConditions(ThroughputDegradationWorkload workload, ThroughputDegradationRun run)
    {
        int edgeCount = Math.Max(1, workload.Windows / 4);
        return
        [
            $"{workload.Windows} windows × {workload.WindowMs} ms；{workload.WarmupMs} ms warmup、{workload.CooldownMs} ms cooldown；全核心 managed throughput。",
            $"early 與 late 是 first 25% 與 last 25%（每側 {edgeCount} 窗）吞吐中位數；保留 {run.LogicalProcessors} logical processors 的全核心觀察。",
            "CurrentMhz 只是同窗電源 API 上限換算值範圍；不做頻率外推，也不宣稱造成衰退的熱、功耗或韌體機制。",
        ];
    }

    private static string Configuration(ThroughputDegradationWorkload workload, ThroughputDegradationRun run) =>
        $"{run.LogicalProcessors} logical processors；{workload.Windows} windows × {workload.WindowMs} ms；all-core managed throughput";

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消後停止全核心 workers，不輸出未完成分窗樣本。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["吞吐或電源取樣不完整時不推算缺失分窗。"], kind, error);
}
