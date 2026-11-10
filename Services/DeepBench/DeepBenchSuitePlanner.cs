namespace XinSpect;

public static class DeepBenchSuitePlanner
{
    private static readonly string[] Phase1Order =
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
        "confidence.engine",
        "cpu.vector-width-throughput",
        "memory.cache-bandwidth-ladder",
        "memory.copy-kernel-bandwidth",
        "topology.tsc-cross-core-sync",
        "topology.core-uniformity",
        "memory.dram-pattern-integrity"
    ];

    public static DeepBenchPlan Plan(DeepBenchRunProfile profile, IReadOnlyList<DeepBenchCatalogEntry>? catalog = null)
        => Plan(profile, DeepBenchTargetCatalog.Default, catalog);

    public static DeepBenchPlan Plan(
        DeepBenchRunProfile profile,
        DeepBenchTargetOption target,
        IReadOnlyList<DeepBenchCatalogEntry>? catalog = null)
    {
        _ = profile;
        ArgumentNullException.ThrowIfNull(target);
        IReadOnlyList<DeepBenchCatalogEntry> entries = catalog ?? DeepBenchCatalog.All;
        var byId = entries.ToDictionary(entry => entry.Id, entry => entry, StringComparer.Ordinal);
        var targeted = target.TestIds.ToHashSet(StringComparer.Ordinal);
        string[] selected = Phase1Order.Where(targeted.Contains).Where(byId.ContainsKey)
            .Where(id => byId[id].Runnable).ToArray();
        var skipped = entries
            .Where(entry => !selected.Contains(entry.Id, StringComparer.Ordinal))
            .Select(entry => new DeepBenchSkippedEntry(
                entry.Id,
                !entry.Runnable
                    ? $"Phase {entry.Phase} 尚未實作：{entry.StatusText}"
                    : targeted.Contains(entry.Id)
                        ? "目標套件要求此項，但目錄未收錄於 Phase 1 執行集"
                    : $"不屬於「{target.Title}」目標套件"))
            .ToArray();
        return new DeepBenchPlan(selected, skipped);
    }
}
