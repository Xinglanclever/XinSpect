using Xunit;

namespace XinSpect.Tests;

public class DeepBenchCatalogTests
{
    private static readonly string[] Phase1RunnableIds =
    [
        "cpu.aes-sha",
        "cpu.load-use-ilp-branch",
        "cpu.rdrand-rdseed",
        "topology.core-latency",
        "memory.cache-latency",
        "memory.stream-bandwidth",
        "memory.loaded-latency",
        "gpu.fp32-fp64-integer",
        "gpu.vram-bandwidth",
        "gpu.pcie-transfer",
        "cpu.top-down",
        "storage.qd-ladder",
        "storage.mixed-rw",
        "storage.write-integrity",
        "storage.flush-durability",
        "storage.slc-sustained-write",
        "ux.network-stack-latency"
    ];

    [Fact]
    public void 三十八項全量登記且編號連續()
    {
        Assert.Equal(38, DeepBenchCatalog.All.Count);
        Assert.Equal(Enumerable.Range(1, 38), DeepBenchCatalog.All.Select(entry => entry.MatrixNumber));
        Assert.Equal(38, DeepBenchCatalog.All.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(DeepBenchCatalog.All, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.False(string.IsNullOrWhiteSpace(entry.StatusText));
            Assert.False(string.IsNullOrWhiteSpace(entry.Requirement));
            Assert.False(string.IsNullOrWhiteSpace(entry.QuickEstimateText));
        });
    }

    [Fact]
    public void 已接入測項狀態與可信度引擎正確()
    {
        var byId = DeepBenchCatalog.All.ToDictionary(entry => entry.Id, entry => entry);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["cpu.aes-sha"].Status);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["cpu.load-use-ilp-branch"].Status);
        Assert.True(byId["cpu.load-use-ilp-branch"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["cpu.rdrand-rdseed"].Status);
        Assert.True(byId["cpu.rdrand-rdseed"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Integrated, byId["cpu.top-down"].Status);
        Assert.True(byId["cpu.top-down"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Integrated, byId["topology.core-latency"].Status);
        Assert.Equal(DeepBenchTestStatus.Integrated, byId["memory.cache-latency"].Status);
        Assert.Equal(DeepBenchTestStatus.Integrated, byId["memory.stream-bandwidth"].Status);
        Assert.Equal(DeepBenchTestStatus.Integrated, byId["memory.loaded-latency"].Status);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["gpu.fp32-fp64-integer"].Status);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["gpu.vram-bandwidth"].Status);
        Assert.True(byId["gpu.vram-bandwidth"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["gpu.pcie-transfer"].Status);
        Assert.True(byId["gpu.pcie-transfer"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["storage.qd-ladder"].Status);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["storage.mixed-rw"].Status);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["storage.write-integrity"].Status);
        Assert.True(byId["storage.write-integrity"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["storage.flush-durability"].Status);
        Assert.True(byId["storage.flush-durability"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["storage.slc-sustained-write"].Status);
        Assert.True(byId["storage.slc-sustained-write"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["ux.network-stack-latency"].Status);
        Assert.True(byId["ux.network-stack-latency"].Runnable);

        var confidence = byId["confidence.engine"];
        Assert.Equal(34, confidence.MatrixNumber);
        Assert.Equal(DeepBenchTestStatus.Implemented, confidence.Status);
        Assert.False(confidence.Runnable);
    }

    [Fact]
    public void Quick與Full都只選十四個已接入測項()
    {
        foreach (var profile in new[] { DeepBenchRunProfile.Quick, DeepBenchRunProfile.Full })
        {
            var plan = DeepBenchSuitePlanner.Plan(profile);
            Assert.Equal(Phase1RunnableIds.OrderBy(id => id, StringComparer.Ordinal), plan.SelectedIds.OrderBy(id => id, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void 延後項目必須明示原因且不可靜默消失()
    {
        var plan = DeepBenchSuitePlanner.Plan(DeepBenchRunProfile.Full);
        var skipped = plan.Skipped.ToDictionary(item => item.TestId, item => item.Reason);
        Assert.Equal(21, skipped.Count);
        Assert.All(skipped.Values, reason => Assert.Contains("Phase", reason, StringComparison.Ordinal));
        Assert.DoesNotContain(Phase1RunnableIds, id => skipped.ContainsKey(id));
    }

    [Fact]
    public void 高負載與磁碟寫入都是互斥執行()
    {
        Assert.All(
            DeepBenchCatalog.All.Where(entry => entry.ResourceClass is DeepBenchResourceClass.CpuLoad or DeepBenchResourceClass.MemoryLoad or DeepBenchResourceClass.GpuLoad or DeepBenchResourceClass.DiskWrite),
            entry => Assert.Equal(DeepBenchParallelSafety.Exclusive, entry.ParallelSafety));
    }
}
