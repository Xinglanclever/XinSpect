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
        "memory.ecc-whea-stress",
        "gpu.fp32-fp64-integer",
        "gpu.vram-bandwidth",
        "gpu.pcie-transfer",
        "gpu.dispatch-jitter",
        "cpu.top-down",
        "storage.qd-ladder",
        "storage.mixed-rw",
        "storage.write-integrity",
        "storage.flush-durability",
        "storage.slc-sustained-write",
        "storage.iocp-engine",
        "ux.network-stack-latency",
        "ux.audio-buffer-glitch",
        "ux.present-frame-pacing",
        "gauntlet.boost-recovery",
        "gauntlet.throughput-degradation",
        "gauntlet.power-state-latency"
    ];

    public static DeepBenchPlan Plan(DeepBenchRunProfile profile, IReadOnlyList<DeepBenchCatalogEntry>? catalog = null)
    {
        _ = profile;
        IReadOnlyList<DeepBenchCatalogEntry> entries = catalog ?? DeepBenchCatalog.All;
        var byId = entries.ToDictionary(entry => entry.Id, entry => entry, StringComparer.Ordinal);
        string[] selected = Phase1Order.Where(byId.ContainsKey).Where(id => byId[id].Runnable).ToArray();
        var skipped = entries
            .Where(entry => !selected.Contains(entry.Id, StringComparer.Ordinal))
            .Select(entry => new DeepBenchSkippedEntry(
                entry.Id,
                entry.Runnable ? "已登記但不屬於 Phase 1 執行集" : $"Phase {entry.Phase} 尚未實作：{entry.StatusText}"))
            .ToArray();
        return new DeepBenchPlan(selected, skipped);
    }
}
