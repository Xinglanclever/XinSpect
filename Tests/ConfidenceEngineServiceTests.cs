using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class ConfidenceEngineServiceTests
{
    [Fact]
    public async Task 全部已知答案案例分類一致()
    {
        var service = new ConfidenceEngineService(new FixedEngine(CreateMeasurement(matched: true)));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(
            ["confidence.audit.matched-cases", "confidence.audit.elapsed.us"],
            result.Metrics.Select(metric => metric.Id));
        Assert.Equal([7], result.Metrics[0].Samples);
        string conditions = string.Join('\n', result.Conditions);
        Assert.Contains("tight-samples", conditions, StringComparison.Ordinal);
        Assert.Contains("pooled-configurations", conditions, StringComparison.Ordinal);
        Assert.Contains("不量任何硬體", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 分類不符時整場拒收()
    {
        DeepBenchTestResult result = await RunAsync(
            new ConfidenceEngineService(new FixedEngine(CreateMeasurement(matched: false))), DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.NotNull(result.Error);
        Assert.Contains("分類不符", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 空稽核整場拒收()
    {
        var service = new ConfidenceEngineService(new FixedEngine(new ConfidenceAuditMeasurement([], 1)));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
    }

    [Fact]
    public async Task 取消後不輸出部分稽核補值()
    {
        var service = new ConfidenceEngineService(new CancellingEngine());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var context = new DeepBenchRunContext(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());
        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
    }

    [Fact]
    public void 已知答案案例涵蓋所有可信度等級()
    {
        (string CaseId, double[] Samples, DeepBenchConfidence Expected)[] cases = ConfidenceEngineService.BuildCases();

        Assert.Contains(cases, c => c.Expected == DeepBenchConfidence.High);
        Assert.Contains(cases, c => c.Expected == DeepBenchConfidence.Medium);
        Assert.Contains(cases, c => c.Expected == DeepBenchConfidence.Low);
        Assert.Contains(cases, c => c.Expected == DeepBenchConfidence.Insufficient);
        Assert.All(cases, c => Assert.False(string.IsNullOrWhiteSpace(c.CaseId)));
    }

    [Fact]
    public async Task Managed引擎對真實統計引擎稽核全數一致()
    {
        ConfidenceAuditMeasurement measurement = await new ManagedConfidenceAuditEngine().AuditAsync(
            new ConfidenceAuditContext(DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>(), CancellationToken.None),
            CancellationToken.None);

        Assert.NotEmpty(measurement.Cases);
        Assert.All(measurement.Cases, c => Assert.True(c.Matched, $"{c.CaseId}: 預期 {c.ExpectedConfidence}、實得 {c.ActualConfidence}"));
        Assert.True(measurement.ElapsedMicroseconds >= 0);
    }

    [Fact]
    public void 誠實界線明示非硬體量測與規格來源()
    {
        string limitations = string.Join('\n', ConfidenceEngineService.Limitations);

        Assert.Contains("不量任何硬體", limitations, StringComparison.Ordinal);
        Assert.Contains("與其他 37 個測項的量測性質不同", limitations, StringComparison.Ordinal);
        Assert.Contains("不宣稱涵蓋任何通用統計學標準", limitations, StringComparison.Ordinal);
        Assert.Contains("不是效能宣稱", limitations, StringComparison.Ordinal);
    }

    private static ConfidenceAuditMeasurement CreateMeasurement(bool matched) =>
        new(ConfidenceEngineService.BuildCases()
            .Select(c => new ConfidenceAuditCase(
                c.CaseId, c.Expected.ToString(), c.Expected, c.Samples.Length, 0, matched))
            .Append(new ConfidenceAuditCase("pooled-configurations", "Insufficient", DeepBenchConfidence.Insufficient, 0, 0, matched))
            .ToArray(),
            42);

    private static async Task<DeepBenchTestResult> RunAsync(ConfidenceEngineService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class FixedEngine(ConfidenceAuditMeasurement measurement) : IConfidenceAuditEngine
    {
        public Task<ConfidenceAuditMeasurement> AuditAsync(ConfidenceAuditContext context, CancellationToken cancellationToken)
            => Task.FromResult(measurement);
    }

    private sealed class CancellingEngine : IConfidenceAuditEngine
    {
        public async Task<ConfidenceAuditMeasurement> AuditAsync(ConfidenceAuditContext context, CancellationToken cancellationToken)
        {
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ConfidenceAuditMeasurement([], 0);
        }
    }
}
