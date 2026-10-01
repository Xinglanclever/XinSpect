using System.Diagnostics;

namespace XinSpect;

public sealed record DramStrideEnvironment(long AvailableMemoryBytes, string Source);

public interface IDramStrideEnvironmentProbe
{
    DramStrideEnvironment Detect();
}

public sealed record DramStrideSettings(
    long WorkingSetBytes,
    IReadOnlyList<int> StrideBytes,
    int WarmupPasses,
    int MeasurePasses);

public sealed record DramStridePlan(
    long WorkingSetBytes,
    IReadOnlyList<int> StrideBytes,
    DramStrideSettings Settings);

public sealed record DramStridePoint(
    int StrideBytes,
    long WorkingSetBytes,
    IReadOnlyList<double> NsPerAccessSamples);

public sealed record DramStrideMeasurement(IReadOnlyList<DramStridePoint> Points);

public sealed record DramStrideContext(
    DramStridePlan Plan,
    DeepBenchRunProfile Profile,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public interface IDramStrideEngine
{
    Task<DramStrideMeasurement> MeasureAsync(DramStrideContext context, CancellationToken cancellationToken);
}

public sealed class DramStrideValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// DRAM 映射推論：以遞增 stride 循序走訪大型 working set，畫出每筆存取成本隨 stride 的變化曲線。
/// 大 stride 下的成本階梯與 row activation／bank conflict 假說一致，但記憶體控制器的
/// channel／rank／bank 位址雜湊無法從使用者模式得知——全部結果僅為推論，不宣稱確定映射。
/// </summary>
public sealed class DramMappingInferenceService(
    IDramStrideEnvironmentProbe? probe = null,
    IDramStrideEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "memory.dram-mapping-inference";
    private const long BytesPerPage = 4096;
    private const long MinimumWorkingSetBytes = 64L * 1024 * 1024;
    private const double WorkingSetBudgetFraction = 0.4;

    public static string[] Limitations { get; } =
    [
        "結果僅為推論：曲線在大 stride 處的階梯可能來自 row activation、bank／rank conflict、TLB miss 或 page table walk；記憶體控制器的位址雜湊無法從使用者模式得知，不宣稱任何確定的 row／bank／rank 映射。",
        "managed 陣列所在的實體分頁由 Windows 決定，本程式不控制也觀察不到實體位址；量的是虛擬位址空間的可重現訪問模式。",
        "循序 stride 走訪讓小 stride 由 prefetch 部分餵養（跨頁的大 stride 通常停在頁邊界不被預取）；曲線同時包含 cache、prefetch、TLB 與 DRAM 成分，不拆分。",
        "各 stride 的原始樣本與明示比率並列輸出；不合成單一總分，不外推到其他訪問模式。",
    ];

    private readonly IDramStrideEnvironmentProbe _probe = probe ?? new WindowsDramStrideProbe();
    private readonly IDramStrideEngine _engine = engine ?? new WindowsDramStrideEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "檢查可用記憶體"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DramStrideEnvironment environment = _probe.Detect();
            DramStrideSettings settings = GetSettings(context.Profile);
            DramStridePlan? plan = BuildPlan(environment, settings);
            if (plan is null)
            {
                return Unsupported(context, started,
                    $"可用記憶體不足以支撐最小 {MinimumWorkingSetBytes / 1024.0 / 1024.0:0} MiB working set（實得 {environment.AvailableMemoryBytes / 1024.0 / 1024.0:0} MiB）。");
            }

            var engineContext = new DramStrideContext(plan, context.Profile, context.Progress, cancellationToken);
            DramStrideMeasurement measurement = await _engine
                .MeasureAsync(engineContext, cancellationToken)
                .ConfigureAwait(false);
            Validate(measurement, plan);
            return CreateResult(context, started, environment, plan, measurement);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (DramStrideValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static DramStrideSettings GetSettings(DeepBenchRunProfile profile)
    {
        const long mib = 1024L * 1024;
        return profile switch
        {
            DeepBenchRunProfile.Quick => new(
                768 * mib,
                [256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536],
                1, 3),
            DeepBenchRunProfile.Full => new(
                1536 * mib,
                [256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288],
                1, 7),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };
    }

    internal static DramStridePlan? BuildPlan(DramStrideEnvironment environment, DramStrideSettings settings)
    {
        long budget = Math.Max(0, (long)(environment.AvailableMemoryBytes * WorkingSetBudgetFraction));
        long workingSet = settings.WorkingSetBytes;
        if (workingSet > budget)
            workingSet = budget / (256L * 1024 * 1024) * 256L * 1024 * 1024;
        if (workingSet < MinimumWorkingSetBytes || settings.StrideBytes.Count == 0)
            return null;
        if (settings.StrideBytes.Any(stride => stride <= 0 || stride % 8 != 0))
            return null;
        return new DramStridePlan(workingSet, [.. settings.StrideBytes], settings);
    }

    internal static string FormatStride(int strideBytes) => strideBytes switch
    {
        >= 1024 * 1024 => $"{strideBytes / (1024 * 1024)}mib",
        >= 1024 => $"{strideBytes / 1024}kib",
        _ => $"{strideBytes}b",
    };

    private static void Validate(DramStrideMeasurement measurement, DramStridePlan plan)
    {
        if (measurement.Points.Count != plan.StrideBytes.Count)
            throw new DramStrideValidationException("stride 掃描點數不完整；不推算缺失點。");
        for (int index = 0; index < measurement.Points.Count; index++)
        {
            DramStridePoint point = measurement.Points[index];
            if (point.StrideBytes != plan.StrideBytes[index])
                throw new DramStrideValidationException($"stride 掃描第 {index + 1} 點步進與規劃不一致。");
            if (point.WorkingSetBytes != plan.WorkingSetBytes)
                throw new DramStrideValidationException($"stride 掃描第 {index + 1} 點 working set 與規劃不一致。");
            if (point.NsPerAccessSamples.Count != plan.Settings.MeasurePasses || point.NsPerAccessSamples.Count == 0)
                throw new DramStrideValidationException($"stride 掃描第 {index + 1} 點樣本數不一致；不推算缺失樣本。");
            if (point.NsPerAccessSamples.Any(value => !double.IsFinite(value) || value <= 0))
                throw new DramStrideValidationException($"stride 掃描第 {index + 1} 點出現非有限或非正數樣本；整場拒收。");
        }
    }

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        DramStrideEnvironment environment,
        DramStridePlan plan,
        DramStrideMeasurement measurement)
    {
        List<DeepBenchMetric> metrics = [];
        foreach (DramStridePoint point in measurement.Points)
        {
            metrics.Add(new(
                $"memory.dram-stride.{FormatStride(point.StrideBytes)}.ns-per-access",
                $"stride {FormatStride(point.StrideBytes)}",
                "ns/access",
                false,
                "managed 循序 stride 走訪",
                point.NsPerAccessSamples,
                []));
        }

        double smallest = Median(measurement.Points[0].NsPerAccessSamples);
        double largest = Median(measurement.Points[^1].NsPerAccessSamples);
        metrics.Add(new(
            $"memory.dram-stride.{FormatStride(measurement.Points[^1].StrideBytes)}-over-{FormatStride(measurement.Points[0].StrideBytes)}.ratio",
            $"大 stride 相對小 stride 的每筆存取成本",
            "ratio",
            true,
            $"median(stride {FormatStride(measurement.Points[^1].StrideBytes)}) / median(stride {FormatStride(measurement.Points[0].StrideBytes)})",
            [largest / smallest],
            []));

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"working set {plan.WorkingSetBytes / 1024.0 / 1024.0:0} MiB；stride {plan.StrideBytes.Count} 點 × {plan.Settings.MeasurePasses} 回",
            metrics,
            [
                $"環境來源：{environment.Source}；可用記憶體 {environment.AvailableMemoryBytes / 1024.0 / 1024.0:0} MiB。",
                $"stride 序列：{string.Join("、", plan.StrideBytes.Select(FormatStride))}；每 pass 以固定 stride 循序走訪 working set 一圈，大 stride 以多圈補足存取數。",
                "大 stride 的成本階梯僅與 row activation／bank conflict 假說一致；映射本身無法從使用者模式確定，結論一律視為推論。",
            ],
            Limitations,
            DeepBenchFailureKind.None,
            null);
    }

    internal static double Median(IReadOnlyList<double> samples)
    {
        double[] sorted = [.. samples];
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2d;
    }

    private static DeepBenchTestResult Unsupported(DeepBenchRunContext context, DateTime started, string reason) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [],
            Limitations, DeepBenchFailureKind.Unsupported, reason);

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消後不輸出部分掃描補值。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["量測不完整時不推算缺失 stride。"], kind, error);
}

public sealed class WindowsDramStrideProbe : IDramStrideEnvironmentProbe
{
    public DramStrideEnvironment Detect() => new(
        MemoryNumaTlbInterop.AvailablePhysicalMemoryBytes(),
        "GlobalMemoryStatusEx");
}

/// <summary>Windows 實測引擎：managed long[] + 固定種子頁序排列，逐 stride 計時。</summary>
public sealed class WindowsDramStrideEngine : IDramStrideEngine
{
    private const long BytesPerPage = 4096;
    private const long LongsPerPage = BytesPerPage / sizeof(long);
    private const long MinimumAccessesPerPass = 1_000_000;

    public Task<DramStrideMeasurement> MeasureAsync(
        DramStrideContext context,
        CancellationToken cancellationToken)
        => Task.Run(() => Measure(context, cancellationToken), cancellationToken);

    private static DramStrideMeasurement Measure(DramStrideContext context, CancellationToken cancellationToken)
    {
        DramStridePlan plan = context.Plan;
        long[] buffer = new long[plan.WorkingSetBytes / sizeof(long)];
        for (long index = 0; index < buffer.Length; index += LongsPerPage)
            buffer[index] = index;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int totalSteps = plan.StrideBytes.Count * plan.Settings.MeasurePasses;
        int completed = 0;
        List<DramStridePoint> points = [];
        foreach (int stride in plan.StrideBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int strideLongs = stride / sizeof(long);
            long accessesPerLap = buffer.Length / strideLongs;
            long laps = Math.Max(1, (MinimumAccessesPerPass + accessesPerLap - 1) / accessesPerLap);
            List<double> samples = [];
            int passes = plan.Settings.WarmupPasses + plan.Settings.MeasurePasses;
            for (int pass = 0; pass < passes; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool warmup = pass < plan.Settings.WarmupPasses;
                context.Progress.Report(new DeepBenchProgress(
                    DramMappingInferenceService.TestId,
                    completed,
                    Math.Max(totalSteps, 1),
                    0.05 + 0.92 * completed / Math.Max(totalSteps, 1),
                    warmup
                        ? $"warmup stride {DramMappingInferenceService.FormatStride(stride)}"
                        : $"掃描 stride {DramMappingInferenceService.FormatStride(stride)}（第 {pass - plan.Settings.WarmupPasses + 1}/{plan.Settings.MeasurePasses} 回）"));
                long timestamp = Stopwatch.GetTimestamp();
                long sink = Walk(buffer, strideLongs, laps);
                double elapsedNs = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds * 1_000_000d;
                if (sink == long.MaxValue)
                    throw new DramStrideValidationException("掃描累加值異常；整場拒收。");
                if (!warmup)
                    samples.Add(elapsedNs / (double)(laps * accessesPerLap));
                completed++;
            }

            points.Add(new DramStridePoint(stride, plan.WorkingSetBytes, samples));
        }

        context.Progress.Report(new DeepBenchProgress(
            DramMappingInferenceService.TestId, totalSteps, Math.Max(totalSteps, 1), 0.98, "stride 掃描完成"));
        return new DramStrideMeasurement(points);
    }

    /// <summary>固定種子 Fisher–Yates：可重現的偽隨機排列，供需要打亂頁序的後續實驗使用。</summary>
    internal static int[] BuildPageOrder(int pageCount)
    {
        uint seed = 0x9E3779B9u;
        var order = new int[pageCount];
        for (int index = 0; index < pageCount; index++)
            order[index] = index;
        for (int index = pageCount - 1; index > 0; index--)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            int swap = (int)(seed % (uint)(index + 1));
            (order[index], order[swap]) = (order[swap], order[index]);
        }
        return order;
    }

    private static long Walk(long[] buffer, int strideLongs, long laps)
    {
        long accumulator = 0;
        for (long lap = 0; lap < laps; lap++)
        {
            for (long offset = 0; offset < buffer.Length; offset += strideLongs)
                accumulator += buffer[offset];
        }
        return accumulator;
    }
}
