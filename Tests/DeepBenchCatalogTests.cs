using Xunit;

namespace XinSpect.Tests;

public class DeepBenchCatalogTests
{
    private static readonly string[] Phase1RunnableIds =
    [
        "cpu.aes-sha",
        "cpu.load-use-ilp-branch",
        "cpu.branch-speculation",
        "cpu.rdrand-rdseed",
        "topology.core-latency",
        "topology.core-bandwidth",
        "topology.smt-contention",
        "topology.hybrid-placement",
        "topology.coherence-lock",
        "memory.numa-tlb-largepage",
        "memory.cache-latency",
        "memory.stream-bandwidth",
        "memory.loaded-latency",
        "memory.dram-mapping-inference",
        "memory.ecc-whea-stress",
        "gpu.fp32-fp64-integer",
        "gpu.vram-bandwidth",
        "gpu.pcie-transfer",
        "gpu.dispatch-jitter",
        "gpu.raster-texture",
        "gpu.codec-throughput",
        "cpu.top-down",
        "storage.qd-ladder",
        "storage.mixed-rw",
        "storage.write-integrity",
        "storage.flush-durability",
        "storage.slc-sustained-write",
        "storage.iocp-engine",
        "storage.io-gpu-pipeline",
        "ux.network-stack-latency",
        "ux.audio-buffer-glitch",
        "ux.present-frame-pacing",
        "gauntlet.boost-recovery",
        "gauntlet.throughput-degradation",
        "gauntlet.power-state-latency",
        "gauntlet.multi-domain",
        "ux.synthetic-workloads",
        "confidence.engine"
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
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["cpu.branch-speculation"].Status);
        Assert.True(byId["cpu.branch-speculation"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["cpu.rdrand-rdseed"].Status);
        Assert.True(byId["cpu.rdrand-rdseed"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Integrated, byId["cpu.top-down"].Status);
        Assert.True(byId["cpu.top-down"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Integrated, byId["topology.core-latency"].Status);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["topology.core-bandwidth"].Status);
        Assert.True(byId["topology.core-bandwidth"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["topology.smt-contention"].Status);
        Assert.True(byId["topology.smt-contention"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["topology.hybrid-placement"].Status);
        Assert.True(byId["topology.hybrid-placement"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["topology.coherence-lock"].Status);
        Assert.True(byId["topology.coherence-lock"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["memory.numa-tlb-largepage"].Status);
        Assert.True(byId["memory.numa-tlb-largepage"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Integrated, byId["memory.cache-latency"].Status);
        Assert.Equal(DeepBenchTestStatus.Integrated, byId["memory.stream-bandwidth"].Status);
        Assert.Equal(DeepBenchTestStatus.Integrated, byId["memory.loaded-latency"].Status);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["memory.dram-mapping-inference"].Status);
        Assert.True(byId["memory.dram-mapping-inference"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Integrated, byId["memory.ecc-whea-stress"].Status);
        Assert.True(byId["memory.ecc-whea-stress"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["gpu.fp32-fp64-integer"].Status);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["gpu.vram-bandwidth"].Status);
        Assert.True(byId["gpu.vram-bandwidth"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["gpu.pcie-transfer"].Status);
        Assert.True(byId["gpu.pcie-transfer"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["gpu.dispatch-jitter"].Status);
        Assert.True(byId["gpu.dispatch-jitter"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["gpu.raster-texture"].Status);
        Assert.True(byId["gpu.raster-texture"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["gpu.codec-throughput"].Status);
        Assert.True(byId["gpu.codec-throughput"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["storage.qd-ladder"].Status);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["storage.mixed-rw"].Status);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["storage.write-integrity"].Status);
        Assert.True(byId["storage.write-integrity"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["storage.flush-durability"].Status);
        Assert.True(byId["storage.flush-durability"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["storage.slc-sustained-write"].Status);
        Assert.True(byId["storage.slc-sustained-write"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["storage.iocp-engine"].Status);
        Assert.True(byId["storage.iocp-engine"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["storage.io-gpu-pipeline"].Status);
        Assert.True(byId["storage.io-gpu-pipeline"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["ux.network-stack-latency"].Status);
        Assert.True(byId["ux.network-stack-latency"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["ux.audio-buffer-glitch"].Status);
        Assert.True(byId["ux.audio-buffer-glitch"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["ux.present-frame-pacing"].Status);
        Assert.True(byId["ux.present-frame-pacing"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["gauntlet.power-state-latency"].Status);
        Assert.True(byId["gauntlet.power-state-latency"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["gauntlet.boost-recovery"].Status);
        Assert.True(byId["gauntlet.boost-recovery"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["gauntlet.throughput-degradation"].Status);
        Assert.True(byId["gauntlet.throughput-degradation"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["gauntlet.multi-domain"].Status);
        Assert.True(byId["gauntlet.multi-domain"].Runnable);
        Assert.Equal(DeepBenchTestStatus.Implemented, byId["ux.synthetic-workloads"].Status);
        Assert.True(byId["ux.synthetic-workloads"].Runnable);

        var confidence = byId["confidence.engine"];
        Assert.Equal(34, confidence.MatrixNumber);
        Assert.Equal(DeepBenchTestStatus.Implemented, confidence.Status);
        Assert.True(confidence.Runnable);
    }

    [Fact]
    public void Quick與Full都只選三十八個已接入測項()
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
        Assert.Empty(skipped);
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
