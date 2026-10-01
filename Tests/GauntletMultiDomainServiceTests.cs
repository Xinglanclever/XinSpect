using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class GauntletMultiDomainServiceTests
{
    [Fact]
    public async Task 三域逐窗樣本與三個earlyLate比率()
    {
        var measurement = CreateMeasurement(6,
            [100, 99, 98, 97, 96, 95], [50, 49.5, 49, 48.5, 48, 47.5], [30, 29.5, 29, 28.5, 28, 27.5]);
        var service = new GauntletMultiDomainService(new FixedEngine(measurement));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(
            [
                "gauntlet.multi-domain.cpu.mops",
                "gauntlet.multi-domain.memory.mibs",
                "gauntlet.multi-domain.storage.mibs",
                "gauntlet.multi-domain.cpu.early-late.ratio",
                "gauntlet.multi-domain.memory.early-late.ratio",
                "gauntlet.multi-domain.storage.early-late.ratio",
            ],
            result.Metrics.Select(metric => metric.Id));
        Assert.Equal(6, result.Metrics[0].Samples.Count);
        Assert.Equal(95d / 100d, result.Metrics[3].Samples.Single(), 12);
        Assert.Equal(27.5d / 30d, result.Metrics[5].Samples.Single(), 12);
        Assert.Contains("設計目的", string.Join('\n', result.Conditions), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 窗數不符或非正數樣本都整場拒收()
    {
        var wrongCount = CreateMeasurement(2, [100, 98], [50, 49], [30, 29]);
        var nonPositive = new GauntletMultiDomainMeasurement(
        [
            new(0, 100, 50, 30),
            new(1, 0, 49, 29),
            new(2, 98, 48, 28),
        ]);

        foreach (GauntletMultiDomainMeasurement measurement in new[] { wrongCount, nonPositive })
        {
            DeepBenchTestResult result = await RunAsync(new GauntletMultiDomainService(new FixedEngine(measurement)), DeepBenchRunProfile.Quick);
            Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
            Assert.Empty(result.Metrics);
        }
    }

    [Fact]
    public async Task 取消後不輸出部分窗補值()
    {
        var service = new GauntletMultiDomainService(new CancellingEngine());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var context = new DeepBenchRunContext(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());
        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("不補值", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public void Quick與Full使用不同窗數與窗長()
    {
        GauntletMultiDomainSettings quick = GauntletMultiDomainService.GetSettings(DeepBenchRunProfile.Quick);
        GauntletMultiDomainSettings full = GauntletMultiDomainService.GetSettings(DeepBenchRunProfile.Full);

        Assert.Equal(6, quick.WindowCount);
        Assert.Equal(12, full.WindowCount);
        Assert.True(full.WindowMilliseconds > quick.WindowMilliseconds);
        Assert.True(full.WarmupWindows >= quick.WarmupWindows);
    }

    [Fact]
    public void 誠實界線明示搶資源與不外推()
    {
        string limitations = string.Join('\n', GauntletMultiDomainService.Limitations);

        Assert.Contains("互相搶資源", limitations, StringComparison.Ordinal);
        Assert.Contains("不外推成實際應用的多工表現", limitations, StringComparison.Ordinal);
        Assert.Contains("逐位元組讀回驗證", limitations, StringComparison.Ordinal);
        Assert.Contains("不是散熱認證", limitations, StringComparison.Ordinal);
        Assert.Contains("不合成單一總分", limitations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows引擎能完成最小多域實測()
    {
        var settings = new GauntletMultiDomainSettings(2, 200, 0);
        string tempDir = Path.Combine(Path.GetTempPath(), "XinSpect.multi-domain.test");
        Directory.CreateDirectory(tempDir);
        try
        {
            GauntletMultiDomainMeasurement measurement = await new WindowsMultiDomainEngine().MeasureAsync(
                new GauntletMultiDomainContext(settings, tempDir, new Progress<DeepBenchProgress>(), CancellationToken.None),
                CancellationToken.None);

            Assert.Equal(2, measurement.Windows.Count);
            Assert.All(measurement.Windows, window =>
            {
                Assert.True(double.IsFinite(window.CpuMops) && window.CpuMops > 0);
                Assert.True(double.IsFinite(window.MemoryMibs) && window.MemoryMibs > 0);
                Assert.True(double.IsFinite(window.StorageMibs) && window.StorageMibs > 0);
            });
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static GauntletMultiDomainMeasurement CreateMeasurement(
        int count, double[] cpu, double[] memory, double[] storage) =>
        new(Enumerable.Range(0, count)
            .Select(index => new GauntletMultiDomainWindow(index, cpu[index], memory[index], storage[index]))
            .ToArray());

    private static async Task<DeepBenchTestResult> RunAsync(GauntletMultiDomainService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class FixedEngine(GauntletMultiDomainMeasurement measurement) : IGauntletMultiDomainEngine
    {
        public Task<GauntletMultiDomainMeasurement> MeasureAsync(GauntletMultiDomainContext context, CancellationToken cancellationToken)
            => Task.FromResult(measurement);
    }

    private sealed class CancellingEngine : IGauntletMultiDomainEngine
    {
        public async Task<GauntletMultiDomainMeasurement> MeasureAsync(GauntletMultiDomainContext context, CancellationToken cancellationToken)
        {
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new GauntletMultiDomainMeasurement([]);
        }
    }
}
