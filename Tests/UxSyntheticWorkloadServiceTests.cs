using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class UxSyntheticWorkloadServiceTests
{
    [Fact]
    public async Task 三步驟逐迭代樣本與端到端延遲()
    {
        var measurement = CreateMeasurement(8);
        var service = new UxSyntheticWorkloadService(new FixedEngine(measurement));

        DeepBenchTestResult result = await RunAsync(service, DeepBenchRunProfile.Quick);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(
            [
                "ux.synthetic.sha256.throughput",
                "ux.synthetic.transform.throughput",
                "ux.synthetic.json-roundtrip.throughput",
                "ux.synthetic.end-to-end.ms",
            ],
            result.Metrics.Select(metric => metric.Id));
        Assert.Equal(8, result.Metrics[0].Samples.Count);
        Assert.Equal(3.0, result.Metrics[3].Samples.Single(), 12);
        Assert.Contains("沒有錄製負載", string.Join('\n', result.Conditions), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 迭代數不符或步驟順序錯或非正數都拒收()
    {
        var wrongCount = CreateMeasurement(1);
        var wrongOrder = new SyntheticWorkloadMeasurement(
        [
            new(0,
            [
                new("transform", 1.0, 10.0),
                new("sha256", 1.0, 10.0),
                new("json-roundtrip", 1.0, 10.0),
            ]),
        ]);
        var nonPositive = new SyntheticWorkloadMeasurement(
        [
            new(0,
            [
                new("sha256", 0, 10.0),
                new("transform", 1.0, 10.0),
                new("json-roundtrip", 1.0, 10.0),
            ]),
        ]);

        foreach (SyntheticWorkloadMeasurement measurement in new[] { wrongCount, wrongOrder, nonPositive })
        {
            DeepBenchTestResult result = await RunAsync(new UxSyntheticWorkloadService(new FixedEngine(measurement)), DeepBenchRunProfile.Quick);
            Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
            Assert.Empty(result.Metrics);
        }
    }

    [Fact]
    public async Task 取消後不輸出部分迭代補值()
    {
        var service = new UxSyntheticWorkloadService(new CancellingEngine());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var context = new DeepBenchRunContext(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());
        DeepBenchTestResult result = await service.RunAsync(context, cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("不輸出部分迭代補值", string.Join('\n', result.Limitations), StringComparison.Ordinal);
    }

    [Fact]
    public void Quick與Full使用不同迭代數與負載()
    {
        SyntheticWorkloadSettings quick = UxSyntheticWorkloadService.GetSettings(DeepBenchRunProfile.Quick);
        SyntheticWorkloadSettings full = UxSyntheticWorkloadService.GetSettings(DeepBenchRunProfile.Full);

        Assert.Equal(8, quick.Iterations);
        Assert.Equal(24, full.Iterations);
        Assert.True(full.PayloadBytes > quick.PayloadBytes);
    }

    [Fact]
    public void 誠實界線明示非實際應用與跨機不成立()
    {
        string limitations = string.Join('\n', UxSyntheticWorkloadService.Limitations);

        Assert.Contains("不是任何實際應用程式的錄製重播", limitations, StringComparison.Ordinal);
        Assert.Contains("不外推成 UI 回應速度", limitations, StringComparison.Ordinal);
        Assert.Contains("跨機器比較不成立", limitations, StringComparison.Ordinal);
        Assert.Contains("不合成單一總分", limitations, StringComparison.Ordinal);
    }

    [Fact]
    public void 合成Json生成與驗證往返一致()
    {
        byte[] payload = new byte[4096];
        uint prng = 0x243F6A88u;
        for (int index = 0; index < payload.Length; index += 4)
        {
            prng ^= prng << 13; prng ^= prng >> 17; prng ^= prng << 5;
            payload[index] = (byte)prng;
            payload[index + 1] = (byte)(prng >> 8);
            payload[index + 2] = (byte)(prng >> 16);
            payload[index + 3] = (byte)(prng >> 24);
        }

        string json = ManagedSyntheticWorkloadEngine.BuildSyntheticJson(payload);
        Assert.StartsWith("{", json, StringComparison.Ordinal);
        Assert.EndsWith("}", json, StringComparison.Ordinal);
        Assert.True(ManagedSyntheticWorkloadEngine.VerifySyntheticJson(json, payload));
        Assert.False(ManagedSyntheticWorkloadEngine.VerifySyntheticJson("{", payload));
        Assert.False(ManagedSyntheticWorkloadEngine.VerifySyntheticJson("{\"k0\":1,\"k4\":2,\"k8\"}", payload));
    }

    [Fact]
    public async Task Windows引擎能完成最小合成流程實測()
    {
        var settings = new SyntheticWorkloadSettings(2, 64 * 1024);
        SyntheticWorkloadMeasurement measurement = await new ManagedSyntheticWorkloadEngine().MeasureAsync(
            new SyntheticWorkloadContext(settings, new Progress<DeepBenchProgress>(), CancellationToken.None),
            CancellationToken.None);

        Assert.Equal(2, measurement.Iterations.Count);
        Assert.All(measurement.Iterations, iteration =>
        {
            Assert.Equal(UxSyntheticWorkloadService.StepIds, iteration.Steps.Select(step => step.StepId).ToArray());
            Assert.All(iteration.Steps, step =>
                Assert.True(double.IsFinite(step.Milliseconds) && step.Milliseconds > 0 && double.IsFinite(step.Throughput) && step.Throughput > 0));
        });
    }

    private static SyntheticWorkloadMeasurement CreateMeasurement(int iterations) =>
        new(Enumerable.Range(0, iterations).Select(index => new SyntheticWorkloadIteration(
            index,
            [
                new("sha256", 1.0, 10.0 + index),
                new("transform", 1.0, 20.0 + index),
                new("json-roundtrip", 1.0, 30.0 + index),
            ])).ToArray());

    private static async Task<DeepBenchTestResult> RunAsync(UxSyntheticWorkloadService service, DeepBenchRunProfile profile)
    {
        var context = new DeepBenchRunContext(Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
        return await service.RunAsync(context, CancellationToken.None);
    }

    private sealed class FixedEngine(SyntheticWorkloadMeasurement measurement) : ISyntheticWorkloadEngine
    {
        public Task<SyntheticWorkloadMeasurement> MeasureAsync(SyntheticWorkloadContext context, CancellationToken cancellationToken)
            => Task.FromResult(measurement);
    }

    private sealed class CancellingEngine : ISyntheticWorkloadEngine
    {
        public async Task<SyntheticWorkloadMeasurement> MeasureAsync(SyntheticWorkloadContext context, CancellationToken cancellationToken)
        {
            _ = context;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new SyntheticWorkloadMeasurement([]);
        }
    }
}
