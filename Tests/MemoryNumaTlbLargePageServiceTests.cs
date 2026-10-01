using Xunit;

namespace XinSpect.Tests;

public class MemoryNumaTlbLargePageServiceTests
{
    private const long MiB = 1024L * 1024;

    [Fact]
    public void 建plan過濾超過記憶體預算的掃描點並獨立判定三個子項()
    {
        var environment = new MemoryTlbLargePageEnvironment(
            NumaNodeCount: 1,
            LargePageMinimumBytes: (uint)(2 * MiB),
            LargePageAvailable: false,
            LargePageUnavailableReason: "SeLockMemoryPrivilege 未授予此帳戶（需群組原則「鎖定分頁」）；不猜測、不降級。",
            AvailableMemoryBytes: 100 * MiB,
            Source: "測試");
        var settings = new MemoryNumaTlbLargePageSettings(
            [2 * MiB, 8 * MiB, 512 * MiB], 64 * MiB, 64 * MiB, 1, 3, 5);

        MemoryNumaTlbLargePagePlan? plan = MemoryNumaTlbLargePageService.BuildPlan(environment, settings);

        Assert.NotNull(plan);
        Assert.Equal([2 * MiB, 8 * MiB], plan.TlbWorkingSetsBytes);
        Assert.False(plan.IncludeLargePageComparison);
        Assert.False(plan.IncludeNumaComparison);
    }

    [Fact]
    public async Task 大分頁與NUMA可用時plan納入兩個對照()
    {
        var environment = new MemoryTlbLargePageEnvironment(
            3, (uint)(2 * MiB), true, null, 4096 * MiB, "測試");
        var settings = new MemoryNumaTlbLargePageSettings([2 * MiB, 8 * MiB], 64 * MiB, 64 * MiB, 1, 3, 5);

        MemoryNumaTlbLargePagePlan? plan = MemoryNumaTlbLargePageService.BuildPlan(environment, settings);

        Assert.NotNull(plan);
        Assert.True(plan.IncludeLargePageComparison);
        Assert.True(plan.IncludeNumaComparison);
        Assert.Equal(0, plan.NumaLocalNode);
        Assert.Equal(1, plan.NumaRemoteNode);

        var measurement = new MemoryNumaTlbLargePageMeasurement(
            [
                new(2 * MiB, 4096, [2.0, 2.2, 2.1]),
                new(8 * MiB, 4096, [2.4, 2.6, 2.5]),
                new(32 * MiB, 4096, [2.8, 3.0, 2.9]),
                new(128 * MiB, 4096, [3.6, 3.8, 3.7]),
            ],
            new MemoryLargePagePoint(64 * MiB, [3.0, 3.2, 3.1], [1.5, 1.7, 1.6]),
            new MemoryNumaPoint(0, 1, [2.0, 2.2, 2.1, 2.3, 2.2], [3.0, 3.2, 3.1, 3.3, 3.2]));
        var service = new MemoryNumaTlbLargePageService(
            new FixedProbe(environment), new CapturingEngine(measurement));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(
            [
                "memory.tlb-scan.2mib.ns-per-page",
                "memory.tlb-scan.8mib.ns-per-page",
                "memory.tlb-scan.32mib.ns-per-page",
                "memory.tlb-scan.128mib.ns-per-page",
                "memory.large-page.regular.ns-per-page",
                "memory.large-page.large.ns-per-page",
                "memory.large-page.regular-over-large.ratio",
                "memory.numa.local.ns-per-page",
                "memory.numa.remote.ns-per-page",
                "memory.numa.remote-over-local.ratio",
            ],
            result.Metrics.Select(metric => metric.Id));
        Assert.Equal([2.0, 2.2, 2.1], result.Metrics[0].Samples);
        Assert.Equal(3.1 / 1.6, result.Metrics[6].Samples.Single(), 12);
        Assert.Equal(3.2 / 2.2, result.Metrics[9].Samples.Single(), 12);
        Assert.Contains("單節點機器", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 單NUMA無大分頁權限時仍輸出TLB掃描並誠實標示另兩項未執行()
    {
        var environment = new MemoryTlbLargePageEnvironment(
            1, (uint)(2 * MiB), false, "SeLockMemoryPrivilege 未授予此帳戶（需群組原則「鎖定分頁」）；不猜測、不降級。", 4096 * MiB, "測試");
        var measurement = new MemoryNumaTlbLargePageMeasurement(
            [
                new(2 * MiB, 4096, [2.0, 2.2, 2.1]),
                new(8 * MiB, 4096, [2.4, 2.6, 2.5]),
                new(32 * MiB, 4096, [2.8, 3.0, 2.9]),
                new(128 * MiB, 4096, [3.6, 3.8, 3.7]),
            ],
            null,
            null);
        var service = new MemoryNumaTlbLargePageService(
            new FixedProbe(environment), new CapturingEngine(measurement));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(4, result.Metrics.Count);
        string conditions = string.Join('\n', result.Conditions);
        Assert.Contains("SeLockMemoryPrivilege 未授予", conditions, StringComparison.Ordinal);
        Assert.Contains("跨 NUMA 對照未執行", conditions, StringComparison.Ordinal);
        Assert.DoesNotContain("memory.large-page", string.Join('\n', result.Metrics.Select(m => m.Id)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 三個子項都不適用時回Unsupported不啟動引擎()
    {
        var environment = new MemoryTlbLargePageEnvironment(
            1, 0, false, "此系統回報不支援大分頁（GetLargePageMinimum = 0）。", 3 * MiB, "測試");
        var engine = new CapturingEngine(new MemoryNumaTlbLargePageMeasurement([], null, null));
        var service = new MemoryNumaTlbLargePageService(new FixedProbe(environment), engine);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Empty(engine.Contexts);
        Assert.NotNull(result.Error);
        Assert.Contains("三個子項都不適用", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 樣本缺失非有限或數量不符都整場拒收()
    {
        var environment = FullEnvironment();
        var plan = MemoryNumaTlbLargePageService.BuildPlan(environment, QuickSettings())!;

        var missingSample = new MemoryNumaTlbLargePageMeasurement(
            [new(2 * MiB, 4096, [2.0, 2.2]), new(8 * MiB, 4096, [2.4, 2.6, 2.5])],
            LargePoint(3),
            NumaPoint(5));
        var nonFinite = new MemoryNumaTlbLargePageMeasurement(
            [new(2 * MiB, 4096, [2.0, 0, 2.1]), new(8 * MiB, 4096, [2.4, 2.6, 2.5])],
            LargePoint(3),
            NumaPoint(5));
        var wrongSize = new MemoryNumaTlbLargePageMeasurement(
            [new(2 * MiB, 4096, [2.0, 2.2, 2.1]), new(16 * MiB, 4096, [2.4, 2.6, 2.5])],
            LargePoint(3),
            NumaPoint(5));
        var missingLarge = new MemoryNumaTlbLargePageMeasurement(
            [new(2 * MiB, 4096, [2.0, 2.2, 2.1]), new(8 * MiB, 4096, [2.4, 2.6, 2.5])],
            null,
            NumaPoint(5));
        var missingNuma = new MemoryNumaTlbLargePageMeasurement(
            [new(2 * MiB, 4096, [2.0, 2.2, 2.1]), new(8 * MiB, 4096, [2.4, 2.6, 2.5])],
            LargePoint(3),
            null);

        foreach (MemoryNumaTlbLargePageMeasurement measurement in new[] { missingSample, nonFinite, wrongSize, missingLarge, missingNuma })
        {
            DeepBenchTestResult result = await RunAsync(
                new MemoryNumaTlbLargePageService(new FixedProbe(environment), new CapturingEngine(measurement)),
                DeepBenchRunProfile.Quick);
            Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
            Assert.Empty(result.Metrics);
        }

        Assert.Equal(2, plan.TlbWorkingSetsBytes.Count);
    }

    [Fact]
    public async Task 取消後不輸出部分掃描補值()
    {
        var service = new MemoryNumaTlbLargePageService(
            new FixedProbe(FullEnvironment()), new CancellingEngine());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var context = new DeepBenchRunContext(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());
        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("不輸出部分掃描補值", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public void Quick與Full使用不同掃描點與回數()
    {
        MemoryNumaTlbLargePageSettings quick = MemoryNumaTlbLargePageService.GetSettings(DeepBenchRunProfile.Quick);
        MemoryNumaTlbLargePageSettings full = MemoryNumaTlbLargePageService.GetSettings(DeepBenchRunProfile.Full);

        Assert.Equal([2 * MiB, 8 * MiB, 32 * MiB, 128 * MiB], quick.TlbWorkingSetsBytes);
        Assert.Equal(6, full.TlbWorkingSetsBytes.Count);
        Assert.Contains(1024 * MiB, full.TlbWorkingSetsBytes);
        Assert.Equal(3, quick.MeasurePasses);
        Assert.Equal(5, full.MeasurePasses);
        Assert.Equal(5, quick.NumaMeasurePasses);
        Assert.Equal(11, full.NumaMeasurePasses);
        Assert.True(full.LargePageWorkingSetBytes > quick.LargePageWorkingSetBytes);
    }

    [Fact]
    public void 誠實界線涵蓋TLB混合效應與權限和NUMA前提()
    {
        string limitations = string.Join('\n', MemoryNumaTlbLargePageService.Limitations);

        Assert.Contains("混合效應", limitations, StringComparison.Ordinal);
        Assert.Contains("不宣稱量到 DTLB 大小", limitations, StringComparison.Ordinal);
        Assert.Contains("SeLockMemoryPrivilege", limitations, StringComparison.Ordinal);
        Assert.Contains("不降級", limitations, StringComparison.Ordinal);
        Assert.Contains("至少兩個 NUMA 節點", limitations, StringComparison.Ordinal);
        Assert.Contains("不合成單一總分", limitations, StringComparison.Ordinal);
        Assert.Contains("未在實機全面驗證", limitations, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(512, 1)]
    [InlineData(32768, 1)]
    public void 掃描步進與頁數互質避免循序prefetch主導(int pageCount, int _)
    {
        int stride = MemoryNumaTlbLargePageService.ChooseStride(pageCount);

        Assert.True(stride >= 1);
        if (stride > 1)
            Assert.Equal(1, Gcd(stride, pageCount));
        int position = 0;
        var visited = new HashSet<int>();
        for (int step = 0; step < pageCount; step++)
        {
            visited.Add(position);
            position = (position + stride) % pageCount;
        }
        Assert.Equal(pageCount, visited.Count);

        static int Gcd(int a, int b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return a;
        }
    }

    [Fact]
    public async Task Windows引擎能完成最小TLB掃描實測()
    {
        var service = new MemoryNumaTlbLargePageService();
        var quick = MemoryNumaTlbLargePageService.GetSettings(DeepBenchRunProfile.Quick);
        var environment = new WindowsMemoryNumaTlbLargePageProbe().Detect();
        MemoryNumaTlbLargePagePlan? plan = MemoryNumaTlbLargePageService.BuildPlan(environment, quick);
        if (plan is null || plan.TlbWorkingSetsBytes.Count == 0)
            return; // 環境連最小掃描都撐不起來時，不以空跑充數。

        var measurement = await new WindowsMemoryNumaTlbLargePageEngine().MeasureAsync(
            new MemoryNumaTlbLargePageContext(plan, DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>(), CancellationToken.None),
            CancellationToken.None);

        Assert.NotEmpty(measurement.TlbPoints);
        Assert.All(measurement.TlbPoints, point =>
        {
            Assert.Equal(plan.Settings.MeasurePasses, point.NsPerPageSamples.Count);
            Assert.All(point.NsPerPageSamples, value => Assert.True(double.IsFinite(value) && value > 0));
        });
        _ = service;
    }

    private static MemoryTlbLargePageEnvironment FullEnvironment() =>
        new(2, (uint)(2 * MiB), true, null, 4096 * MiB, "測試");

    private static MemoryNumaTlbLargePageSettings QuickSettings() =>
        new([2 * MiB, 8 * MiB], 64 * MiB, 64 * MiB, 1, 3, 5);

    private static MemoryLargePagePoint LargePoint(int samples) =>
        new(64 * MiB, Repeat(3.1, samples), Repeat(1.6, samples));

    private static MemoryNumaPoint NumaPoint(int samples) =>
        new(0, 1, Repeat(2.2, samples), Repeat(3.2, samples));

    private static double[] Repeat(double value, int count) =>
        Enumerable.Repeat(value, count).ToArray();

    private static async Task<DeepBenchTestResult> RunAsync(MemoryNumaTlbLargePageService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class FixedProbe(MemoryTlbLargePageEnvironment Environment) : IMemoryNumaTlbLargePageProbe
    {
        public MemoryTlbLargePageEnvironment Detect() => Environment;
    }

    private sealed class CapturingEngine(MemoryNumaTlbLargePageMeasurement measurement) : IMemoryNumaTlbLargePageEngine
    {
        public List<MemoryNumaTlbLargePageContext> Contexts { get; } = [];

        public Task<MemoryNumaTlbLargePageMeasurement> MeasureAsync(
            MemoryNumaTlbLargePageContext context,
            CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            return Task.FromResult(measurement);
        }
    }

    private sealed class CancellingEngine : IMemoryNumaTlbLargePageEngine
    {
        public async Task<MemoryNumaTlbLargePageMeasurement> MeasureAsync(
            MemoryNumaTlbLargePageContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new MemoryNumaTlbLargePageMeasurement([], null, null);
        }
    }
}
