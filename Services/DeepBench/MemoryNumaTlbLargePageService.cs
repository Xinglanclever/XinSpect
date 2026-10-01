using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XinSpect;

public sealed record MemoryTlbLargePageEnvironment(
    int NumaNodeCount,
    uint LargePageMinimumBytes,
    bool LargePageAvailable,
    string? LargePageUnavailableReason,
    long AvailableMemoryBytes,
    string Source);

public interface IMemoryNumaTlbLargePageProbe
{
    MemoryTlbLargePageEnvironment Detect();
}

public sealed record MemoryNumaTlbLargePageSettings(
    IReadOnlyList<long> TlbWorkingSetsBytes,
    long LargePageWorkingSetBytes,
    long NumaWorkingSetBytes,
    int WarmupPasses,
    int MeasurePasses,
    int NumaMeasurePasses);

public sealed record MemoryNumaTlbLargePagePlan(
    IReadOnlyList<long> TlbWorkingSetsBytes,
    bool IncludeLargePageComparison,
    uint LargePageBytes,
    long LargePageWorkingSetBytes,
    bool IncludeNumaComparison,
    int NumaLocalNode,
    int NumaRemoteNode,
    MemoryNumaTlbLargePageSettings Settings);

public sealed record MemoryTlbWorkingSetPoint(
    long WorkingSetBytes,
    int PageSizeBytes,
    IReadOnlyList<double> NsPerPageSamples);

public sealed record MemoryLargePagePoint(
    long WorkingSetBytes,
    IReadOnlyList<double> RegularNsPerPageSamples,
    IReadOnlyList<double> LargeNsPerPageSamples);

public sealed record MemoryNumaPoint(
    int LocalNode,
    int RemoteNode,
    IReadOnlyList<double> LocalNsPerPageSamples,
    IReadOnlyList<double> RemoteNsPerPageSamples);

public sealed record MemoryNumaTlbLargePageMeasurement(
    IReadOnlyList<MemoryTlbWorkingSetPoint> TlbPoints,
    MemoryLargePagePoint? LargePagePoint,
    MemoryNumaPoint? NumaPoint);

public sealed record MemoryNumaTlbLargePageContext(
    MemoryNumaTlbLargePagePlan Plan,
    DeepBenchRunProfile Profile,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public interface IMemoryNumaTlbLargePageEngine
{
    Task<MemoryNumaTlbLargePageMeasurement> MeasureAsync(
        MemoryNumaTlbLargePageContext context,
        CancellationToken cancellationToken);
}

public sealed class MemoryNumaTlbLargePageValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// NUMA／TLB／大分頁深測：遞增 working set 的逐頁掃描曲線，加上大分頁與跨 NUMA 節點對照。
/// 三個子項各自判定適用性；不適用的子項如實標示原因，不降級、不猜測、不合成總分。
/// </summary>
public sealed class MemoryNumaTlbLargePageService(
    IMemoryNumaTlbLargePageProbe? probe = null,
    IMemoryNumaTlbLargePageEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "memory.numa-tlb-largepage";
    private const long BytesPerPage = 4096;
    private const int MinimumTlbPoints = 2;
    private const double WorkingSetBudgetFraction = 0.5;

    public static string[] Limitations { get; } =
    [
        "TLB 掃描量的是遞增 working set 下本程式逐頁讀寫的每頁成本，是 TLB、prefetch、快取與 page table walk 的混合效應；不拆出單一硬體 TLB 規格，也不宣稱量到 DTLB 大小。",
        "大分頁對照需要 SeLockMemoryPrivilege（群組原則「鎖定分頁」）且實際配置成功；啟用或配置失敗時如實標示未執行，不降級宣稱。",
        "跨 NUMA 對照需要至少兩個 NUMA 節點；單節點機器如實標示不適用。",
        "多處理器群組與多 NUMA 節點並存的路徑未在實機全面驗證。",
        "各 working set、大分頁與 NUMA 對照並列輸出原始樣本與明示比率；不合成單一總分。",
    ];

    private readonly IMemoryNumaTlbLargePageProbe _probe = probe ?? new WindowsMemoryNumaTlbLargePageProbe();
    private readonly IMemoryNumaTlbLargePageEngine _engine = engine ?? new WindowsMemoryNumaTlbLargePageEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "探測 NUMA／大分頁環境"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            MemoryTlbLargePageEnvironment environment = _probe.Detect();
            MemoryNumaTlbLargePageSettings settings = GetSettings(context.Profile);
            MemoryNumaTlbLargePagePlan? plan = BuildPlan(environment, settings);
            if (plan is null)
            {
                return Unsupported(context, started,
                    $"三個子項都不適用：可用記憶體 {environment.AvailableMemoryBytes / 1024.0 / 1024.0:0} MiB、NUMA 節點 {environment.NumaNodeCount} 個、大分頁 {(environment.LargePageAvailable ? "可用" : "不可用")}。");
            }

            var engineContext = new MemoryNumaTlbLargePageContext(plan, context.Profile, context.Progress, cancellationToken);
            MemoryNumaTlbLargePageMeasurement measurement = await _engine
                .MeasureAsync(engineContext, cancellationToken)
                .ConfigureAwait(false);
            Validate(measurement, plan);
            return CreateResult(context, started, environment, plan, measurement);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (MemoryNumaTlbLargePageValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static MemoryNumaTlbLargePageSettings GetSettings(DeepBenchRunProfile profile)
    {
        const long mib = 1024L * 1024;
        return profile switch
        {
            DeepBenchRunProfile.Quick => new([2 * mib, 8 * mib, 32 * mib, 128 * mib], 64 * mib, 64 * mib, 1, 3, 5),
            DeepBenchRunProfile.Full => new(
                [2 * mib, 8 * mib, 32 * mib, 128 * mib, 512 * mib, 1024 * mib], 256 * mib, 256 * mib, 1, 5, 11),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };
    }

    internal static MemoryNumaTlbLargePagePlan? BuildPlan(
        MemoryTlbLargePageEnvironment environment,
        MemoryNumaTlbLargePageSettings settings)
    {
        long budget = Math.Max(0, (long)(environment.AvailableMemoryBytes * WorkingSetBudgetFraction));
        List<long> tlbSets = [.. settings.TlbWorkingSetsBytes.Where(size => size > 0 && size <= budget)];
        bool includeTlb = tlbSets.Count >= MinimumTlbPoints;
        bool includeLargePage = environment.LargePageAvailable && environment.LargePageMinimumBytes > 0
            && settings.LargePageWorkingSetBytes > 0
            && settings.LargePageWorkingSetBytes % environment.LargePageMinimumBytes == 0
            && settings.LargePageWorkingSetBytes <= budget;
        bool includeNuma = environment.NumaNodeCount >= 2
            && settings.NumaWorkingSetBytes > 0 && settings.NumaWorkingSetBytes <= budget;

        if (!includeTlb && !includeLargePage && !includeNuma)
            return null;

        return new MemoryNumaTlbLargePagePlan(
            includeTlb ? tlbSets : [],
            includeLargePage,
            environment.LargePageMinimumBytes,
            settings.LargePageWorkingSetBytes,
            includeNuma,
            includeNuma ? 0 : -1,
            includeNuma ? Math.Min(1, environment.NumaNodeCount - 1) : -1,
            settings);
    }

    internal static string FormatWorkingSet(long bytes)
    {
        const long gib = 1024L * 1024 * 1024;
        const long mib = 1024L * 1024;
        return bytes >= gib ? $"{bytes / gib}gib" : $"{bytes / mib}mib";
    }

    private static void Validate(MemoryNumaTlbLargePageMeasurement measurement, MemoryNumaTlbLargePagePlan plan)
    {
        if (plan.TlbWorkingSetsBytes.Count > 0)
        {
            if (measurement.TlbPoints.Count != plan.TlbWorkingSetsBytes.Count)
                throw new MemoryNumaTlbLargePageValidationException("TLB 掃描 working set 點數不完整；不推算缺失點。");
            for (int index = 0; index < measurement.TlbPoints.Count; index++)
            {
                MemoryTlbWorkingSetPoint point = measurement.TlbPoints[index];
                if (point.WorkingSetBytes != plan.TlbWorkingSetsBytes[index])
                    throw new MemoryNumaTlbLargePageValidationException($"TLB 掃描 working set 第 {index + 1} 點大小與規劃不一致。");
                if (point.PageSizeBytes != BytesPerPage)
                    throw new MemoryNumaTlbLargePageValidationException($"TLB 掃描第 {index + 1} 點分頁大小不是 4 KiB。");
                if (point.NsPerPageSamples.Count != plan.Settings.MeasurePasses || point.NsPerPageSamples.Count == 0)
                    throw new MemoryNumaTlbLargePageValidationException($"TLB 掃描第 {index + 1} 點樣本數不一致；不推算缺失樣本。");
                if (point.NsPerPageSamples.Any(value => !double.IsFinite(value) || value <= 0))
                    throw new MemoryNumaTlbLargePageValidationException($"TLB 掃描第 {index + 1} 點出現非有限或非正數樣本；整場拒收。");
            }
        }
        else if (measurement.TlbPoints.Count > 0)
        {
            throw new MemoryNumaTlbLargePageValidationException("規劃不含 TLB 掃描卻回報了掃描點。");
        }

        if (plan.IncludeLargePageComparison)
        {
            if (measurement.LargePagePoint is null)
                throw new MemoryNumaTlbLargePageValidationException("大分頁對照缺失；不推算。");
            MemoryLargePagePoint point = measurement.LargePagePoint;
            if (point.WorkingSetBytes != plan.LargePageWorkingSetBytes)
                throw new MemoryNumaTlbLargePageValidationException("大分頁對照大小與規劃不一致。");
            if (point.RegularNsPerPageSamples.Count != plan.Settings.MeasurePasses
                || point.LargeNsPerPageSamples.Count != plan.Settings.MeasurePasses
                || point.RegularNsPerPageSamples.Count == 0)
                throw new MemoryNumaTlbLargePageValidationException("大分頁對照兩側樣本數不一致；不推算缺失樣本。");
            if (point.RegularNsPerPageSamples.Concat(point.LargeNsPerPageSamples)
                .Any(value => !double.IsFinite(value) || value <= 0))
                throw new MemoryNumaTlbLargePageValidationException("大分頁對照出現非有限或非正數樣本；整場拒收。");
        }

        if (plan.IncludeNumaComparison)
        {
            if (measurement.NumaPoint is null)
                throw new MemoryNumaTlbLargePageValidationException("跨 NUMA 對照缺失；不推算。");
            MemoryNumaPoint point = measurement.NumaPoint;
            if (point.LocalNode == point.RemoteNode)
                throw new MemoryNumaTlbLargePageValidationException("跨 NUMA 對照的本地與遠端節點不可相同。");
            if (point.LocalNsPerPageSamples.Count != plan.Settings.NumaMeasurePasses
                || point.RemoteNsPerPageSamples.Count != plan.Settings.NumaMeasurePasses
                || point.LocalNsPerPageSamples.Count == 0)
                throw new MemoryNumaTlbLargePageValidationException("跨 NUMA 對照兩側樣本數不一致；不推算缺失樣本。");
            if (point.LocalNsPerPageSamples.Concat(point.RemoteNsPerPageSamples)
                .Any(value => !double.IsFinite(value) || value <= 0))
                throw new MemoryNumaTlbLargePageValidationException("跨 NUMA 對照出現非有限或非正數樣本；整場拒收。");
        }
    }

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        MemoryTlbLargePageEnvironment environment,
        MemoryNumaTlbLargePagePlan plan,
        MemoryNumaTlbLargePageMeasurement measurement)
    {
        List<DeepBenchMetric> metrics = [];
        foreach (MemoryTlbWorkingSetPoint point in measurement.TlbPoints)
        {
            metrics.Add(new(
                $"memory.tlb-scan.{FormatWorkingSet(point.WorkingSetBytes)}.ns-per-page",
                $"TLB 掃描 {FormatWorkingSet(point.WorkingSetBytes)}",
                "ns/page",
                false,
                "managed 逐 4 KiB 頁讀寫、確定性偽隨機頁序",
                point.NsPerPageSamples,
                []));
        }

        if (measurement.LargePagePoint is { } large)
        {
            metrics.Add(new(
                "memory.large-page.regular.ns-per-page",
                $"大分頁對照 4 KiB 基準（{FormatWorkingSet(large.WorkingSetBytes)}）",
                "ns/page",
                false,
                "VirtualAlloc 一般分頁、逐 4 KiB 頁讀寫",
                large.RegularNsPerPageSamples,
                []));
            metrics.Add(new(
                "memory.large-page.large.ns-per-page",
                $"大分頁 {large.WorkingSetBytes / environment.LargePageMinimumBytes:0} × {environment.LargePageMinimumBytes / 1024.0 / 1024.0:0} MiB",
                "ns/page",
                false,
                "VirtualAlloc MEM_LARGE_PAGES、逐 4 KiB 頁讀寫",
                large.LargeNsPerPageSamples,
                []));
            metrics.Add(new(
                "memory.large-page.regular-over-large.ratio",
                "一般分頁 / 大分頁每頁成本",
                "ratio",
                false,
                "median(regular) / median(large)",
                [Median(large.RegularNsPerPageSamples) / Median(large.LargeNsPerPageSamples)],
                []));
        }

        if (measurement.NumaPoint is { } numa)
        {
            metrics.Add(new(
                "memory.numa.local.ns-per-page",
                $"NUMA 本地節點 {numa.LocalNode} 逐頁存取",
                "ns/page",
                false,
                "VirtualAllocExNuma 本地節點、逐 4 KiB 頁讀寫",
                numa.LocalNsPerPageSamples,
                []));
            metrics.Add(new(
                "memory.numa.remote.ns-per-page",
                $"NUMA 遠端節點 {numa.RemoteNode} 逐頁存取",
                "ns/page",
                false,
                "VirtualAllocExNuma 遠端節點、逐 4 KiB 頁讀寫",
                numa.RemoteNsPerPageSamples,
                []));
            metrics.Add(new(
                "memory.numa.remote-over-local.ratio",
                "遠端 / 本地每頁存取成本",
                "ratio",
                true,
                "median(remote) / median(local)",
                [Median(numa.RemoteNsPerPageSamples) / Median(numa.LocalNsPerPageSamples)],
                []));
        }

        List<string> conditions =
        [
            $"環境來源：{environment.Source}；NUMA 節點 {environment.NumaNodeCount} 個；可用記憶體 {environment.AvailableMemoryBytes / 1024.0 / 1024.0:0} MiB。",
            measurement.TlbPoints.Count > 0
                ? $"TLB 掃描 {measurement.TlbPoints.Count} 個 working set（{string.Join("＜", measurement.TlbPoints.Select(point => FormatWorkingSet(point.WorkingSetBytes)))}），各 {plan.Settings.MeasurePasses} 回。"
                : "TLB 掃描未執行：可用記憶體不足以支撐至少兩個 working set。",
            measurement.LargePagePoint is not null
                ? $"大分頁對照已執行：SeLockMemoryPrivilege 啟用且 {FormatWorkingSet(plan.LargePageWorkingSetBytes)} 配置成功。"
                : $"大分頁對照未執行：{environment.LargePageUnavailableReason ?? "規劃未納入。"}",
            measurement.NumaPoint is not null
                ? $"跨 NUMA 對照已執行：節點 {plan.NumaLocalNode} → {plan.NumaRemoteNode}。"
                : "跨 NUMA 對照未執行：單一 NUMA 節點機器不適用。",
        ];

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            measurement.TlbPoints.Count > 0
                ? $"TLB 掃描 {measurement.TlbPoints.Count} 點 × {plan.Settings.MeasurePasses} 回"
                + (measurement.LargePagePoint is not null ? "；大分頁對照" : "")
                + (measurement.NumaPoint is not null ? "；跨 NUMA 對照" : "")
                : "子項對照",
            metrics,
            conditions,
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

    /// <summary>與頁數互質的確定性走訪步進（4 KiB 頁數為 2 的冪時任何奇數皆互質）；避開循序存取讓 prefetch 完全主導。</summary>
    internal static int ChooseStride(int pageCount)
    {
        if (pageCount <= 2) return 1;
        int stride = pageCount / 2 | 1;
        while (stride > 1 && Gcd(stride, pageCount) != 1) stride -= 2;
        return stride;

        static int Gcd(int a, int b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return a;
        }
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
            ["量測不完整時不推算缺失子項。"], kind, error);
}

/// <summary>Windows 環境探測：NUMA 節點數、大分頁最小尺寸與 SeLockMemoryPrivilege 實測、可用實體記憶體。</summary>
public sealed class WindowsMemoryNumaTlbLargePageProbe : IMemoryNumaTlbLargePageProbe
{
    public MemoryTlbLargePageEnvironment Detect()
    {
        int nodeCount = 1;
        try
        {
            if (MemoryNumaTlbInterop.GetNumaHighestNodeNumber(out uint highest))
                nodeCount = (int)highest + 1;
        }
        catch
        {
            nodeCount = 1;
        }

        uint largePageMinimum = 0;
        try { largePageMinimum = MemoryNumaTlbInterop.GetLargePageMinimum(); }
        catch { largePageMinimum = 0; }

        bool largePageAvailable = false;
        string? largePageReason = null;
        if (largePageMinimum == 0)
        {
            largePageReason = "此系統回報不支援大分頁（GetLargePageMinimum = 0）。";
        }
        else if (!MemoryNumaTlbInterop.TryEnableLockPagesPrivilege())
        {
            largePageReason = "SeLockMemoryPrivilege 未授予此帳戶（需群組原則「鎖定分頁」）；不猜測、不降級。";
        }
        else
        {
            nint test = MemoryNumaTlbInterop.AllocateLargePages(largePageMinimum);
            if (test == 0)
            {
                largePageReason = $"SeLockMemoryPrivilege 已啟用，但大分頁實際配置失敗（Win32 錯誤 {Marshal.GetLastWin32Error()}）。";
            }
            else
            {
                MemoryNumaTlbInterop.Free(test);
                largePageAvailable = true;
            }
        }

        long available = MemoryNumaTlbInterop.AvailablePhysicalMemoryBytes();
        return new MemoryTlbLargePageEnvironment(
            nodeCount,
            largePageMinimum,
            largePageAvailable,
            largePageReason,
            available,
            "GetNumaHighestNodeNumber / GetLargePageMinimum / SeLockMemoryPrivilege 實測 / GlobalMemoryStatusEx");
    }
}

/// <summary>Windows 實測引擎：managed 陣列掃 TLB 曲線；unmanaged 對照組唯一差異是分頁大小或 NUMA 節點。</summary>
public sealed class WindowsMemoryNumaTlbLargePageEngine : IMemoryNumaTlbLargePageEngine
{
    private const long BytesPerPage = 4096;
    private const long LongsPerPage = BytesPerPage / sizeof(long);

    public Task<MemoryNumaTlbLargePageMeasurement> MeasureAsync(
        MemoryNumaTlbLargePageContext context,
        CancellationToken cancellationToken)
        => Task.Run(() => Measure(context, cancellationToken), cancellationToken);

    private static MemoryNumaTlbLargePageMeasurement Measure(
        MemoryNumaTlbLargePageContext context,
        CancellationToken cancellationToken)
    {
        MemoryNumaTlbLargePagePlan plan = context.Plan;
        int totalSteps =
            plan.TlbWorkingSetsBytes.Count * (plan.Settings.WarmupPasses + plan.Settings.MeasurePasses)
            + (plan.IncludeLargePageComparison ? 2 * (plan.Settings.WarmupPasses + plan.Settings.MeasurePasses) : 0)
            + (plan.IncludeNumaComparison ? 2 * plan.Settings.NumaMeasurePasses : 0);
        int completed = 0;
        var progress = (int done, double fractionBase, string phase) =>
            context.Progress.Report(new DeepBenchProgress(
                MemoryNumaTlbLargePageService.TestId, done, Math.Max(totalSteps, 1), 0.05 + 0.92 * fractionBase, phase));

        List<MemoryTlbWorkingSetPoint> tlbPoints = [];
        foreach (long size in plan.TlbWorkingSetsBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tlbPoints.Add(ScanManagedWorkingSet(size, plan.Settings, context, completed, totalSteps, out completed));
        }

        MemoryLargePagePoint? largePoint = null;
        if (plan.IncludeLargePageComparison)
        {
            cancellationToken.ThrowIfCancellationRequested();
            largePoint = CompareLargePages(plan, context, completed, totalSteps, out completed);
        }

        MemoryNumaPoint? numaPoint = null;
        if (plan.IncludeNumaComparison)
        {
            cancellationToken.ThrowIfCancellationRequested();
            numaPoint = CompareNumaNodes(plan, context, completed, totalSteps, out completed);
        }

        progress(completed, 1.0, "NUMA／TLB／大分頁掃描完成");
        return new MemoryNumaTlbLargePageMeasurement(tlbPoints, largePoint, numaPoint);
    }

    private static MemoryTlbWorkingSetPoint ScanManagedWorkingSet(
        long workingSetBytes,
        MemoryNumaTlbLargePageSettings settings,
        MemoryNumaTlbLargePageContext context,
        int completed,
        int totalSteps,
        out int updatedCompleted)
    {
        long pageCount = workingSetBytes / BytesPerPage;
        int stride = MemoryNumaTlbLargePageService.ChooseStride((int)pageCount);
        long[] buffer = new long[workingSetBytes / sizeof(long)];
        for (long index = 0; index < buffer.Length; index += LongsPerPage)
            buffer[index] = index;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        List<double> samples = [];
        int passes = settings.WarmupPasses + settings.MeasurePasses;
        for (int pass = 0; pass < passes; pass++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.Progress.Report(new DeepBenchProgress(
                MemoryNumaTlbLargePageService.TestId, completed + pass, Math.Max(totalSteps, 1),
                0.05 + 0.92 * (completed + pass) / Math.Max(totalSteps, 1),
                pass < settings.WarmupPasses
                    ? $"warmup 掃描 {MemoryNumaTlbLargePageService.FormatWorkingSet(workingSetBytes)}"
                    : $"TLB 掃描 {MemoryNumaTlbLargePageService.FormatWorkingSet(workingSetBytes)}（第 {pass - settings.WarmupPasses + 1}/{settings.MeasurePasses} 回）"));
            long timestamp = Stopwatch.GetTimestamp();
            long sink = ScanManaged(buffer, (int)pageCount, stride);
            double elapsedNs = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds * 1_000_000d;
            double nsPerPage = elapsedNs / pageCount;
            if (sink == long.MaxValue) throw new InvalidOperationException("掃描意外 sentinel。");
            if (pass >= settings.WarmupPasses)
                samples.Add(nsPerPage);
        }

        updatedCompleted = completed + passes;
        return new MemoryTlbWorkingSetPoint(workingSetBytes, (int)BytesPerPage, samples);
    }

    private static long ScanManaged(long[] buffer, int pageCount, int stride)
    {
        long accumulator = 0;
        int page = 0;
        for (int index = 0; index < pageCount; index++)
        {
            accumulator += buffer[page * LongsPerPage];
            page += stride;
            if (page >= pageCount) page -= pageCount;
        }
        return accumulator;
    }

    private static MemoryLargePagePoint CompareLargePages(
        MemoryNumaTlbLargePagePlan plan,
        MemoryNumaTlbLargePageContext context,
        int completed,
        int totalSteps,
        out int updatedCompleted)
    {
        long bytes = plan.LargePageWorkingSetBytes;
        nint regular = MemoryNumaTlbInterop.AllocateReadWrite(bytes);
        if (regular == 0)
            throw new MemoryNumaTlbLargePageValidationException(
                $"大分頁對照的一般分頁基準配置失敗（Win32 錯誤 {Marshal.GetLastWin32Error()}）；整場拒收。");
        nint large = MemoryNumaTlbInterop.AllocateLargePages(bytes);
        if (large == 0)
            throw new MemoryNumaTlbLargePageValidationException(
                $"大分頁配置失敗（Win32 錯誤 {Marshal.GetLastWin32Error()}）；不用猜測值替代。");

        try
        {
            MemoryNumaTlbInterop.TouchPages(regular, bytes);
            MemoryNumaTlbInterop.TouchPages(large, bytes);
            List<double> regularSamples = ScanUnmanagedRepeated(
                regular, bytes, plan.Settings, context, "大分頁對照 4 KiB 基準", completed, totalSteps, out completed);
            List<double> largeSamples = ScanUnmanagedRepeated(
                large, bytes, plan.Settings, context, "大分頁對照 MEM_LARGE_PAGES", completed, totalSteps, out updatedCompleted);
            return new MemoryLargePagePoint(bytes, regularSamples, largeSamples);
        }
        finally
        {
            MemoryNumaTlbInterop.Free(regular);
            MemoryNumaTlbInterop.Free(large);
        }
    }

    private static MemoryNumaPoint CompareNumaNodes(
        MemoryNumaTlbLargePagePlan plan,
        MemoryNumaTlbLargePageContext context,
        int completed,
        int totalSteps,
        out int updatedCompleted)
    {
        long bytes = plan.Settings.NumaWorkingSetBytes;
        nint local = MemoryNumaTlbInterop.AllocateNuma(bytes, plan.NumaLocalNode);
        nint remote = MemoryNumaTlbInterop.AllocateNuma(bytes, plan.NumaRemoteNode);
        if (local == 0 || remote == 0)
        {
            if (local != 0) MemoryNumaTlbInterop.Free(local);
            if (remote != 0) MemoryNumaTlbInterop.Free(remote);
            throw new MemoryNumaTlbLargePageValidationException(
                $"NUMA 節點配置失敗（local Win32 {Marshal.GetLastWin32Error()}）；整場拒收。");
        }

        try
        {
            MemoryNumaTlbInterop.TouchPages(local, bytes);
            MemoryNumaTlbInterop.TouchPages(remote, bytes);
            List<double> localSamples = [];
            List<double> remoteSamples = [];
            for (int pass = 0; pass < plan.Settings.NumaMeasurePasses; pass++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Progress.Report(new DeepBenchProgress(
                    MemoryNumaTlbLargePageService.TestId, completed + pass * 2, Math.Max(totalSteps, 1),
                    0.05 + 0.92 * (completed + pass * 2) / Math.Max(totalSteps, 1),
                    $"跨 NUMA 對照 第 {pass + 1}/{plan.Settings.NumaMeasurePasses} 回"));
                localSamples.Add(ScanUnmanagedOnce(local, bytes));
                remoteSamples.Add(ScanUnmanagedOnce(remote, bytes));
            }

            updatedCompleted = completed + plan.Settings.NumaMeasurePasses * 2;
            return new MemoryNumaPoint(plan.NumaLocalNode, plan.NumaRemoteNode, localSamples, remoteSamples);
        }
        finally
        {
            MemoryNumaTlbInterop.Free(local);
            MemoryNumaTlbInterop.Free(remote);
        }
    }

    private static List<double> ScanUnmanagedRepeated(
        nint address,
        long bytes,
        MemoryNumaTlbLargePageSettings settings,
        MemoryNumaTlbLargePageContext context,
        string phaseLabel,
        int completed,
        int totalSteps,
        out int updatedCompleted)
    {
        List<double> samples = [];
        int passes = settings.WarmupPasses + settings.MeasurePasses;
        for (int pass = 0; pass < passes; pass++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.Progress.Report(new DeepBenchProgress(
                MemoryNumaTlbLargePageService.TestId, completed + pass, Math.Max(totalSteps, 1),
                0.05 + 0.92 * (completed + pass) / Math.Max(totalSteps, 1),
                pass < settings.WarmupPasses ? $"warmup {phaseLabel}" : $"量測 {phaseLabel}"));
            double nsPerPage = ScanUnmanagedOnce(address, bytes);
            if (pass >= settings.WarmupPasses)
                samples.Add(nsPerPage);
        }

        updatedCompleted = completed + passes;
        return samples;
    }

    private static double ScanUnmanagedOnce(nint address, long bytes)
    {
        long pageCount = bytes / BytesPerPage;
        int stride = MemoryNumaTlbLargePageService.ChooseStride((int)pageCount);
        long timestamp = Stopwatch.GetTimestamp();
        long accumulator = 0;
        int page = 0;
        for (long index = 0; index < pageCount; index++)
        {
            accumulator += Marshal.ReadInt64(address, page * (int)BytesPerPage);
            page += stride;
            if (page >= pageCount) page -= (int)pageCount;
        }

        double elapsedNs = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds * 1_000_000d;
        if (accumulator == long.MaxValue) throw new InvalidOperationException("掃描意外 sentinel。");
        return elapsedNs / pageCount;
    }
}

internal static class MemoryNumaTlbInterop
{
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemLargePages = 0x20000000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const int ErrorNotAllAssigned = 1300;

    [DllImport("kernel32.dll")]
    internal static extern uint GetLargePageMinimum();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(nint lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocExNuma(nint hProcess, nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect, uint nndPreferred);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNumaHighestNodeNumber(out uint HighestNodeNumber);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out nint lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        nint tokenHandle, bool disableAllPrivileges, ref TokenPrivileges newState, nuint bufferLength, nint previousState, nint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    internal static bool TryEnableLockPagesPrivilege()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out nint token))
                return false;
            try
            {
                if (!LookupPrivilegeValue(null, "SeLockMemoryPrivilege", out nint luid))
                    return false;
                var privileges = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SePrivilegeEnabled,
                };
                if (!AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0))
                    return false;
                return Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch
        {
            return false;
        }
    }

    internal static nint AllocateLargePages(long bytes) =>
        VirtualAlloc(0, (nuint)bytes, MemCommit | MemReserve | MemLargePages, PageReadWrite);

    internal static nint AllocateReadWrite(long bytes) =>
        VirtualAlloc(0, (nuint)bytes, MemCommit | MemReserve, PageReadWrite);

    internal static nint AllocateNuma(long bytes, int node) =>
        VirtualAllocExNuma(-1, 0, (nuint)bytes, MemCommit | MemReserve, PageReadWrite, (uint)node);

    internal static void Free(nint address) => VirtualFree(address, 0, MemRelease);

    /// <summary>提交並把每一頁寫過一次，避免第一次量測落在 demand-zero 錯誤處理上。</summary>
    internal static void TouchPages(nint address, long bytes)
    {
        for (long offset = 0; offset < bytes; offset += 4096)
            Marshal.WriteInt64(address + (nint)offset, offset);
    }

    internal static long AvailablePhysicalMemoryBytes()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? (long)status.AvailPhys : 0;
        }
        catch
        {
            return 0;
        }
    }
}
