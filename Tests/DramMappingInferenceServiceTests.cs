using Xunit;

namespace XinSpect.Tests;

public class DramMappingInferenceServiceTests
{
    private const long MiB = 1024L * 1024;

    [Fact]
    public void Plan把過大WorkingSet縮到預算並拒絕非法stride()
    {
        var environment = new DramStrideEnvironment(2048 * MiB, "測試");
        var settings = new DramStrideSettings(1536 * MiB, [256, 512, 4096], 1, 3);

        DramStridePlan? plan = DramMappingInferenceService.BuildPlan(environment, settings);

        Assert.NotNull(plan);
        Assert.Equal(768 * MiB, plan.WorkingSetBytes);

        var zeroStride = new DramStrideSettings(768 * MiB, [256, 0], 1, 3);
        Assert.Null(DramMappingInferenceService.BuildPlan(environment, zeroStride));
        var noStride = new DramStrideSettings(768 * MiB, [], 1, 3);
        Assert.Null(DramMappingInferenceService.BuildPlan(environment, noStride));
        var tiny = new DramStrideEnvironment(20 * MiB, "測試");
        Assert.Null(DramMappingInferenceService.BuildPlan(tiny, settings));
    }

    [Fact]
    public async Task 記憶體不足時整場Unsupported不啟動引擎()
    {
        var probe = new FixedProbe(new DramStrideEnvironment(10 * MiB, "測試"));
        var engine = new CapturingEngine(CreateMeasurement(768 * MiB, [256, 512]));
        var service = new DramMappingInferenceService(probe, engine);

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Empty(engine.Contexts);
        Assert.NotNull(result.Error);
        Assert.Contains("可用記憶體不足", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 曲線保留各stride原始樣本並只做明示比率()
    {
        var environment = new DramStrideEnvironment(4096 * MiB, "測試");
        var plan = DramMappingInferenceService.BuildPlan(environment, DramMappingInferenceService.GetSettings(DeepBenchRunProfile.Quick))!;
        var measurement = CreateMeasurement(plan.WorkingSetBytes, [.. plan.StrideBytes]);
        var service = new DramMappingInferenceService(new FixedProbe(environment), new CapturingEngine(measurement));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(
            plan.StrideBytes.Select(DramMappingInferenceService.FormatStride)
                .Select(label => $"memory.dram-stride.{label}.ns-per-access")
                .Append($"memory.dram-stride.64kib-over-256b.ratio"),
            result.Metrics.Select(metric => metric.Id));
        Assert.Equal([1.0, 1.1, 1.2], result.Metrics[0].Samples);
        Assert.Equal(9.9 / 1.1, result.Metrics[^1].Samples.Single(), 12);
        string conditions = string.Join('\n', result.Conditions);
        Assert.Contains("僅與 row activation／bank conflict 假說一致", conditions, StringComparison.Ordinal);
        Assert.Contains("推論", conditions, StringComparison.Ordinal);
        Assert.Contains("僅為推論", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 樣本缺失非有限或點數不符都整場拒收()
    {
        var environment = new DramStrideEnvironment(4096 * MiB, "測試");
        var settings = DramMappingInferenceService.GetSettings(DeepBenchRunProfile.Quick);
        var plan = DramMappingInferenceService.BuildPlan(environment, settings)!;

        var missing = new DramStrideMeasurement(
        [
            new(plan.StrideBytes[0], plan.WorkingSetBytes, [1.0, 1.1]),
            .. plan.StrideBytes.Skip(1).Select(stride => new DramStridePoint(stride, plan.WorkingSetBytes, [1.0, 1.1, 1.2])),
        ]);
        var nonFinite = new DramStrideMeasurement(
            plan.StrideBytes.Select((stride, index) => index == 2
                ? new DramStridePoint(stride, plan.WorkingSetBytes, [double.NaN, 1.0, 1.0])
                : new DramStridePoint(stride, plan.WorkingSetBytes, [1.0, 1.1, 1.2])).ToArray());
        var wrongStride = new DramStrideMeasurement(
            plan.StrideBytes.Select((stride, index) => index == 1
                ? new DramStridePoint(3, plan.WorkingSetBytes, [1.0, 1.1, 1.2])
                : new DramStridePoint(stride, plan.WorkingSetBytes, [1.0, 1.1, 1.2])).ToArray());
        var tooManySamples = new DramStrideMeasurement(
            plan.StrideBytes.Select(stride => new DramStridePoint(stride, plan.WorkingSetBytes, [1.0, 1.1, 1.2, 1.3])).ToArray());

        foreach (DramStrideMeasurement measurement in new[] { missing, nonFinite, wrongStride, tooManySamples })
        {
            DeepBenchTestResult result = await RunAsync(
                new DramMappingInferenceService(new FixedProbe(environment), new CapturingEngine(measurement)),
                DeepBenchRunProfile.Quick);
            Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
            Assert.Empty(result.Metrics);
        }

        Assert.Equal(9, plan.StrideBytes.Count);
    }

    [Fact]
    public async Task 取消後不輸出部分掃描補值()
    {
        var service = new DramMappingInferenceService(
            new FixedProbe(new DramStrideEnvironment(4096 * MiB, "測試")), new CancellingEngine());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var context = new DeepBenchRunContext(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());
        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("不輸出部分掃描補值", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public void Quick與Full使用不同WorkingSet與stride點數()
    {
        DramStrideSettings quick = DramMappingInferenceService.GetSettings(DeepBenchRunProfile.Quick);
        DramStrideSettings full = DramMappingInferenceService.GetSettings(DeepBenchRunProfile.Full);

        Assert.Equal(768 * MiB, quick.WorkingSetBytes);
        Assert.Equal(1536 * MiB, full.WorkingSetBytes);
        Assert.Equal(9, quick.StrideBytes.Count);
        Assert.Equal(12, full.StrideBytes.Count);
        Assert.Contains(524288, full.StrideBytes);
        Assert.DoesNotContain(524288, quick.StrideBytes);
        Assert.Equal(3, quick.MeasurePasses);
        Assert.Equal(7, full.MeasurePasses);
    }

    [Fact]
    public void 誠實界線明示推論性質與不宣稱映射()
    {
        string limitations = string.Join('\n', DramMappingInferenceService.Limitations);

        Assert.Contains("僅為推論", limitations, StringComparison.Ordinal);
        Assert.Contains("不宣稱任何確定的 row／bank／rank 映射", limitations, StringComparison.Ordinal);
        Assert.Contains("實體分頁由 Windows 決定", limitations, StringComparison.Ordinal);
        Assert.Contains("不拆分", limitations, StringComparison.Ordinal);
        Assert.Contains("不合成單一總分", limitations, StringComparison.Ordinal);
    }

    [Fact]
    public void 頁序排列是可重現的完整置換()
    {
        int[] order = WindowsDramStrideEngine.BuildPageOrder(4096);

        Assert.Equal(4096, order.Length);
        Assert.Equal(Enumerable.Range(0, 4096), order.OrderBy(value => value));
        Assert.Equal(order, WindowsDramStrideEngine.BuildPageOrder(4096));
    }

    [Fact]
    public async Task Windows引擎能完成最小stride掃描實測()
    {
        var environment = new WindowsDramStrideProbe().Detect();
        var settings = new DramStrideSettings(64 * MiB, [256, 8192, 65536], 0, 1);
        DramStridePlan? plan = DramMappingInferenceService.BuildPlan(environment, settings);
        if (plan is null)
            return; // 環境連最小掃描都撐不起來時，不以空跑充數。

        DramStrideMeasurement measurement = await new WindowsDramStrideEngine().MeasureAsync(
            new DramStrideContext(plan, DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>(), CancellationToken.None),
            CancellationToken.None);

        Assert.Equal(3, measurement.Points.Count);
        Assert.All(measurement.Points, point =>
        {
            Assert.Equal(plan.WorkingSetBytes, point.WorkingSetBytes);
            Assert.All(point.NsPerAccessSamples, value => Assert.True(double.IsFinite(value) && value > 0));
        });
    }

    private static DramStrideMeasurement CreateMeasurement(long workingSet, IReadOnlyList<int> strides) =>
        new(strides.Select((stride, index) =>
            new DramStridePoint(stride, workingSet, [1.0 * (index + 1), 1.1 * (index + 1), 1.2 * (index + 1)])).ToArray());

    private static async Task<DeepBenchTestResult> RunAsync(DramMappingInferenceService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class FixedProbe(DramStrideEnvironment Environment) : IDramStrideEnvironmentProbe
    {
        public DramStrideEnvironment Detect() => Environment;
    }

    private sealed class CapturingEngine(DramStrideMeasurement measurement) : IDramStrideEngine
    {
        public List<DramStrideContext> Contexts { get; } = [];

        public Task<DramStrideMeasurement> MeasureAsync(
            DramStrideContext context,
            CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            return Task.FromResult(measurement);
        }
    }

    private sealed class CancellingEngine : IDramStrideEngine
    {
        public async Task<DramStrideMeasurement> MeasureAsync(
            DramStrideContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new DramStrideMeasurement([]);
        }
    }
}
