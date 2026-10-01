using Xunit;

namespace XinSpect.Tests;

public class CpuMicroarchBenchTests
{
    [Fact]
    public void Quick與Full使用不同工作負載且都排除暖機()
    {
        var quick = CpuMicroarchBenchService.GetWorkload(DeepBenchRunProfile.Quick);
        var full = CpuMicroarchBenchService.GetWorkload(DeepBenchRunProfile.Full);

        Assert.Equal(1, quick.WarmUpRounds);
        Assert.Equal(2, quick.MeasuredRounds);
        Assert.Equal(2, full.WarmUpRounds);
        Assert.Equal(6, full.MeasuredRounds);
        Assert.True(full.LoadChainLength > quick.LoadChainLength);
        Assert.True(full.IlpLength > quick.IlpLength);
        Assert.True(full.BranchLength > quick.BranchLength);
    }

    [Fact]
    public void 依賴載入鏈與分支圖樣確定生成且索引有效()
    {
        int[] firstChain = CpuMicroarchBenchService.CreateLoadChain(128, 2026);
        int[] secondChain = CpuMicroarchBenchService.CreateLoadChain(128, 2026);
        int[] randomInputs = CpuMicroarchBenchService.CreateBranchInputs(128, 2026);

        Assert.Equal(128, firstChain.Length);
        Assert.Equal(secondChain, firstChain);
        Assert.All(firstChain, index => Assert.InRange(index, 0, 127));
        Assert.All(randomInputs, index => Assert.InRange(index, 0, 127));
        Assert.Contains(randomInputs, index => index >= 64);
        Assert.Contains(randomInputs, index => index < 64);
    }

    [Fact]
    public async Task 微架構工作負載保留每輪原始樣本和防刪除檢查碼()
    {
        var workload = new CpuMicroarchBenchService.CpuMicroarchWorkload(
            WarmUpRounds: 1,
            MeasuredRounds: 2,
            LoadChainLength: 4096,
            IlpLength: 4096,
            BranchLength: 4096);
        var service = new CpuMicroarchBenchService(workload);
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

        DeepBenchTestResult result = await service.RunAsync(context, CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Null(result.Error);
        string[] expected =
        [
            "cpu.loaduse.dependent-load-latency",
            "cpu.ilp.degree-1",
            "cpu.ilp.degree-2",
            "cpu.ilp.degree-4",
            "cpu.ilp.degree-8",
            "cpu.branch.predictable-throughput",
            "cpu.branch.random-throughput",
            "cpu.branch.random-predictable-ratio"
        ];
        Assert.Equal(expected, result.Metrics.Select(metric => metric.Id));
        Assert.All(result.Metrics, metric =>
        {
            Assert.Equal(2, metric.Samples.Count);
            Assert.All(metric.Samples, sample => Assert.True(double.IsFinite(sample) && sample > 0));
        });
        Assert.Contains(result.Conditions, text => text.Contains("防刪除檢查碼", StringComparison.Ordinal));
        Assert.Contains(result.Limitations, text => text.Contains("不是", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 預設服務依RunProfile選擇工作負載()
    {
        var service = new CpuMicroarchBenchService();
        var context = new DeepBenchRunContext(
            Guid.NewGuid(), DeepBenchRunProfile.Full, new Progress<DeepBenchProgress>());

        DeepBenchTestResult result = await service.RunAsync(context, CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Contains("1048576", result.Configuration, StringComparison.Ordinal);
        Assert.All(result.Metrics, metric => Assert.Equal(6, metric.Samples.Count));
    }
}
